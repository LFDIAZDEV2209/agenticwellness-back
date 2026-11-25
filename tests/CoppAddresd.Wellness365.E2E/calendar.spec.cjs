const { test, expect } = require('@playwright/test');

async function setup(request, input = {}) {
  expect((await request.post('/fixture/reset', { data: input })).ok()).toBeTruthy();
}

async function at(request, instant) {
  expect((await request.put('/fixture/clock', { data: { instant } })).ok()).toBeTruthy();
}

async function zone(request, timeZone) {
  expect((await request.put('/fixture/zone', { data: { timeZone } })).ok()).toBeTruthy();
}

async function read(page, day = '') {
  await page.getByLabel('Día del plan').fill(day);
  const [response] = await Promise.all([
    page.waitForResponse(r => r.url().includes('/fixture/plan')),
    page.getByRole('button', { name: 'Consultar' }).click(),
  ]);
  return { status: response.status(), body: await response.json() };
}

async function close(request, day, data = { adherence: .9, weight: 79 }) {
  return request.put('/fixture/logs/' + day, { data });
}

test('rechaza días inválidos y futuros, y conserva el motivo de bloqueo', async ({ page, request }) => {
  await setup(request);
  await page.goto('/fixture');
  for (const day of ['0', '366', '1.5', 'mañana']) {
    expect(await read(page, day)).toMatchObject({ status: 400, body: { error: 'bad_day' } });
  }
  expect(await read(page, '2')).toMatchObject({ status: 422, body: { error: 'future_day' } });
  const future = await close(request, '2');
  expect(future.status()).toBe(422);
  expect(await future.json()).toMatchObject({ error: 'future_day' });
  expect((await close(request, '1.5')).status()).toBe(400);
  await setup(request, { person: false });
  expect(await read(page)).toMatchObject({ status: 404, body: { error: 'person_not_found' } });
  expect((await close(request, '1')).status()).toBe(404);
  await expect(page.getByRole('status')).toHaveText('404 person_not_found');
  await setup(request, { block: 'terms' });
  expect(await read(page)).toMatchObject({ status: 409, body: { error: 'plan_locked', status: { state: 'locked', code: 'terms' } } });
  await expect(page.getByRole('status')).toHaveText('409 plan_locked terms');
});

test('real ignora inicio y modo del cliente y solo muestra pesajes registrados', async ({ page, request }) => {
  await setup(request, { startDate: '2025-01-01', real: false });
  await page.goto('/fixture');
  expect(await read(page)).toMatchObject({
    status: 200, body: {
      day: 1, today: 1, real: true, engine_ver: '5.2', params_version: 1, startDate: '2026-01-01', logStatus: 'open', measuredWeightKg: null,
    },
  });
  expect((await close(request, '1')).ok()).toBeTruthy();
  await at(request, '2026-01-03T18:00:00Z');
  expect(await read(page, '1')).toMatchObject({ body: { logStatus: 'recorded', measuredWeightKg: 79, streak: 1 } });
  expect(await read(page, '2')).toMatchObject({
    body: { logStatus: 'missing', adherence: .8, measuredWeightKg: null, streak: 0 },
  });
  expect(await read(page)).toMatchObject({ body: { logStatus: 'open', measuredWeightKg: null, streak: 0 } });
  await expect(page.getByText('Sin pesaje')).toBeVisible();
  await setup(request, { demoHost: true, startDate: '2025-12-28' });
  expect(await read(page, '1')).toMatchObject({
    body: { real: false, startDate: '2025-12-28', today: 5, logStatus: 'simulated' },
  });
});

