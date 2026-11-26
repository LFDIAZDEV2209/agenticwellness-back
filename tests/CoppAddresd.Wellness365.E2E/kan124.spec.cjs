const { test, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');
const people = require('../CoppAddresd.UnitTests/Wellness365/Fixtures/people.json');
const { foods } = require('../../src/CoppAddresd.Domain/Wellness365/Reference/food-and-templates.json');

const metrics = ['kcal', 'protein', 'carbs', 'fat'];
const planUrl = '/api/v1/people/me/plan';

async function checked(response, status = 200) {
  expect(response.status(), await response.text()).toBe(status);
  return status === 204 ? undefined : response.json();
}

async function seed(request, { age, fit, sex, h, w0, waist0, target, act, goal, pace, veg, nolac }) {
  await checked(await request.post('/fixture/reset', { data: { postgres: true, age, fit } }), 204);
  const app = await checked(await request.get('/fixture/session/app'));
  const headers = { Authorization: 'Bearer ' + app.accessToken };
  const person = '/v1/people/' + app.patientId;
  const profile = await checked(await request.put(person, {
    headers, data: { sex, h, w0, waist0, target, act, goal, pace, veg, nolac },
  }));
  expect(profile.status.ok).toBe(true);
  return {
    app,
    day: d => request.get(planUrl + '/' + d + '?includeMenu=true', { headers }).then(r => checked(r)),
    detail: d => request.get(person + '/plan/' + d, { headers }).then(r => checked(r)),
    close: (d, adh) => request.put(person + '/logs/' + d, {
      headers, data: { adh, sleep: 7, energy: 4, pain: 0, sym: [], ex: 1 },
    }).then(r => checked(r)),
  };
}

async function atDay(request, d) {
  const instant = new Date(Date.UTC(2026, 0, d, 18)).toISOString();
  await checked(await request.put('/fixture/clock', { data: { instant } }), 204);
}

function assertDish(dish, profile) {
  expect(dish.items.length).toBeGreaterThan(0);
  for (const { food, grams } of dish.items) {
    expect(foods[food], food).toBeDefined();
    expect(Number.isFinite(grams) && grams > 0, food + ' gramos').toBe(true);
    if (profile.veg) expect(['meat', 'fish']).not.toContain(foods[food].group);
    if (profile.nolac) expect(foods[food].group).not.toBe('dairy');
  }
  for (const key of metrics) {
    const total = dish.items.reduce((sum, item) => sum + foods[item.food][key] * item.grams / 100, 0);
    expect(dish.totals[key], key).toBeCloseTo(total, 4);
  }
}

test.describe('KAN-124 · menú del Motor 365', () => {
  for (const profile of [
    ...people,
    { ...people[2], id: 'solo vegetariana', nolac: 0 },
    { ...people[2], id: 'solo sin lácteos', veg: 0 },
  ]) {
    test(profile.id + ' · cuatro comidas y sustituciones en los cinco días', async ({ request }) => {
      const { day, detail } = await seed(request, profile);
      await atDay(request, 365);
      for (const d of [1, 30, 90, 180, 365]) {
        const response = await day(d);
        expect(response.mealPlan, 'El calendario debe publicar el menú del motor').toBeDefined();
        const { meals, totals, vegetarian, dairyFree, simplified } = response.mealPlan;
        expect(response.day).toBe(d);
        expect({ vegetarian, dairyFree, simplified }).toEqual({
          vegetarian: !!profile.veg, dairyFree: !!profile.nolac, simplified: false,
        });
        expect(meals.map(m => m.name.es)).toEqual(['Desayuno', 'Almuerzo', 'Merienda', 'Cena']);
        for (const [i, share] of [.25, .35, .15, .25].entries())
          expect(meals[i].targetKcal).toBeCloseTo(response.nutrition.kcal * share, 4);
        expect(Math.abs(totals.kcal - response.nutrition.kcal) / response.nutrition.kcal).toBeLessThanOrEqual(.08);
        const canonical = await detail(d);
        expect(canonical.kc).toBe(response.nutrition.kcal);
        expect(totals.kcal).toBeCloseTo(canonical.totals.k, 4);
        expect(meals.map(m => m.options[0].items.map(({ food, grams }) => ({ food, grams }))))
          .toEqual(canonical.menu.map(m => m.items.map(({ food, grams }) => ({ food, grams }))));
        for (const key of metrics)
          expect(totals[key]).toBeCloseTo(meals.reduce((sum, m) => sum + m.options[0].totals[key], 0), 4);
        let alternatives = 0;
        for (const { options } of meals) {
          const [first, ...rest] = options;
          assertDish(first, profile);
          const signatures = options.map(dish => dish.items.map(i => i.food).sort().join(','));
          expect(new Set(signatures).size).toBe(options.length);
          for (const dish of rest) {
            alternatives++;
            assertDish(dish, profile);
            for (const key of metrics) {
              const swapped = totals[key] - first.totals[key] + dish.totals[key];
              expect(Math.abs(swapped - totals[key]), key).toBeLessThanOrEqual(Math.min(totals[key], swapped) * .08);
              if (key === 'kcal')
                expect(Math.abs(swapped - response.nutrition.kcal) / response.nutrition.kcal).toBeLessThanOrEqual(.08);
            }
          }
        }
        expect(alternatives).toBeGreaterThan(0);
      }
      expect((await day(30)).mealPlan).toEqual((await day(30)).mealPlan);
    });
  }

  test('el menú conserva la autenticación, el calendario y la respuesta sin expansión', async ({ request }) => {
    const { app } = await seed(request, people[2]);
    const headers = { Authorization: 'Bearer ' + app.accessToken };
    expect((await request.get(planUrl + '?includeMenu=true')).status()).toBe(401);
    const future = await checked(await request.get(planUrl + '/2?includeMenu=true', { headers }), 422);
    expect(future).not.toHaveProperty('mealPlan');
    expect(await checked(await request.get(planUrl, { headers }))).not.toHaveProperty('mealPlan');
    const expanded = await checked(await request.get(planUrl + '?includeMenu=true&includeSync=true', { headers }));
    expect(expanded.mealPlan.meals).toHaveLength(4);
    expect(expanded.sync).toMatchObject({ personId: app.patientId, throughDay: 1 });
  });

  test('simplifica solo después de dos cierres consecutivos por debajo de 60 %', async ({ request }) => {
    const { day, close } = await seed(request, people[2]);
    await atDay(request, 6);
    await close(1, .59);
    await close(2, .6);
    expect((await day(3)).mealPlan.simplified).toBe(false);
    await close(3, .59);
    expect((await day(4)).mealPlan.simplified).toBe(false);
    await close(4, .59);
    const fifth = await day(5);
    expect(fifth.mealPlan.simplified).toBe(true);
    expect(fifth.mealPlan.meals).toHaveLength(4);
    for (const meal of fifth.mealPlan.meals) {
      expect(meal.options.length).toBeLessThanOrEqual(2);
      meal.options.forEach(dish => assertDish(dish, people[2]));
    }
    await close(5, .6);
    expect((await day(6)).mealPlan.simplified).toBe(false);
  });

  test.describe('app en Chromium', () => {
    test.skip(!process.env.COP_KAN130_APP_DIR, 'La prueba de navegador requiere el checkout de la app');
    test('la app muestra el menú del servidor y conserva el cambio de plato al volver', async ({ browser, request }) => {
      test.setTimeout(100_000);
      const { app, day, close } = await seed(request, people[2]);
      const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'es-CO' });
      try {
        await connectClients(context, app);
        const page = await context.newPage();
        async function open() {
          await expect(page.getByText('Día 1 de 365', { exact: true })).toBeVisible({ timeout: 25_000 });
          await page.getByRole('region', { name: 'Actividades del día' })
            .getByRole('button', { name: /Registra tus comidas de hoy/ }).click();
          await expect(page.getByRole('heading', { name: 'Dieta de hoy' })).toBeVisible();
        }
        await page.goto('http://127.0.0.1:51362');
        await open();
        await expect(page.getByText('Vegetariana', { exact: true })).toBeVisible();
        await expect(page.getByText('Sin lácteos', { exact: true })).toBeVisible();
        const expected = await day(1);
        const { meals } = expected.mealPlan;
        const number = n => new Intl.NumberFormat('es-CO', { maximumFractionDigits: 1 }).format(n);
        const target = page.getByText('Meta diaria: ' + number(expected.nutrition.kcal) + ' kcal', { exact: true });
        const rows = meal => page.getByRole('region', { name: meal.name.es }).getByRole('listitem');
        const ingredients = dish => dish.items.map(item => item.name.es + number(item.grams) + ' g');
        for (const meal of meals) await expect(rows(meal)).toHaveText(ingredients(meal.options[0]));
        const swap = meals.find(meal => meal.options.length > 1);
        const change = page.getByRole('region', { name: swap.name.es }).getByRole('button', { name: 'Cambiar plato' });
        await change.click();
        await expect(rows(swap)).toHaveText(ingredients(swap.options[1]));
        await expect(target).toBeVisible();
        for (const meal of meals.filter(m => m !== swap)) await expect(rows(meal)).toHaveText(ingredients(meal.options[0]));
        await page.reload();
        await open();
        await expect(rows(swap)).toHaveText(ingredients(swap.options[1]));
        await expect(target).toBeVisible();

        await atDay(request, 3);
        await close(1, .59);
        await close(2, .59);
        await page.evaluate(() => window.dispatchEvent(new Event('focus')));
        await expect(page.getByRole('button', { name: 'Modo simplificado', pressed: true })).toBeVisible();
        await expect(page.getByText('Día 3', { exact: true })).toBeVisible();
        for (const meal of (await day(3)).mealPlan.meals) {
          const displayed = await rows(meal).allTextContents();
          expect(meal.options.map(ingredients)).toContainEqual(displayed);
        }
      } finally {
        await context.close();
      }
    });
  });
});
