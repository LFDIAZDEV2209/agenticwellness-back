const { expect, test } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const api = 'http://127.0.0.1:51348';
const erpOrigin = 'http://127.0.0.1:51349';
const historyPath = '/api/v1/erp/motor365/parameters/versions';
const headers = session => ({ Authorization: 'Bearer ' + session.accessToken });

async function checked(reply, status = 200) {
  expect(reply.status(), await reply.text()).toBe(status);
  return status === 200 ? reply.json() : undefined;
}

async function session(request, audience) {
  return checked(await request.get('/fixture/session/' + audience));
}

async function browserSession(browser, auth, roles, writes) {
  const context = await browser.newContext({ locale: 'es-CO', viewport: { width: 1000, height: 900 } });
  await connectClients(context, auth, null, null, { api, appOrigin: erpOrigin });
  await context.route('**/api/auth/me', route => route.fulfill({ json: {
    id: auth.userId, email: 'qa@example.invalid', firstName: 'Admin', lastName: 'QA', roles, permissions: [],
  } }));
  await context.route('**' + historyPath + '**', async route => {
    if (route.request().method() !== 'GET') {
      writes.push(route.request().method());
      return route.fulfill({ status: 405 });
    }
    const url = new URL(route.request().url());
    return route.fulfill({ response: await route.fetch({ url: api + url.pathname + url.search }) });
  });
  return context;
}

test('clinical_admin consulta quién cambió los parámetros y compara versiones sin publicarlas', async ({ browser, request }) => {
  await checked(await request.post('/fixture/reset', { data: { postgres: true } }), 204);
  const [publisher, reader] = await Promise.all([session(request, 'erp-admin'), session(request, 'erp-clinical-admin')]);
  // La auditoría proviene del JWT firmado por el host de pruebas, no de la respuesta de UI.
  const publisherId = JSON.parse(Buffer.from(publisher.accessToken.split('.')[1], 'base64url')).sub;
  const legacyPath = '/v1/engine/params';
  const before = await checked(await request.get(legacyPath, { headers: headers(publisher) }));
  const firstFloor = before.params.floorF === 1300 ? 1350 : 1300;
  const currentFloor = firstFloor + 100;
  const firstReason = `QA · primera revisión de parámetros v${before.params_version + 1}`;
  const activeReason = `QA · referencia de auditoría actual v${before.params_version + 2}`;
  const first = await checked(await request.put(legacyPath, { headers: headers(publisher), data: {
    params: { floorF: firstFloor }, reason: firstReason, expectedVersion: before.params_version,
  } }));
  const active = await checked(await request.put(legacyPath, { headers: headers(publisher), data: {
    params: { floorF: currentFloor }, reason: activeReason, expectedVersion: first.params_version,
  } }));

  const writes = [];
  const context = await browserSession(browser, reader, ['clinical_admin'], writes);
  try {
    const page = await context.newPage();
    await page.goto(erpOrigin + '/settings');
    const historyLink = page.getByRole('link', { name: 'Historial de parámetros del Motor 365' });
    await expect(historyLink).toBeVisible();
    await historyLink.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Historial de parámetros del Motor 365' })).toBeVisible();
    await expect(page.getByText(activeReason, { exact: true })).toBeVisible();
    await expect(page.getByText(firstReason, { exact: true })).toBeVisible();
    const activeVersion = page.getByRole('listitem').filter({ hasText: activeReason });
    await expect(activeVersion.getByText(publisherId, { exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'Versión a comparar' }).selectOption(String(first.params_version));
    const table = page.getByRole('table', { name: 'Diferencias de parámetros' });
    const floor = table.getByRole('row', { name: /Piso energético femenino/ });
    await expect(floor.getByRole('cell', { name: String(firstFloor), exact: true })).toBeVisible();
    await expect(floor.getByRole('cell', { name: String(currentFloor), exact: true })).toBeVisible();

    await page.setViewportSize({ width: 390, height: 844 });
    await expect(floor).toBeVisible();
    expect(writes).toEqual([]);
    expect(await checked(await request.get(legacyPath, { headers: headers(publisher) }))).toEqual(active);

    const pageOne = await checked(await request.get(historyPath + '?limit=1', { headers: headers(reader) }));
    expect(pageOne.versions[0]).toMatchObject({ version: active.params_version, changedBy: publisherId, reason: activeReason });
    const pageTwo = await checked(await request.get(historyPath + '?limit=1&before=' + pageOne.nextBefore, { headers: headers(reader) }));
    expect(pageTwo.versions[0].version).toBe(first.params_version);
    await checked(await request.get(historyPath + '/' + (active.params_version + 1), { headers: headers(reader) }), 404);
    const reference = await checked(await request.get(historyPath + '/1', { headers: headers(reader) }));
    expect(reference.revision).toMatchObject({ version: 1, defaults: true, changedBy: null, publishedAt: null, reason: null });
  } finally {
    await context.unrouteAll({ behavior: 'ignoreErrors' });
    await context.close();
  }
});

test('los demás roles no pueden consultar auditoría de parámetros', async ({ browser, request }) => {
  const professional = await session(request, 'erp');
  const context = await browserSession(browser, professional, ['Professional'], []);
  try {
    const page = await context.newPage();
    await page.goto(erpOrigin + '/settings');
    await expect(page.getByRole('heading', { name: 'Configuración' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Historial de parámetros del Motor 365' })).toHaveCount(0);
    await page.goto(erpOrigin + '/settings/motor365-parameters');
    await expect(page.getByRole('alert').getByText('No tienes permiso para consultar este historial.')).toBeVisible();
    for (const audience of ['erp', 'erp-admin', 'app']) {
      const reader = await session(request, audience);
      await checked(await request.get(historyPath, { headers: headers(reader) }), 403);
      await checked(await request.get(historyPath + '/1', { headers: headers(reader) }), 403);
    }
  } finally {
    await context.unrouteAll({ behavior: 'ignoreErrors' });
    await context.close();
  }
});
