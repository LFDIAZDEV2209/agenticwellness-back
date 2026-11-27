const { test, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const api = 'http://127.0.0.1:51391';
const erpOrigin = 'http://127.0.0.1:51393';
const log = (w, fields = {}) => ({ adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], w, ex: 1, ...fields });
const entries = (days, weight, fields) => Object.fromEntries(days.map(day => [day, log(weight, fields)]));
async function checked(response, code = 200) {
  expect(response.status(), await response.text()).toBe(code);
  return code === 204 ? undefined : response.json();
}
async function session(request, audience) {
  return checked(await request.get('/fixture/session/' + audience));
}
const headers = session => ({ Authorization: 'Bearer ' + session.accessToken });
async function moveTo(request, day) {
  await checked(await request.put('/fixture/clock', {
    data: { instant: new Date(Date.UTC(2026, 0, day, 18)).toISOString() },
  }), 204);
}
async function setup(request, scenario = {}) {
  await checked(await request.post('/fixture/reset', { data: { postgres: true, ...scenario } }), 204);
  const app = await session(request, 'app');
  const path = '/v1/people/' + app.patientId;
  return {
    app,
    save: async logs => checked(await request.put(path, { headers: headers(app), data: { log: logs } })),
    close: async (day, data) => checked(await request.put(path + '/logs/' + day, { headers: headers(app), data })),
    events: async () => checked(await request.get(path + '/events', { headers: headers(app) })),
    day: async day => checked(await request.get(path + '/plan/' + day, { headers: headers(app) })),
  };
}
const weekly = events => events.filter(event => ['sin', 'ajuste'].includes(event.k));

test('aplica los umbrales de cintura medidos y no publica decisiones futuras', async ({ request }) => {
  const person = await setup(request);
  await moveTo(request, 14);
  await person.save({ ...entries([2, 4, 7, 8, 10, 12], 80) });
  expect(weekly(await person.events())).toEqual([]);
  await person.close(14, log(80, { waist: 84.2 }));
  const result = weekly(await person.events());
  expect(result).toEqual([expect.objectContaining({ d: 14, k: 'sin', delta: 0, rate: 0,
    es: expect.stringContaining('cintura'), en: expect.stringMatching(/waist/i) })]);
  await moveTo(request, 15);
  expect((await person.day(15)).adjK).toBe(0);
  expect(weekly(await person.events())).toEqual(result);
  await person.close(14, log(80, { waist: 84.21 }));
  expect(weekly(await person.events())).toEqual([expect.objectContaining({ d: 14, delta: -100 })]);
  expect((await person.day(15)).adjK).toBe(-100);
  const erp = await session(request, 'erp');
  const year = await checked(await request.get(
    '/api/v1/program/erp/patients/' + person.app.patientId + '/year-365?month=2026-01',
    { headers: headers(erp) },
  ));
  expect(year.report.events.filter(event => event.day === 14)).toEqual([
    expect.objectContaining({ kind: 'ajuste' }),
  ]);
});

test('masa respeta adherencia exacta, hambre de la semana y pausa', async ({ request }) => {
  const person = await setup(request, { goal: 1 });
  await moveTo(request, 14);
  await person.save({ ...entries([1, 2, 3], 80), ...entries([8, 9, 10, 11, 12, 13], 80, { adh: .69 }) });
  await person.close(14, log(80, { adh: .69, waist: 86.2 }));
  expect(weekly(await person.events())[0]).toMatchObject({ delta: 0, es: expect.stringContaining('Adherencia') });
  await person.save(entries([8, 9, 10, 11, 12, 13, 14], 80, { adh: .7, waist: 86.2 }));
  expect(weekly(await person.events())[0]).toMatchObject({ delta: -100, es: expect.stringContaining('Cintura') });
  await person.save({ 13: log(80, { adh: .7, sym: ['hambre'] }),
    14: log(80, { adh: .7, waist: 86.2, sym: ['hambre'] }) });
  expect(weekly(await person.events())[0]).toMatchObject({ delta: 0, es: expect.stringContaining('Hambre') });

  // La semana 13 de masa es de pausa, incluso con un ritmo fuera de banda.
  await moveTo(request, 91);
  await person.save({ ...entries([78, 79, 80], 80), ...entries([85, 86, 87, 91], 80) });
  expect(weekly(await person.events()).find(event => event.d === 91)).toMatchObject({
    delta: 0, es: expect.stringContaining('pausa'),
  });
});

