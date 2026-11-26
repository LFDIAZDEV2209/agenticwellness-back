const { test, expect } = require('@playwright/test');
const { readFile } = require('node:fs/promises');

test('Inicio y Perfil 360 muestran el mismo día y un cierre de app aparece en ERP', async ({ browser, request }) => {
  test.skip(!process.env.COP_KAN130_APP_DIR || !process.env.COP_KAN130_ERP_DIR ||
    !process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requiere los dos checkouts y PostgreSQL efímero');
  test.setTimeout(150_000);
  const instant = '2026-01-08T18:00:00Z';
  expect((await request.post('/fixture/reset', { data: { postgres: true } })).ok()).toBeTruthy();
  expect((await request.put('/fixture/clock', { data: { instant } })).ok()).toBeTruthy();
  const [app, erp] = await Promise.all(['app', 'erp'].map(async type =>
    (await request.get('/fixture/session/' + type)).json()));
  const api = process.env.COP_KAN156_GATEWAY_URL ?? 'http://127.0.0.1:51361';
  const appContext = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'es-CO' });
  const erpContext = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'es-CO' });
  try {
    await appContext.addInitScript(({ userId, at }) => {
      if (location.origin !== 'http://127.0.0.1:51362') return;
      localStorage.setItem('antares_lang', 'es');
      localStorage.setItem('copp.wellness-privacy.audit.v1.' + at + '.00000000.fixture',
        JSON.stringify({ actor: userId, action: 'accepted', at, acceptedAt: at, scope: 'sensitive-data', version: '2026-11-25-draft.1' }));
    }, { userId: app.userId, at: instant });
    await erpContext.addInitScript(() => {
      if (location.origin === 'http://127.0.0.1:51363') localStorage.setItem('copp_lang', 'es');
    });
    const requests = [];
    const closures = [];
    await Promise.all([[appContext, app], [erpContext, erp]].map(([context, session]) =>
      context.route('**/api/**', async route => {
        const req = route.request();
        const url = new URL(req.url());
        const path = url.pathname;
        if (path.startsWith('/api/v1/people/') || path.includes('/year-365')) {
          requests.push([session.userId, req.method(), path]);
          const response = await route.fetch({ url: api + path + url.search });
          if (req.method() === 'PUT' && path.includes('/logs/')) {
            expect(response.status()).toBe(204);
            closures.push({ path, body: req.postDataJSON() });
            if (closures.length === 1) return route.abort('connectionreset');
          }
          return route.fulfill({ response });
        }
        const respond = json => route.fulfill({ json });
        if (path === '/api/auth/refresh') return respond({ accessToken: session.accessToken, expiresIn: 1200 });
        if (path === '/api/auth/me') return respond({ id: session.userId, email: 'qa@example.invalid',
          firstName: 'Persona', lastName: 'QA', roles: [],
          permissions: ['Program.Profile360View', 'Program.Export', 'Program.View', 'Patients.View'] });
        if (path === '/api/v1/me/patient-profile') return respond({ patientId: session.patientId,
          firstName: 'Persona', lastName: 'QA' });
        if (path.endsWith('/overview')) return respond({ patient_name: 'Persona QA', enrollment: null,
          streak: null, xp: null, tareas_hoy: [], adherencia_semana: [], evolucion_12_semanas: [],
          scores: null, mediciones_clinicas: null, weaknesses: [], interventions: [], pending_adaptations: 0,
          clinical_reviews: [], daily_checkins: [] });
        if (path === '/api/v1/me/context') return respond({ userId: session.userId, clinics: [], organization: null, onboardingCompleted: true });
        if (path === '/api/v1/me/account-legal-consents') return respond({
          locale: 'es', enforcementEnabled: true, acceptanceRequired: false, documents: [] });
        if (path === '/api/v1/me/legal-consents') return respond({ locale: 'es', scopes: [] });
        if (path.includes('/biometria/')) return route.fulfill({ status: 404, json: {} });
        return respond([]);
      })));
    const [appPage, erpPage] = await Promise.all([appContext.newPage(), erpContext.newPage()]);
    await appPage.clock.setFixedTime(new Date(instant));
    const erpErrors = [];
    erpPage.on('pageerror', error => erpErrors.push(error.message));
    await Promise.all([
      appPage.goto('http://127.0.0.1:51362'),
      erpPage.goto('http://127.0.0.1:51363/patients/' + app.patientId + '/360'),
    ]);
    await expect(appPage.getByText('Día 8 de 365', { exact: true })).toBeVisible({ timeout: 20_000 });
    await expect(appPage.getByText('Plan activo', { exact: true })).toBeVisible();
    const card = erpPage.getByRole('region', { name: 'Motor 365', exact: true });
    await expect(card.getByText('Día 8 de 365')).toBeVisible({ timeout: 20_000 }).catch(async (error) => {
      throw new Error(`${error.message}\nERP: ${erpPage.url()}\n${(await erpPage.locator('body').ariaSnapshot()).slice(0, 5000)}\nSolicitudes Motor: ${JSON.stringify(requests)}\nErrores ERP: ${erpErrors.join("; ")}`);
    });
    await expect(card.getByText('Plan activo')).toBeVisible();
    await expect(card.getByText('Sin pesaje')).toBeVisible();

    await appContext.setOffline(true);
    await appPage.getByRole('button', { name: 'Ver mi plan de bienestar' }).click();
    await appPage.getByRole('button', { name: 'Bienestar', exact: true }).click();
    for (const [name, value] of Object.entries({ adherence: '90', sleepHours: '7', energy: '4', pain: '0', weightKg: '79' }))
      await appPage.locator('ion-input[name="' + name + '"] input').fill(value);
    await appPage.getByText('Sí', { exact: true }).click();
    await appPage.getByRole('button', { name: 'Cerrar el día', exact: true }).click();
    await expect(appPage.getByText('Guardado en este dispositivo. Se sincronizará al volver la conexión.')).toBeVisible();
    expect(requests.filter(([, method]) => method === 'PUT')).toHaveLength(0);
    const lostAck = appPage.waitForEvent('requestfailed', {
      predicate: failed => failed.method() === 'PUT' && failed.url().endsWith('/logs/8'),
      timeout: 15_000,
    });
    await appContext.setOffline(false);
    await lostAck;
    await expect.poll(() => closures.length).toBe(1);
    await appContext.setOffline(true);
    await appContext.setOffline(false);
    await expect.poll(() => closures.length).toBe(2);
    expect(closures[0]).toEqual(closures[1]);
    await expect(appPage.getByText('Cierre sincronizado.')).toBeVisible();
    await erpPage.reload();
    await expect(card.getByText('79 kg', { exact: true })).toBeVisible();
    expect(requests).toContainEqual([app.userId, 'PUT', '/api/v1/people/me/logs/8']);
    expect(await (await request.get('/fixture/persistence-stats')).json())
      .toMatchObject({ dates: ['2026-01-08'], events: 1, planVersion: 2 });

    await erpPage.getByRole('tab', { name: 'Año 365' }).click();
    const report = erpPage.getByRole('region', { name: 'Año 365', exact: true });
    await expect(report.locator('[data-date="2026-01-08"]')).toContainText('Registrado');
    await expect(report.getByText('90 %', { exact: true })).toBeVisible();
    const download = erpPage.waitForEvent('download');
    await report.getByRole('button', { name: 'Descargar CSV' }).click();
    const csv = await download;
    expect(await readFile(await csv.path(), 'utf8')).toContain('"2026-01","Adherencia","90","%"');
  } finally {
    await Promise.all([appContext.close(), erpContext.close()]);
  }
});
