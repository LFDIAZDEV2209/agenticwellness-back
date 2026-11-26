const { test, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

async function seed(request, data, age) {
  const reset = await request.post('/fixture/reset', { data: { postgres: true, age } });
  expect(reset.status(), await reset.text()).toBe(204);
  const session = await request.get('/fixture/session/app');
  expect(session.status()).toBe(200);
  const app = await session.json();
  const headers = { Authorization: 'Bearer ' + app.accessToken };
  const person = '/v1/people/' + app.patientId;
  const updated = await request.put(person, { headers, data });
  expect(updated.status(), await updated.text()).toBe(200);
  expect((await updated.json()).status).toMatchObject({ ok: true });
  return { app, headers, person };
}

async function atDay(request, day) {
  const instant = new Date(Date.UTC(2026, 0, day, 18)).toISOString();
  expect((await request.put('/fixture/clock', { data: { instant } })).status()).toBe(204);
}

async function readEngineDay(page, day) {
  await page.getByLabel('Día para energía').fill(String(day));
  const [response] = await Promise.all([
    page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/plan/' + day)),
    page.getByRole('button', { name: 'Leer energía' }).click(),
  ]);
  expect(response.status(), await response.text()).toBe(200);
  const result = await response.json();
  await expect(page.locator('#energy-result')).toContainText(`Día ${day} · ${result.kc} kcal`);
  return result;
}

function expectBalanced({ P, C, F, kc }, maxProtein, label) {
  expect(P, label + ' · tope de proteína').toBeLessThanOrEqual(maxProtein);
  expect(C, label + ' · piso de carbohidrato').toBeGreaterThanOrEqual(80);
  expect(F, label + ' · grasa no negativa').toBeGreaterThanOrEqual(0);
  expect(Math.abs(4 * P + 4 * C + 9 * F - kc) / kc, label + ' · tolerancia').toBeLessThanOrEqual(.03);
}

test.describe('KAN-127 · objetivos de nutrición del Motor 365', () => {
  test.skip(!process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requiere PostgreSQL efímero');

  for (const [goal, protein, restDay] of [['bajar', 160, 6], ['masa', 180, 4], ['def', 200, 7]]) {
    test(`aplica las reglas de ${goal} en entrenamiento y descanso`, async ({ page, request }) => {
      const { headers, person } = await seed(request,
        { w0: 100, h: 175, act: 3, goal, target: goal === 'masa' ? 105 : 95, pace: 'm' });
      await atDay(request, restDay);
      await page.goto('/fixture');
      const training = await readEngineDay(page, 1);
      expect(training).toMatchObject({ P: protein, water: 3500, train: true });
      expect(training.F).toBe(Math.round(training.kc * .24 / 9));
      expect(training.fiber).toBe(Math.max(25, Math.round(training.kc * .014)));
      const rest = await readEngineDay(page, restDay);
      expect(rest.train).toBe(false);
      expect(rest.F).toBe(Math.round(rest.kc * .28 / 9));
      const annual = await request.get(person + '/plan', { headers });
      expect(annual.status()).toBe(200);
      const { days } = await annual.json();
      expect(days).toHaveLength(365);
      for (const day of days) expectBalanced(day, 220, `${goal} día ${day.d}`);
    });
  }

  test('aplica 28 % en mantenimiento aunque el día sea de entrenamiento', async ({ page, request }) => {
    const { headers, person } = await seed(request, { w0: 80, goal: 'bajar', target: 79 });
    expect((await request.put(person + '/logs/1', {
      headers, data: { adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], w: 79, ex: 1 },
    })).status()).toBe(200);
    await atDay(request, 2);
    await page.goto('/fixture');
    const day = await readEngineDay(page, 2);
    expect(day).toMatchObject({ mode: 'maint', train: true });
    expect(day.F).toBe(Math.round(day.kc * .28 / 9));
  });

  test('prioriza el piso de carbohidrato sin publicar grasa negativa', async ({ page, request }) => {
    await page.goto('/fixture');
    // Perfiles sintéticos extremos admitidos por el contrato, no recomendaciones clínicas.
    await seed(request, { h: 100, w0: 150, target: 140, act: 1, goal: 'def', pace: 'max' }, 100);
    const reducedFat = await readEngineDay(page, 1);
    expect(reducedFat).toMatchObject({ C: 80, P: 300, fiber: 25, water: 5250 });
    expect(reducedFat.F).toBeLessThan(Math.round(reducedFat.kc * .24 / 9));
    expectBalanced(reducedFat, 330, 'grasa reducida');

    const { headers, person } = await seed(request, { h: 100, w0: 120, target: 110, act: 1, goal: 'def', pace: 'max' }, 100);
    const zeroFat = await readEngineDay(page, 1);
    expect(zeroFat).toMatchObject({ kc: 1230, P: 227, C: 80, F: 0 });
    expectBalanced(zeroFat, 264, 'presupuesto menor que proteína + carbohidrato');

    async function readAnnualFirst() {
      const annual = await request.get(person + '/plan', { headers });
      expect(annual.status(), await annual.text()).toBe(200);
      return (await annual.json()).days[0];
    }
    expect(await readAnnualFirst()).toMatchObject({ P: 227, C: 80, F: 0 });
    expect((await request.put('/fixture/kan127/legacy-snapshot')).status()).toBe(204);
    expect(await readAnnualFirst()).toMatchObject({ P: 227, C: 80, F: 0 });
  });

  test('redondea agua al múltiplo de 250 ml a ambos lados del umbral', async ({ page, request }) => {
    await page.goto('/fixture');
    for (const [w0, water] of [[82.14, 2750], [82.15, 3000]]) {
      await seed(request, { w0 });
      expect((await readEngineDay(page, 1)).water).toBe(water);
    }
  });

  test('el redondeo de gramos no supera 2.2 g/kg con proteína configurada al tope', async ({ page, request }) => {
    await seed(request, { w0: 80.25, goal: 'def' });
    const { accessToken } = await (await request.get('/fixture/session/erp-admin')).json();
    const headers = { Authorization: 'Bearer ' + accessToken };
    const path = '/v1/engine/params';
    const before = await request.get(path, { headers });
    expect(before.status(), await before.text()).toBe(200);
    const { params, params_version } = await before.json();
    const config = { pro: { def: 2.2 }, proCap: 2.2 };
    const updated = await request.put(path, {
      headers, data: { params: config, expectedVersion: params_version, reason: 'Fixture KAN-127' },
    });
    expect(updated.status(), await updated.text()).toBe(200);
    const version = (await updated.json()).params_version;
    try {
      await page.goto('/fixture');
      const day = await readEngineDay(page, 1);
      expect(day).toMatchObject({ P: 176, params_version: version });
      expectBalanced(day, 2.2 * 80.25, '80.25 kg × 2.2 g/kg = 176.55 g');
    } finally {
      const restored = await request.put(path, {
        headers, data: { params: { pro: { def: params.pro.def }, proCap: params.proCap },
          expectedVersion: version, reason: 'Restaurar parámetros de fixture KAN-127' },
      });
      expect(restored.status(), await restored.text()).toBe(200);
    }
  });

  test('la app y Perfil 360 muestran los objetivos del mismo día de servidor', async ({ browser, request }) => {
    test.skip(!process.env.COP_KAN130_APP_DIR || !process.env.COP_KAN130_ERP_DIR, 'Requiere ambos checkouts');
    test.setTimeout(100_000);
    const { app, headers, person } = await seed(request, { w0: 100, h: 175, act: 3, goal: 'masa', target: 105, pace: 'm' });
    const erp = await (await request.get('/fixture/session/erp')).json();
    const expected = await request.get(person + '/plan/1', { headers });
    expect(expected.status()).toBe(200);
    const { kc, P, C, F, fiber, water } = await expected.json();
    expect({ kc, P, C, F, fiber, water }).toEqual({ kc: 2990, P: 180, C: 388, F: 80, fiber: 42, water: 3500 });

    const appContext = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'es-CO' });
    const erpContext = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'es-CO' });
    try {
      await connectClients(appContext, app, erpContext, erp);
      const [appPage, erpPage] = await Promise.all([appContext.newPage(), erpContext.newPage()]);
      await Promise.all([
        appPage.goto('http://127.0.0.1:51362'),
        erpPage.goto('http://127.0.0.1:51363/patients/' + app.patientId + '/360'),
      ]);
      async function openNutrition() {
        await expect(appPage.getByText('Día 1 de 365', { exact: true })).toBeVisible({ timeout: 25_000 });
        await appPage.getByRole('button', { name: 'Ver mi plan de bienestar' }).click();
        await appPage.getByRole('button', { name: 'Nutrición', exact: true }).first().click();
        await expect(appPage.getByRole('heading', { name: 'Dieta de hoy' })).toBeVisible();
      }
      await openNutrition();
      const card = erpPage.getByRole('region', { name: 'Motor 365', exact: true });
      await expect(card.getByText('Día 1 de 365')).toBeVisible({ timeout: 25_000 });
      const num = n => new Intl.NumberFormat('es-CO').format(n);
      const macros = `${P} g proteína · ${C} g carbohidratos · ${F} g grasas`;
      await expect.soft(appPage.getByText(`Meta diaria: ${num(kc)} kcal`, { exact: true })).toBeVisible();
      await expect.soft(appPage.getByText(macros, { exact: true })).toBeVisible();
      await expect.soft(appPage.getByText(`Meta de fibra: ${fiber} g`, { exact: true })).toBeVisible();
      await expect.soft(appPage.getByText(`0 de ${num(water)} ml`, { exact: true })).toBeVisible();
      await expect.soft(card.getByText(`Meta diaria: ${num(kc)} kcal`, { exact: true })).toBeVisible();
      await expect.soft(card.getByText(macros, { exact: true })).toBeVisible();
      await expect.soft(card.getByText(`Meta de fibra: ${fiber} g`, { exact: true })).toBeVisible();
      await expect.soft(card.getByText(`Meta de agua: ${num(water)} ml`, { exact: true })).toBeVisible();
      await appPage.getByRole('button', { name: '+250 ml', exact: true }).click();
      await expect(appPage.getByText(`250 de ${num(water)} ml`, { exact: true })).toBeVisible();
      await appPage.reload();
      await openNutrition();
      await expect(appPage.getByText(`250 de ${num(water)} ml`, { exact: true })).toBeVisible();
    } finally {
      await Promise.all([appContext.close(), erpContext.close()]);
    }
  });
});
