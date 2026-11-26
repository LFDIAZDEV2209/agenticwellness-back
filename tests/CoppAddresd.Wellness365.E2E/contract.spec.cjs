const { test, expect } = require('@playwright/test');

test('JWT app y ERP comparten el día y solo la persona puede guardar el cierre', async ({ page, request }) => {
  test.skip(!process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requiere PostgreSQL efímero');
  expect((await request.post('/fixture/reset', { data: { postgres: true } })).ok()).toBeTruthy();
  const app = await (await request.get('/fixture/session/app')).json();
  const erp = await (await request.get('/fixture/session/erp')).json();
  const denied = await (await request.get('/fixture/session/erp-denied')).json();
  const unassigned = await (await request.get('/fixture/session/erp-unassigned')).json();
  const people = '/api/v1/people';
  const current = people + '/me/plan';
  const patient = people + '/' + app.patientId;
  await page.goto('/fixture');

  const call = async (path, token, method = 'GET', input) => page.evaluate(async ({ path, token, method, input }) => {
    const r = await fetch(path, {
      method,
      headers: { ...(token ? { Authorization: 'Bearer ' + token } : {}), 'Content-Type': 'application/json' },
      ...(input ? { body: JSON.stringify(input) } : {}),
    });
    const body = r.headers.get('content-type')?.includes('json') ? await r.json() : null;
    document.querySelector('[role=status]').textContent = r.ok && body?.status
      ? 'Día ' + body.today + ' · ' + body.status.state : String(r.status);
    return { status: r.status, body };
  }, { path, token, method, input });

  expect((await call(current)).status).toBe(401);
  expect((await call(patient + '/plan', denied.accessToken)).status).toBe(403);
  expect((await call(current, erp.accessToken)).status).toBe(403);
  expect((await call(patient + '/plan', unassigned.accessToken)).status).toBe(404);
  expect((await call(people + '/e6148d60-f781-4b58-b2ae-8ab6ef31b263/plan', app.accessToken)).status).toBe(404);
  const self = await call(current, app.accessToken);
  expect(self).toMatchObject({ status: 200, body: { today: 1, todayLocalDate: '2026-01-01', status: { state: 'active' } } });
  expect(await call(patient + '/plan', erp.accessToken)).toEqual(self);
  await expect(page.getByRole('status')).toHaveText('Día 1 · active');
  const canonical = '/v1/people/' + app.patientId;
  expect(await call(canonical + '/plan/1', app.accessToken)).toMatchObject({ status: 200, body: { d: 1, w: null } });
  expect(await call(canonical + '/plan/2', app.accessToken)).toMatchObject({ status: 422, body: { code: 'future_day' } });

  const closure = { adh: .9, sleep: 7, energy: 4, pain: 0, sym: ["nauseas"], w: 79, ex: true };
  expect((await call(patient + '/logs/1', erp.accessToken, 'PUT', closure)).status).toBe(403);
  expect(await call(people + '/me/logs/2', app.accessToken, 'PUT', closure)).toMatchObject({
    status: 422, body: { error: 'future_day' },
  });
  for (let i = 0; i < 2; i++) expect((await call(people + '/me/logs/1', app.accessToken, 'PUT', closure)).status).toBe(204);
  expect(await call(patient + '/plan/1', erp.accessToken)).toMatchObject({
    status: 200, body: { today: 1, logStatus: 'recorded', measuredWeightKg: 79 },
  });
  expect(await call(canonical + '/plan/1', erp.accessToken)).toMatchObject({ status: 200, body: { d: 1, w: 79 } });
  expect(await call(canonical + '/logs/1', app.accessToken, 'PUT', { ...closure, ex: 1 })).toMatchObject({
    status: 200, body: { ok: true, ver: 2 },
  });
  expect(await (await request.get('/fixture/persistence-stats')).json()).toMatchObject({ events: 1, planVersion: 2 });

  expect((await request.put('/fixture/clock', { data: { instant: '2026-01-02T03:00:00Z' } })).ok()).toBeTruthy();
  expect((await request.put('/fixture/profile-zone', { data: { timeZone: 'Asia/Tokyo' } })).ok()).toBeTruthy();
  expect(await call(current, app.accessToken)).toMatchObject({ status: 200, body: {
    today: 1, todayLocalDate: '2026-01-02', measuredWeightKg: 79,
  } });
  expect((await request.put('/fixture/clock', { data: { instant: '2026-01-02T15:00:00Z' } })).ok()).toBeTruthy();
  expect(await call(canonical + '/plan/2', erp.accessToken)).toMatchObject({ status: 200, body: { d: 2, w: null } });
  expect(await call(current, app.accessToken)).toMatchObject({ status: 200, body: { today: 2, measuredWeightKg: null } });

  for (const block of ['unanswered', 'doctor', 'membership_inactive']) {
    expect((await request.post('/fixture/reset', { data: { postgres: true, block } })).ok()).toBeTruthy();
    expect(await call(current, app.accessToken)).toMatchObject({
      status: 409, body: { error: 'plan_locked', status: { state: 'locked', code: block } },
    });
  }
});
