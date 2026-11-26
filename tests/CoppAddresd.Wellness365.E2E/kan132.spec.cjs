const { test, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const clock = day => new Date(Date.UTC(2026, 0, day, 18));

test('el cierre confirmado ajusta el día siguiente en otro dispositivo', async ({ browser, request }) => {
  test.setTimeout(90_000);
  expect((await request.post('/fixture/reset', { data: { postgres: true } })).status()).toBe(204);
  const app = await (await request.get('/fixture/session/app')).json();
  const headers = { Authorization: 'Bearer ' + app.accessToken };
  const person = '/v1/people/' + app.patientId;
  const contexts = [];
  async function open(day, language = 'es') {
    expect((await request.put('/fixture/clock', { data: { instant: clock(day).toISOString() } })).status()).toBe(204);
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: language });
    contexts.push(context);
    await connectClients(context, app, undefined, undefined, { language, instant: clock(day).toISOString() });
    const page = await context.newPage();
    await page.clock.setFixedTime(clock(day));
    page.setDefaultTimeout(15_000);
    await page.goto('http://127.0.0.1:51362');
    await expect(page.getByText(language === 'en' ? `Day ${day} of 365` : `Día ${day} de 365`, { exact: true }))
      .toBeVisible({ timeout: 25_000 });
    return page;
  }
  try {
    const page = await open(1);
    await expect(page.getByRole('region', { name: 'Por qué cambió tu plan' })).toHaveCount(0);
    await page.getByRole('button', { name: 'Ver mi plan de bienestar' }).press('Enter');
    const plan = page.getByRole('region', { name: 'Tu Plan de Bienestar' });
    await plan.locator('ion-button').filter({ hasText: /^Bienestar$/ }).locator('button').press('Enter');
    for (const [name, value] of Object.entries({ adherence: '90', sleepHours: '7', energy: '4', pain: '7' }))
      await page.locator('ion-input[name="' + name + '"] input').fill(value);
    await page.getByText('Insomnio', { exact: true }).click();
    await page.getByText('No', { exact: true }).click();
    const closed = page.waitForResponse(res => res.request().method() === 'PUT' &&
      new URL(res.url()).pathname.endsWith('/logs/1'));
    await page.getByRole('button', { name: 'Cerrar el día', exact: true }).click();
    expect((await closed).status()).toBe(204);

    const next = await open(2);
    const reasons = next.getByRole('region', { name: 'Por qué cambió tu plan' });
    await expect(reasons).toContainText('Por el dolor que registraste, el ejercicio queda en pausa dos días.');
    await expect(reasons).toContainText('Si te costó dormir, elige una cena más temprana y ligera.');
    const calendar = await request.get('/api/v1/people/me/plan?includeMenu=true', { headers });
    const canonical = await request.get(person + '/plan/2', { headers });
    expect(calendar.status()).toBe(200);
    expect(canonical.status()).toBe(200);
    const day = await calendar.json();
    const detail = await canonical.json();
    expect(day).toMatchObject({ status: { state: 'paused' }, exercise: null, lighterDinner: true });
    expect(detail).toMatchObject({ pause: true, why: day.why, lighterDinner: true });
    expect(day.mealPlan.meals.find(meal => meal.id === 'dinner').targetKcal).toBeCloseTo(day.nutrition.kcal * .20);
    await next.getByRole('region', { name: 'Actividades del día' })
      .getByRole('button', { name: /Registra tus comidas de hoy/ }).click();
    const lighter = next.getByRole('region', { name: 'Cena' }).getByText('Si te costó dormir, elige una cena más temprana y ligera.', { exact: true });
    await expect(lighter).toBeVisible();

    const third = await open(3, 'en');
    await expect(third.getByRole('region', { name: 'Why your plan changed' }))
      .toContainText('Your workout is still on hold because of the pain you logged.');
    await third.getByRole('button', { name: /Exercise.*Today.*workout/i }).click();
    await expect(third.getByRole('heading', { name: 'Exercise' })).toBeVisible();
    await expect(third.getByRole('status').filter({ hasText: 'Plan paused' })).toBeVisible();

    const fourth = await open(4);
    await expect(fourth.getByRole('region', { name: 'Por qué cambió tu plan' })).toHaveCount(0);
    await expect(fourth.getByText('Plan activo', { exact: true })).toBeVisible();
  } finally {
    await Promise.all(contexts.map(context => context.close()));
  }
});
