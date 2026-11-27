const { expect, test } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const api = 'http://127.0.0.1:51350';
const erpOrigin = 'http://127.0.0.1:51351';
const appOrigin = 'http://127.0.0.1:51352';
const todayInstant = '2026-01-15T03:00:00Z';
const headers = session => ({ Authorization: 'Bearer ' + session.accessToken });

async function checked(reply, status = 200) {
  expect(reply.status(), await reply.text()).toBe(status);
  return status === 200 ? reply.json() : undefined;
}

async function openProfile(browser, request, scenario = {}, locale = 'es-CO') {
  await checked(await request.post('/fixture/reset', {
    data: { postgres: true, instant: '2026-01-01T18:00:00Z', ...scenario },
  }), 204);
  await checked(await request.put('/fixture/clock', { data: { instant: todayInstant } }), 204);
  if (scenario.block === 'terms') await checked(await request.post('/fixture/kan128/withdraw-consent'), 204);
  const app = await checked(await request.get('/fixture/session/app'));
  const erp = await checked(await request.get('/fixture/session/erp'));
  const context = await browser.newContext({ locale, timezoneId: 'UTC', viewport: { width: 1440, height: 900 } });
  await connectClients(context, erp, null, null, { api, appOrigin: erpOrigin });
  const page = await context.newPage();
  return { context, page, app, erp, path: '/patients/' + app.patientId + '/360' };
}

test('reúne el plan, los registros y los motivos en Perfil 360 en el día de la app', async ({ browser, request }) => {
  const { context, page, app, erp, path } = await openProfile(browser, request);
  const appContext = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'es-CO', timezoneId: 'UTC' });
  try {
    await connectClients(appContext, app, null, null, { api, appOrigin, instant: todayInstant });
    const appPage = await appContext.newPage();
    await appPage.clock.setFixedTime(new Date(todayInstant));
    const history = Object.fromEntries([1, 2, 3, 8, 9, 10, 14].map(day => [day, {
      adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], w: 80, waist: 85, ex: 1,
    }]));
    await checked(await request.put('/v1/people/' + app.patientId, { headers: headers(app), data: { log: history } }));
    const appDay = await checked(await request.get('/api/v1/people/me/plan?includeMenu=true', { headers: headers(app) }));
    expect(appDay.today).toBe(14); // Son las 22:00 del día anterior en Bogotá, pese al UTC del navegador.
    await Promise.all([page.goto(erpOrigin + path), appPage.goto(appOrigin)]);
    await expect(appPage.getByText(`Día ${appDay.today} de 365`, { exact: true })).toBeVisible({ timeout: 25_000 });
    const card = page.getByRole('region', { name: 'Motor 365', exact: true });
    await expect(card.getByText(`Día ${appDay.today} de 365`, { exact: true })).toBeVisible();
    await expect(card.getByText('Objetivo: Pérdida de peso')).toBeVisible();
    await expect(card.getByRole('heading', { name: 'Ejercicio de hoy' })).toBeVisible();
    await expect(card.getByRole('region', { name: 'Plan semanal del Motor 365' }).getByRole('listitem')).toHaveCount(7);
    const menu = card.getByRole('region', { name: 'Menú de hoy' });
    for (const meal of appDay.mealPlan.meals) {
      await expect(menu.getByText(meal.name.es, { exact: true })).toBeVisible();
    }
    const followUp = card.getByRole('region', { name: 'Seguimiento del Motor 365' });
    await expect(followUp.getByText(/90\s?%/)).toBeVisible();
    await expect(followUp.getByText(/85\s?cm/)).toBeVisible();
    await expect(followUp.getByText('Ejercicio registrado')).toBeVisible();
    await expect(followUp.getByText('7 de 14 días registrados')).toBeVisible();
    const events = await checked(await request.get('/api/v1/people/' + app.patientId + '/weekly-events', { headers: headers(erp) }));
    const adjustments = card.getByRole('region', { name: 'Ajustes semanales' });
    await expect(adjustments.getByText('−100 kcal', { exact: true })).toBeVisible();
    await expect(adjustments.getByText(events[0].es, { exact: true })).toBeVisible();

    const overviewPath = '/api/v1/people/' + app.patientId + '/plan/overview';
    const overview = await checked(await request.get(overviewPath, { headers: headers(erp) }));
    expect(overview).toMatchObject({ today: appDay.today, todayLocalDate: appDay.todayLocalDate,
      nutrition: appDay.nutrition, exercise: appDay.exercise, mealPlan: appDay.mealPlan });
    for (const [audience, status] of [['app', 403], ['erp-unassigned', 404], ['erp-no-permission', 403]]) {
      const credential = await checked(await request.get('/fixture/session/' + audience));
      expect((await request.get(overviewPath, { headers: headers(credential) })).status()).toBe(status);
    }
    await checked(await request.put('/fixture/clock', { data: { instant: '2026-01-15T05:00:00Z' } }), 204);
    await page.getByRole('button', { name: 'Actualizar' }).click();
    await expect(card.getByText('Día 15 de 365', { exact: true })).toBeVisible();
    const week = card.getByRole('region', { name: 'Plan semanal del Motor 365' });
    await expect(week.getByText('Programado', { exact: true })).toHaveCount(6);
    expect(await week.locator('[aria-current="date"]').innerText()).toContain('Día 15');
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await card.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    await card.getByRole('button', { name: 'Abrir calendario de 365 días' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('tab', { name: 'Año 365' })).toBeFocused();
    const calendar = page.getByRole('region', { name: 'Año 365', exact: true });
    await expect(calendar.getByRole('heading', { name: 'Calendario del programa' })).toBeVisible();
    await expect(calendar.getByText('Días con registro: 7', { exact: true })).toBeVisible();
  } finally {
    await Promise.all([context.unrouteAll({ behavior: 'ignoreErrors' }), appContext.unrouteAll({ behavior: 'ignoreErrors' })]);
    await Promise.all([context.close(), appContext.close()]);
  }
});

test('el motivo del bloqueo aparece sin mostrar el plan ni los registros', async ({ browser, request }) => {
  for (const [block, reason] of [
    ['doctor', 'El formulario está pendiente de revisión.'],
    ['unanswered', 'Faltan respuestas en el formulario.'],
    ['terms', /Faltan los consentimientos vigentes|Falta confirmar los términos vigentes/],
    ['membership_inactive', 'No hay una membresía activa.'],
  ]) {
    const { context, page, app, path } = await openProfile(browser, request, { block });
    try {
      const appDay = await request.get('/api/v1/people/me/plan', { headers: headers(app) });
      expect(appDay.status()).toBe(409);
      await page.goto(erpOrigin + path);
      const card = page.getByRole('region', { name: 'Motor 365', exact: true });
      await expect(card.getByText('Plan bloqueado', { exact: true })).toBeVisible();
      await expect(card.getByText(reason)).toBeVisible();
      await expect(card.getByRole('region', { name: 'Menú de hoy' })).toHaveCount(0);
      await expect(card.getByRole('region', { name: 'Seguimiento del Motor 365' })).toHaveCount(0);
    } finally {
      await context.unrouteAll({ behavior: 'ignoreErrors' });
      await context.close();
    }
  }
});