test('el día y los registros sobreviven cambios de zona en ambas direcciones', async ({ page, request }) => {
  await setup(request);
  await page.goto('/fixture');
  await at(request, '2026-01-03T03:00:00Z');
  expect(await read(page)).toMatchObject({ body: { today: 2 } });
  expect((await close(request, '2')).ok()).toBeTruthy();
  await zone(request, 'Asia/Tokyo');
  expect(await read(page)).toMatchObject({ body: { today: 2, measuredWeightKg: 79 } });
  await at(request, '2026-01-03T15:00:00Z');
  expect(await read(page)).toMatchObject({ body: { today: 3, measuredWeightKg: null, streak: 1 } });
  await zone(request, 'Pacific/Honolulu');
  expect(await read(page)).toMatchObject({ body: { today: 3, measuredWeightKg: null, streak: 1 } });
  await expect(page.getByRole('status')).toHaveText('Día 3 · active');
  expect(await read(page, '2')).toMatchObject({ body: { day: 2, measuredWeightKg: 79 } });
  await at(request, '2026-01-04T10:00:00Z');
  expect(await read(page)).toMatchObject({ body: { today: 4, startDate: '2026-01-01' } });
});

test('avanza al siguiente día civil aunque haya 23 horas entre medianoches', async ({ page, request }) => {
  await setup(request, { instant: '2026-03-08T05:00:00Z', timeZone: 'America/New_York' });
  await page.goto('/fixture');
  await at(request, '2026-03-09T03:59:59Z');
  expect(await read(page)).toMatchObject({ body: { today: 1 } });
  await at(request, '2026-03-09T04:00:00Z');
  expect(await read(page)).toMatchObject({ body: { today: 2 } });
});

test('una proyección no activa mantenimiento y una pausa oculta la sesión', async ({ page, request }) => {
  await setup(request, { target: 79.9 });
  await page.goto('/fixture');
  await at(request, '2026-01-10T18:00:00Z');
  expect(await read(page)).toMatchObject({ body: { status: { state: 'active' }, measuredWeightKg: null } });
  await setup(request, { target: 79.9 });
  await at(request, '2026-01-02T18:00:00Z');
  expect(await read(page)).toMatchObject({ body: { status: { state: 'active' } } });
  const recorded = await close(request, '1', { adherence: .9, weight: 79, pain: 8 });
  expect(recorded.ok()).toBeTruthy();
  expect(await read(page)).toMatchObject({ body: { status: { state: 'paused' }, exercise: null } });
  await expect(page.getByRole('status')).toHaveText('Día 2 · paused');
  await at(request, '2026-01-03T18:00:00Z');
  expect(await read(page)).toMatchObject({ body: { status: { state: 'paused' } } });
  await at(request, '2026-01-04T18:00:00Z');
  expect(await read(page)).toMatchObject({ body: { status: { state: 'maint' } } });
});

test('los cierres y el ancla se recuperan de PostgreSQL sin depender de memoria', async ({ page, request }) => {
  test.skip(!process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requiere el PostgreSQL efímero de KAN-131');
  await setup(request, { postgres: true, timeZone: 'Asia/Tokyo', instant: '2026-01-01T02:00:00Z' });
  await page.goto('/fixture');
  await at(request, '2026-01-02T02:00:00Z');
  expect((await close(request, '2')).status()).toBe(204);
  await zone(request, 'Pacific/Honolulu');
  expect((await request.post('/fixture/clear-memory')).ok()).toBeTruthy();
  expect(await read(page)).toMatchObject({ body: { day: 2, today: 2, measuredWeightKg: 79 } });
  await at(request, '2026-01-02T10:00:00Z');
  expect(await read(page)).toMatchObject({ body: { day: 3, today: 3, logStatus: 'open', streak: 1 } });
  const writes = await Promise.all([
    close(request, '3', { adherence: .9, weight: 78 }),
    close(request, '3', { adherence: .9, weight: 78 }),
  ]);
  expect(writes.map(result => result.status())).toEqual([204, 204]);
  expect(await read(page, '2')).toMatchObject({ body: { day: 2, measuredWeightKg: 79 } });
  expect(await read(page, '3')).toMatchObject({ body: { day: 3, measuredWeightKg: 78, streak: 2 } });
  expect((await close(request, '4')).status()).toBe(422);
  expect((await close(request, '3', { adherence: 1.2, weight: 78 })).status()).toBe(400);
  expect(await (await request.get('/fixture/persistence-stats')).json()).toEqual({
    dates: ['2026-01-02', '2026-01-03'], events: 3, planVersion: 4,
  });
});