test('conserva la protección diaria de KAN-132 al evaluar el cierre semanal', async ({ request }) => {
  const person = await setup(request);
  await moveTo(request, 14);
  await person.save(entries([1, 2, 3, 8, 9, 10], 80));
  await person.close(14, log(80));
  expect(weekly(await person.events())[0]).toMatchObject({ delta: -100 });
  for (const symptom of ['hambre', 'mareo']) {
    await person.close(14, log(80, { sym: [symptom] }));
    expect(weekly(await person.events())[0]).toMatchObject({
      delta: 0, es: expect.stringContaining('cierre del día'),
    });
  }
  await moveTo(request, 15);
  expect((await person.day(15)).adjK).toBe(0);
});

test('exige pesajes de ambas semanas y conserva el tope en los días siguientes', async ({ request }) => {
  const person = await setup(request);
  await moveTo(request, 35);
  await person.save({ ...entries([6, 7], 80), ...entries([8, 9, 10, 14], 80),
    ...entries([15, 16, 17, 21, 22, 23, 24, 28, 29, 30, 31, 35], 80) });
  const events = weekly(await person.events());
  expect(events.map(({ d, delta, rate }) => [d, delta, rate])).toEqual([
    [14, 0, null], [21, -100, 0], [28, -100, 0], [35, 0, 0],
  ]);
  expect(events[3].en).toContain('cap');
  await moveTo(request, 36);
  expect((await person.day(36)).adjK).toBe(-200);
});

test('compara las bandas de bajar, definir, masa y mantener antes de redondear', async ({ request }) => {
  const cases = [
    [{}, [[99.75, 0], [99.76, -100], [99.25, 0], [99.24, 100]]],
    [{ goal: 2 }, [[99.80, 0], [99.81, -100], [99.50, 0], [99.49, 100]]],
    [{ goal: 1, target: 120 }, [[100.10, 0], [100.09, 100], [100.35, 0], [100.36, -100]]],
    [{ target: 80 }, [[100.25, 0], [100.26, -100], [99.75, 0], [99.74, 100]]],
  ];
  for (const [profile, bands] of cases) {
    const person = await setup(request, profile);
    await moveTo(request, 14);
    await person.save(entries([3, 4, 5], 100));
    for (const [weight, delta] of bands) {
      await person.save(entries([8, 10, 12, 14], weight));
      expect(weekly(await person.events())).toEqual([
        expect.objectContaining({ d: 14, delta, rate: Number((weight - 100).toFixed(2)) }),
      ]);
    }
  }
});

test.describe('ERP en Chromium', () => {
  test.skip(!process.env.COP_KAN130_ERP_DIR, 'Requiere el checkout de ERP');

  test('Perfil 360 muestra el cierre semanal real, el reintento y la semana sin datos', async ({ browser, request }) => {
    test.setTimeout(90_000);
    const person = await setup(request);
    const erp = await session(request, 'erp');
    await moveTo(request, 14);
    await person.save({ ...entries([1, 2, 3, 8, 9, 10], 80) });
    const context = await browser.newContext({ viewport: { width: 1024, height: 800 }, locale: 'es-CO' });
    try {
      await connectClients(context, erp, null, null, { api, appOrigin: erpOrigin });
      await context.addInitScript(() => localStorage.setItem('copp_lang', 'es'));
      const page = await context.newPage();
      await page.goto(erpOrigin + '/patients/' + person.app.patientId + '/360');
      const card = page.getByRole('region', { name: 'Motor 365', exact: true });
      const history = card.getByRole('region', { name: 'Ajustes semanales' });
      await expect(history.getByText('Aún no hay decisiones semanales.')).toBeVisible({ timeout: 30_000 });
      const first = await person.close(14, log(80));
      expect(await person.close(14, log(80))).toEqual(first);
      await page.getByRole('button', { name: 'Actualizar' }).click();
      await expect(history.getByRole('listitem')).toHaveCount(1);
      await expect(history.getByText('−100 kcal', { exact: true })).toBeVisible();
      await moveTo(request, 22);
      await page.reload();
      await expect(history.getByRole('listitem')).toHaveCount(2);
      await expect(history.getByText(/pesajes insuficientes/i)).toBeVisible();
      const persisted = await checked(await request.get('/fixture/weekly-events'));
      expect(persisted).toEqual([
        expect.objectContaining({ d: 14, k: 'ajuste', delta: -100, rate: 0, planVersion: first.ver }),
        expect.objectContaining({ d: 21, k: 'sin', delta: 0, rate: null, planVersion: first.ver }),
      ]);
      const path = '/api/v1/people/' + person.app.patientId + '/weekly-events';
      for (const [audience, status] of [['erp-unassigned', 404], ['erp-no-permission', 403]]) {
        const credential = await session(request, audience);
        expect((await request.get(path, { headers: headers(credential) })).status()).toBe(status);
      }
    } finally {
      await context.close();
    }
  });
});
