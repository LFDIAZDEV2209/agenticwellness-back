const { test, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const ownPlan = '/api/v1/people/me/plan';
const syncQuery = '?includeSync=true';
const closure = { adh: 0.9, sleep: 7, energy: 4, pain: 0, sym: [], w: 79, waist: 84, ex: true };
const appOrigin = 'http://127.0.0.1:51382';
const erpOrigin = 'http://127.0.0.1:51383';
const gateway = 'http://127.0.0.1:51384';

async function session(request, client) {
  const { accessToken, patientId } = await (await request.get('/fixture/session/' + client)).json();
  return { headers: { Authorization: 'Bearer ' + accessToken }, patientId };
}

async function at(request, instant) {
  expect((await request.put('/fixture/clock', { data: { instant } })).status()).toBe(204);
}

async function openCheckin(page) {
  await page.bringToFront();
  await page.getByRole('button', { name: 'Ver mi plan de bienestar' }).press('Enter');
  const plan = page.getByRole('region', { name: 'Tu Plan de Bienestar' });
  await plan.locator('ion-button').filter({ hasText: /^Bienestar$/ }).locator('button').press('Enter');
}

async function saveCheckin(page) {
  for (const [name, value] of Object.entries({ adherence: '90', sleepHours: '7', energy: '4', pain: '0', weightKg: '79' }))
    await page.locator('ion-input[name="' + name + '"] input').fill(value);
  await page.getByText('Sí', { exact: true }).click();
  await page.getByRole('button', { name: 'Cerrar el día', exact: true }).click();
}

test.describe('KAN-153 · Motor 365 sync', () => {
  test.skip(!process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requires isolated PostgreSQL');

  test.beforeEach(async ({ request }) => {
    const response = await request.post('/fixture/reset', { data: { postgres: true } });
    expect(response.status(), await response.text()).toBe(204);
  });

  test('returns the same authorized plan and bounded sync data in both audiences', async ({ request }) => {
    const [app, erp, unassigned, denied] = await Promise.all(
      ['app', 'erp', 'erp-unassigned', 'erp-no-permission'].map(type => session(request, type)),
    );
    const erpPlan = `/api/v1/people/${app.patientId}/plan`;
    await at(request, '2026-01-16T04:59:59Z');
    expect((await request.put('/api/v1/people/me/logs/15', { headers: app.headers, data: closure })).status()).toBe(204);
    const [own, assigned] = await Promise.all([
      request.get(ownPlan + syncQuery, { headers: app.headers }),
      request.get(erpPlan + syncQuery, { headers: erp.headers }),
    ]);
    expect(own.status(), await own.text()).toBe(200);
    expect(assigned.status(), await assigned.text()).toBe(200);
    const current = await own.json();
    expect(await assigned.json()).toEqual(current);
    expect(current).toMatchObject({
      today: 15, todayLocalDate: '2026-01-15', timeZoneId: 'America/Bogota', real: true,
      engine_ver: '5.2', params_version: expect.any(Number),
      sync: {
        personId: app.patientId, fromDay: 1, throughDay: 15,
        logs: [{ day: 15, date: '2026-01-15', adherence: 0.9, weightKg: 79, waistCm: 84, completedExercise: true }],
      },
    });
    expect(current.sync.events.length).toBeGreaterThan(0);
    expect(current.sync.events.every(event => event.d <= 15 && event.params_version === current.params_version)).toBe(true);
    expect(await (await request.get(ownPlan, { headers: app.headers })).json()).not.toHaveProperty('sync');
    expect((await request.get(erpPlan + syncQuery, { headers: unassigned.headers })).status()).toBe(404);
    expect((await request.get(erpPlan + syncQuery, { headers: denied.headers })).status()).toBe(403);
    expect((await request.get(ownPlan + syncQuery, { headers: erp.headers })).status()).toBe(403);
    expect((await request.get(ownPlan + syncQuery)).status()).toBe(401);

    await at(request, '2026-01-16T05:00:00Z');
    expect(await (await request.get(ownPlan + syncQuery, { headers: app.headers })).json()).toMatchObject({
      today: 16, todayLocalDate: '2026-01-16', sync: { throughDay: 16 },
    });
    await at(request, '2026-02-12T18:00:00Z');
    expect(await (await request.get(ownPlan + syncQuery, { headers: app.headers })).json()).toMatchObject({
      today: 43, sync: { fromDay: 16, throughDay: 43, logs: [] },
    });
    expect(await (await request.get(ownPlan + '/15' + syncQuery, { headers: app.headers })).json()).toMatchObject({
      day: 15, today: 43, sync: { throughDay: 15, logs: [{ day: 15, weightKg: 79 }] },
    });
  });

  test('conditional closure preserves the first confirmed record and is idempotent on retry', async ({ request }) => {
    const { headers, patientId } = await session(request, 'app');
    await at(request, '2026-01-10T18:00:00Z');
    const write = (day, data, conditional = true) => request.put(
      `/api/v1/people/${patientId}/logs/${day}${conditional ? '?createIfAbsent=true' : ''}`,
      { headers, data },
    );
    const stats = async () => (await request.get('/fixture/persistence-stats')).json();
    const initial = await Promise.all([write(8, closure), write(8, closure)]);
    expect(initial.map(response => response.status())).toEqual([204, 204]);
    const first = await stats();
    expect(first.dates).toEqual(['2026-01-08']);

    const conflict = await write(8, { ...closure, w: 76 });
    expect(conflict.status()).toBe(409);
    expect(await conflict.json()).toEqual({ error: 'log_already_recorded' });
    expect(await stats()).toEqual(first);
    expect((await write(8, closure)).status()).toBe(204);
    expect(await stats()).toEqual(first);

    const different = await Promise.all([write(9, closure), write(9, { ...closure, w: 76 })]);
    expect(different.map(response => response.status()).sort()).toEqual([204, 409]);
    const rejected = different.find(response => response.status() === 409);
    expect(await rejected.json()).toEqual({ error: 'log_already_recorded' });
    expect(await stats()).toMatchObject({ dates: ['2026-01-08', '2026-01-09'], planVersion: first.planVersion + 1 });
    const before = await (await request.get(ownPlan + syncQuery, { headers })).json();
    expect(before.sync.logs).toEqual([
      expect.objectContaining({ day: 8, weightKg: 79 }),
      expect.objectContaining({ day: 9, weightKg: different[0].status() === 204 ? 79 : 76 }),
    ]);

    expect((await write(8, { ...closure, w: 78 }, false)).status()).toBe(204);
    const after = await (await request.get(ownPlan + syncQuery, { headers })).json();
    expect(after.sync.logs[0]).toMatchObject({ day: 8, weightKg: 78 });
    expect((await write(11, closure)).status()).toBe(422);
  });

  test('clients share the civil day, recover a lost acknowledgement and refresh actual parameters', async ({ browser, request }) => {
    test.skip(!process.env.COP_KAN130_APP_DIR || !process.env.COP_KAN130_ERP_DIR, 'Requires both clients');
    test.setTimeout(150_000);
    const [patient, erp, admin] = await Promise.all(['app', 'erp', 'erp-admin'].map(async client =>
      (await request.get('/fixture/session/' + client)).json()));
    const beforeMidnight = '2026-01-08T04:59:59Z';
    const midnight = '2026-01-08T05:00:00Z';
    await at(request, beforeMidnight);
    const appContext = await browser.newContext({ viewport: { width: 390, height: 844 }, timezoneId: 'UTC', reducedMotion: 'reduce' });
    const erpContext = await browser.newContext({ viewport: { width: 1440, height: 900 }, timezoneId: 'UTC' });
    try {
      const uploads = [];
      let appReadUnavailable = false;
      let erpReadUnavailable = false;
      await connectClients(appContext, patient, erpContext, erp, {
        api: gateway, appOrigin, erpOrigin, instant: beforeMidnight,
        onMotorRequest: async ({ req, path, route }) => {
          if (req.method() !== 'GET' || !path.endsWith('/plan') ||
            !(path === ownPlan ? appReadUnavailable : erpReadUnavailable)) return false;
          await route.fulfill({ status: 503, json: { error: 'service_unavailable' } });
          return true;
        },
        onMotorResponse: ({ req, path, response }) => {
          if (req.method() !== 'PUT' || !path.endsWith('/logs/8')) return false;
          expect(response.status()).toBe(204);
          expect(new URL(req.url()).searchParams.get('createIfAbsent')).toBe('true');
          expect(path).toContain(patient.patientId);
          uploads.push(req.postDataJSON());
          return uploads.length === 1;
        },
      });
      const [appPage, erpPage] = await Promise.all([appContext.newPage(), erpContext.newPage()]);
      await appPage.clock.setFixedTime(new Date(beforeMidnight));
      await Promise.all([
        appPage.goto(appOrigin),
        erpPage.goto(erpOrigin + '/patients/' + patient.patientId + '/360'),
      ]);
      const card = erpPage.getByRole('region', { name: 'Motor 365' });
      await expect(appPage.getByText('Día 7 de 365', { exact: true })).toBeVisible({ timeout: 25_000 });
      await expect(card.locator('time[datetime="2026-01-07"]')).toBeVisible();
      await at(request, midnight);
      await appPage.clock.setFixedTime(new Date(midnight));
      await appPage.evaluate(() => window.dispatchEvent(new Event('focus')));
      await erpPage.getByRole('button', { name: 'Actualizar' }).click();
      await expect(appPage.getByText('Día 8 de 365', { exact: true })).toBeVisible();
      await expect(appPage.locator('time[datetime="2026-01-08"]')).toBeVisible();
      await expect(card.getByText('Día 8 de 365')).toBeVisible();
      await expect(card.locator('time[datetime="2026-01-08"]')).toBeVisible();

      const appSync = appPage.getByText(/^Última sincronización:/);
      const erpSync = card.getByText(/^Última sincronización:/);
      const appTime = await appSync.textContent();
      const erpTime = await erpSync.getAttribute('datetime');
      appReadUnavailable = erpReadUnavailable = true;
      await appPage.evaluate(() => window.dispatchEvent(new Event('focus')));
      await erpPage.getByRole('button', { name: 'Actualizar' }).click();
      await expect(appPage.getByText('No se pudo actualizar el plan.')).toBeVisible();
      await expect(card.getByText('No se pudo actualizar el plan.')).toBeVisible();
      await expect(appSync).toHaveText(appTime);
      await expect(erpSync).toHaveAttribute('datetime', erpTime);
      appReadUnavailable = erpReadUnavailable = false;
      const recovery = '2026-01-08T05:01:00Z';
      await appPage.clock.setFixedTime(new Date(recovery));
      await appPage.evaluate(() => window.dispatchEvent(new Event('focus')));
      await erpPage.getByRole('button', { name: 'Actualizar' }).click();
      await expect(appSync).toHaveAttribute('datetime', recovery.replace('Z', '.000Z'));
      await expect(erpSync).not.toHaveAttribute('datetime', erpTime);
      await expect(card.getByText('No se pudo actualizar el plan.')).toHaveCount(0);

      await appContext.setOffline(true);
      await openCheckin(appPage);
      await saveCheckin(appPage);
      await expect(appPage.getByText('Guardado en este dispositivo. Se sincronizará al volver la conexión.')).toBeVisible();
      expect(uploads).toHaveLength(0);
      const lost = appPage.waitForEvent('requestfailed', { predicate: req =>
        req.method() === 'PUT' && new URL(req.url()).pathname.endsWith('/logs/8') });
      await appContext.setOffline(false);
      await lost;
      const stats = async () => (await request.get('/fixture/persistence-stats')).json();
      const first = await stats();
      expect(first).toMatchObject({ dates: ['2026-01-08'], planVersion: 2 });
      await appPage.reload();
      await expect.poll(() => uploads.length, { timeout: 12_000 }).toBe(2);
      expect(uploads[0]).toEqual(uploads[1]);
      expect(await stats()).toEqual(first);
      await erpPage.getByRole('tab', { name: 'Año 365' }).click();
      const year = erpPage.getByRole('region', { name: 'Año 365' });
      await expect(year.locator('[data-date="2026-01-08"]')).toContainText('Registrado');
      await expect(year.getByText('90 %', { exact: true })).toBeVisible();
      const selectedMonth = year.getByRole('combobox', { name: 'Mes' });
      await selectedMonth.selectOption('2026-02');
      await expect(year.getByText('Este mes no tiene registros.')).toBeVisible();
      await erpPage.getByRole('button', { name: 'Actualizar' }).click();
      await expect(selectedMonth).toHaveValue('2026-02');
      await expect(year.getByText('Este mes no tiene registros.')).toBeVisible();
      await erpPage.getByRole('tab', { name: 'Resumen' }).click();

      await appPage.bringToFront();
      await appPage.getByRole('button', { name: 'Abrir Año 365' }).press('Enter');
      await expect(appPage.locator('[data-plan-day="8"]')).toHaveAttribute('data-status', 'partial');
      await appPage.getByRole('button', { name: 'Volver a Inicio' }).press('Enter');
      await expect(appPage.getByText('Día 8 de 365', { exact: true })).toBeVisible();

      const headers = { Authorization: 'Bearer ' + admin.accessToken };
      const didRead = await request.get('/v1/engine/params', { headers });
      expect(didRead.status()).toBe(200);
      const { params, params_version } = await didRead.json();
      const appHeaders = { Authorization: 'Bearer ' + patient.accessToken };
      const initialPlan = await (await request.get(ownPlan + syncQuery, { headers: appHeaders })).json();
      const update = await request.put('/v1/engine/params', { headers, data: {
        params: { floorF: params.floorF === 2000 ? 2100 : 2000 }, reason: 'Isolated KAN-153 fixture', expectedVersion: params_version,
      } });
      expect(update.status(), await update.text()).toBe(200);
      const { params_version: version } = await update.json();
      try {
        const updatedPlan = await (await request.get(ownPlan + syncQuery, { headers: appHeaders })).json();
        expect(updatedPlan.params_version).toBe(version);
        expect(updatedPlan.nutrition.kcal).not.toBe(initialPlan.nutrition.kcal);
        expect(updatedPlan.sync.logs).toEqual(initialPlan.sync.logs);
        await appPage.evaluate(() => window.dispatchEvent(new Event('focus')));
        await erpPage.getByRole('button', { name: 'Actualizar' }).click();
        await expect(appPage.getByText(`Motor 5.2 · Parámetros ${version}`, { exact: true })).toBeVisible();
        await expect(card.getByText(`Motor 5.2 · Parámetros ${version}`, { exact: true })).toBeVisible();
        await appPage.bringToFront();
        await appPage.getByRole('region', { name: 'Actividades del día' })
          .locator('ion-button').filter({ hasText: /^Nutrición/ }).locator('button').press('Enter');
        await expect(appPage.getByRole('heading', { name: 'Dieta de hoy' })).toBeVisible();
        const kcal = new Intl.NumberFormat('es-CO').format(updatedPlan.nutrition.kcal);
        await expect(appPage.getByText(`Meta diaria: ${kcal} kcal`, { exact: true })).toBeVisible();
        await expect(card.getByText(`Meta diaria: ${kcal} kcal`, { exact: true })).toBeVisible();
      } finally {
        const restore = await request.put('/v1/engine/params', { headers, data: {
          params: { floorF: params.floorF }, reason: 'Restore isolated fixture parameters', expectedVersion: version,
        } });
        expect(restore.status()).toBe(200);
      }
    } finally {
      await Promise.all([appContext.close(), erpContext.close()]);
    }
  });

  test('demo, account and ERP clinic changes keep pending and confirmed data scoped', async ({ browser, request }) => {
    test.skip(!process.env.COP_KAN130_APP_DIR || !process.env.COP_KAN130_ERP_DIR, 'Requires both clients');
    test.setTimeout(150_000);
    const [original, erp, unlinked] = await Promise.all(['app', 'erp', 'app-unlinked'].map(async client =>
      (await request.get('/fixture/session/' + client)).json()));
    const app = { ...original };
    const instant = '2026-01-09T18:00:00Z';
    await at(request, instant);
    erp.clinics = [
      { id: '0d5bd998-d744-4667-913c-1a99a2ea5aaa', name: 'Clínica QA A', isPrimary: true, locations: [], permissions: [] },
      { id: '0d5bd998-d744-4667-913c-1a99a2ea5bbb', name: 'Clínica QA B', isPrimary: false, locations: [], permissions: [] },
    ];
    erp.organization = { id: '0d5bd998-d744-4667-913c-1a99a2ea5ccc', name: 'Organización QA' };
    const appContext = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
    const erpContext = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    try {
      const uploads = [];
      let holdOldClinic = null;
      await connectClients(appContext, app, erpContext, erp, {
        api: gateway, appOrigin, erpOrigin, instant,
        onMotorRequest: async ({ req, path, route }) => {
          if (!path.endsWith('/plan') || path === ownPlan) return false;
          const clinic = req.headers()['x-clinic-id'];
          if (holdOldClinic && clinic === erp.clinics[0].id) await holdOldClinic;
          if (clinic !== erp.clinics[1].id) return false;
          await route.fulfill({ status: 403, json: { error: 'forbidden' } });
          return true;
        },
        onMotorResponse: ({ req, path, response }) => {
          if (req.method() === 'PUT' && path.includes('/logs/'))
            uploads.push(response.status());
          return false;
        },
      });
      const [appPage, erpPage] = await Promise.all([appContext.newPage(), erpContext.newPage()]);
      await appPage.clock.setFixedTime(new Date(instant));
      await Promise.all([appPage.goto(appOrigin), erpPage.goto(erpOrigin + '/patients/' + app.patientId + '/360')]);
      await expect(appPage.getByText('Día 9 de 365', { exact: true })).toBeVisible({ timeout: 25_000 });
      const card = erpPage.getByRole('region', { name: 'Motor 365' });
      await expect(card.getByText('Día 9 de 365')).toBeVisible();
      let releaseClinic;
      holdOldClinic = new Promise(resolve => { releaseClinic = resolve; });
      const oldClinicRequest = erpPage.waitForRequest(req => req.url().endsWith('/plan') &&
        req.headers()['x-clinic-id'] === erp.clinics[0].id);
      await erpPage.getByRole('button', { name: 'Actualizar' }).click();
      await oldClinicRequest;
      await erpPage.getByRole('button', { name: 'Cambiar clínica activa' }).click();
      await erpPage.getByRole('menuitem', { name: /Clínica QA B/ }).click();
      await expect(card).toHaveCount(0);
      releaseClinic();
      holdOldClinic = null;
      await expect(card).toHaveCount(0);

      await appContext.setOffline(true);
      await openCheckin(appPage);
      await saveCheckin(appPage);
      await expect(appPage.getByText('Guardado en este dispositivo. Se sincronizará al volver la conexión.')).toBeVisible();
      const write = await request.put('/api/v1/people/me/logs/9', {
        headers: { Authorization: 'Bearer ' + original.accessToken }, data: { ...closure, adh: .55, w: 76 },
      });
      expect(write.status()).toBe(204);

      // Navigate while the real queue is paused. Session changes are exercised in the same storage.
      await appPage.goto('about:blank');
      Object.assign(app, { accessToken: 'demo-access-token', userId: 'demo-fixture' });
      await appContext.setOffline(false);
      await appPage.goto(appOrigin);
      await openCheckin(appPage);
      await expect(appPage.getByRole('heading', { name: 'Datos sensibles' })).toBeVisible();
      await appPage.getByRole('checkbox', { name: /^Acepto el uso de mis datos sensibles/ }).click();
      await appPage.getByRole('button', { name: 'Continuar', exact: true }).click();
      await expect(appPage.getByRole('heading', { name: 'Cerrar el día' })).toBeVisible();
      await saveCheckin(appPage);
      await expect(appPage.getByText('Cierre guardado en este dispositivo.')).toBeVisible();
      expect(uploads).toEqual([]);

      Object.assign(app, unlinked);
      await appPage.reload();
      await expect(appPage.getByText('Tu cuenta no tiene un plan vinculado.')).toBeVisible();
      await expect(appPage.getByText(/^Última sincronización:/)).toHaveCount(0);
      expect(uploads).toEqual([]);

      Object.assign(app, original);
      await appPage.reload();
      await expect(appPage.getByText('Día 9 de 365', { exact: true })).toBeVisible();
      await expect.poll(() => uploads).toEqual([409]);
      await openCheckin(appPage);
      await expect(appPage.getByText('Se conservó el cierre que ya estaba registrado en el servidor.')).toBeVisible();
      expect(await (await request.get('/fixture/persistence-stats')).json()).toMatchObject({
        dates: ['2026-01-09'], planVersion: 2,
      });
      const plan = await request.get(ownPlan + syncQuery, {
        headers: { Authorization: 'Bearer ' + original.accessToken },
      });
      expect((await plan.json()).sync.logs).toEqual([expect.objectContaining({ day: 9, adherence: .55, weightKg: 76 })]);
    } finally {
      await Promise.all([appContext.close(), erpContext.close()]);
    }
  });
});
