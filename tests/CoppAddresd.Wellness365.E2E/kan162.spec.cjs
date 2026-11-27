const { test: base, expect } = require('@playwright/test');
const { connectClients } = require('./client-fixtures.cjs');

const instant = '2026-01-01T18:00:00.000Z';
const calendar = '/api/v1/people/me/plan';
const operation = calendar + '/1/ai-adjustment';
const exact = { exact: true };
const appliedReason = 'Una sesión más corta y un ajuste pequeño de energía para facilitar la constancia.';
const format = new Intl.NumberFormat('es-CO');
const withAuth = session => ({ headers: { Authorization: 'Bearer ' + session.accessToken } });
const plan = ({ nutrition, exercise, mealPlan, engine_ver, params_version }) =>
  ({ nutrition, exercise, mealPlan, engine_ver, params_version });

async function checked(response, status = 200) {
  expect(response.status(), response.url() + ' · ' + await response.text()).toBe(status);
  return status === 204 ? undefined : response.json();
}

const test = base.extend({
  motor: async ({ request, browser }, use, info) => {
    const { appOrigin, provider } = info.config.metadata;
    const contexts = [];
    try {
      await checked(await request.post('/fixture/reset', { data: { postgres: true, fit: 2, instant } }), 204);
      const app = await checked(await request.get('/fixture/session/app'));
      const erp = await checked(await request.get('/fixture/session/erp'));
      const auditPath = '/api/v1/people/' + app.patientId + '/plan/1/ai-adjustment/audit';
      const readDay = async () => checked(await request.get(calendar + '?includeMenu=true', withAuth(app)));
      const deterministic = async () =>
        checked(await request.get('/v1/people/' + app.patientId + '/plan/1', withAuth(app)));
      const start = await readDay();
      expect(start).toMatchObject({ day: 1, status: { state: 'active' }, real: true });
      expect(start.exercise.minutes).toBeGreaterThan(1);
      const body = {
        engine_ver: start.engine_ver,
        params_version: start.params_version,
        changes: {
          nutrition: { kcal: start.nutrition.kcal + 20, carbsG: start.nutrition.carbsG + 5 },
          exercise: { minutes: start.exercise.minutes - 1 },
        },
        reason: 'daily_consistency',
      };
      const configure = async ({ status = 200, body: proposal = body, hold = false, raw } = {}) =>
        checked(await request.put(provider + '/fixture/scenario', { data: { status, body: proposal, hold, raw } }));
      await configure();
      const open = async () => {
        const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: 'es-CO' });
        contexts.push(context);
        await connectClients(context, app, undefined, undefined, {
          api: info.project.use.baseURL, appOrigin, instant,
        });
        const page = await context.newPage();
        await page.clock.setFixedTime(new Date(instant));
        await page.goto(appOrigin);
        await expect(page.getByText('Día 1 de 365', exact)).toBeVisible({ timeout: 25_000 });
        await page.getByRole('button', { name: 'Ver mi plan de bienestar' }).click();
        await expect(planRegion(page)).toBeVisible();
        return page;
      };
      await use({
        app, auditPath, body, start, open, configure, deterministic, readDay,
        submitDirect: () => request.post(operation, withAuth(app)),
        releaseProvider: async () => checked(await request.post(provider + '/fixture/release')),
        calls: async () => checked(await request.get(provider + '/fixture/calls')),
        audit: async () => checked(await request.get(auditPath, withAuth(erp))),
      });
    } finally {
      await Promise.all(contexts.map(context => context.close()));
    }
  },
});

const planRegion = page => page.getByRole('region', { name: 'Tu Plan de Bienestar', ...exact });
const adjustment = page => page.getByRole('region', { name: 'Ajuste de plan con IA', ...exact });

async function planNutrition(page, { kcal, proteinG, carbsG, fatG }) {
  await page.getByRole('button', { name: 'Nutrición', ...exact }).first().click();
  await expect(page.getByRole('heading', { name: 'Dieta de hoy' })).toBeVisible();
  await expect(page.getByText('Meta diaria: ' + format.format(kcal) + ' kcal', exact)).toBeVisible();
  await expect(page.getByText(proteinG + ' g proteína · ' + carbsG + ' g carbohidratos · ' + fatG + ' g grasas', exact))
    .toBeVisible();
}

async function planExercise(page, exercise) {
  await planRegion(page).getByRole('button', { name: 'Actividad', ...exact }).click();
  await expect(page.getByRole('heading', { name: exercise.name.es, ...exact })).toBeVisible();
  await expect(page.getByText(new RegExp('Día 1 · ' + exercise.minutes + ' min · Fase '))).toBeVisible();
}

async function mutate(page, method, name) {
  const button = adjustment(page).getByRole('button', { name, ...exact });
  await expect(button, 'Falta la acción de KAN-162 en el plan de Antares').toBeVisible();
  const [response] = await Promise.all([
    page.waitForResponse(res => res.request().method() === method && new URL(res.url()).pathname === operation),
    button.click(),
  ]);
  return checked(response);
}

const version = day => ({ engine_ver: day.engine_ver, params_version: day.params_version });

test('control · Antares muestra calorías, macros y ejercicio del motor sin llamar al proveedor', async ({ motor }) => {
  await planNutrition(await motor.open(), motor.start.nutrition);
  await planExercise(await motor.open(), motor.start.exercise);
  expect(plan(await motor.readDay())).toEqual(plan(motor.start));
  expect(await motor.calls()).toEqual([]);
});

test('contrato · el día real admite solicitar una decisión al servidor con el plan visible', async ({ motor }) => {
  await planNutrition(await motor.open(), motor.start.nutrition);
  const decision = await checked(await motor.submitDirect());
  expect(decision).toMatchObject({ status: 'applied', ...version(motor.start) });
});

test('aplica una propuesta acotada, persiste el historial y permite recuperar exactamente el plan base',
  async ({ motor }) => {
    const baseDay = await motor.deterministic();
    const page = await motor.open();
    const applied = await mutate(page, 'POST', 'Solicitar ajuste');
    expect(applied).toMatchObject({ id: expect.any(String), status: 'applied', ...version(motor.start),
      reason: { es: appliedReason } });
    await expect(adjustment(page)).toContainText(appliedReason);

    const effective = await motor.readDay();
    expect(effective.exercise).toMatchObject(motor.body.changes.exercise);
    expect(effective.exercise.items.map(item => item.id)).toEqual(motor.start.exercise.items.map(item => item.id));
    expect(effective.exercise.items.reduce((sum, item) => sum + item.minutes, 0)).toBe(effective.exercise.minutes);
    expect(effective.nutrition).toEqual({ ...motor.start.nutrition, ...motor.body.changes.nutrition });
    expect(await motor.deterministic()).toEqual(baseDay);
    await planExercise(await motor.open(), effective.exercise);
    await planNutrition(await motor.open(), effective.nutrition);

    const fresh = await motor.open();
    await expect(adjustment(fresh)).toContainText(appliedReason);
    const reverted = await mutate(fresh, 'DELETE', 'Volver al plan base');
    expect(reverted).toMatchObject({ status: 'reverted', ...version(motor.start) });
    expect(plan(await motor.readDay())).toEqual(plan(motor.start));
    expect(await motor.deterministic()).toEqual(baseDay);
    await planExercise(await motor.open(), motor.start.exercise);
    await planNutrition(await motor.open(), motor.start.nutrition);
    expect(await motor.calls()).toEqual([{
      ...version(motor.start),
      nutrition: { kcal: motor.start.nutrition.kcal, proteinG: motor.start.nutrition.proteinG,
        carbsG: motor.start.nutrition.carbsG, fatG: motor.start.nutrition.fatG },
      exercise: { level: motor.start.exercise.level, minutes: motor.start.exercise.minutes },
    }]);
    expect(await motor.audit()).toEqual([
      expect.objectContaining({ id: applied.id, day: 1, status: 'applied', ...version(motor.start),
        proposed: motor.body.changes, applied: motor.body.changes,
        reason: expect.objectContaining({ es: appliedReason }) }),
      expect.objectContaining({ reverts: applied.id, day: 1, status: 'reverted', ...version(motor.start),
        reason: expect.objectContaining({ es: expect.stringMatching(/\S/) }) }),
    ]);
  });

const rejections = [
  ['piso de energía', 'kcal_floor', { nutrition: { kcal: 1 } }],
  ['tope de energía', 'kcal_cap', { nutrition: { kcal: 75_000 } }],
  ['tope de proteína', 'macro_bounds', { nutrition: { proteinG: 900 } }],
  ['piso de carbohidratos', 'macro_bounds', { nutrition: { carbsG: 1 } }],
  ['nivel no disponible', 'exercise_level', { exercise: { level: 99 } }],
  ['duración fuera de rango, incluso con otro cambio válido', 'exercise_duration', { exercise: { minutes: 240 } }],
  ['versión de parámetros distinta de la vigente', 'stale_version'],
  ['indicación clínica en el motivo', 'unsafe_content', undefined, { reason: 'Toma 99 mg de medicación.' }],
  ['proveedor no disponible', 'provider_unavailable', undefined, {}, 503],
];

for (const [name, code, changes, override = {}, status = 200] of rejections) {
  test('rechaza ' + name + ', explica y conserva el plan base', async ({ motor }) => {
    const proposed = changes ? { ...changes } : motor.body.changes;
    if (code === 'exercise_duration') proposed.nutrition = motor.body.changes.nutrition;
    const metadata = code === 'stale_version' ? { params_version: motor.start.params_version + 1 } : override;
    await motor.configure({ status, body: { ...motor.body, changes: proposed, ...metadata } });
    const before = await motor.deterministic();
    const page = await motor.open();
    const decision = await mutate(page, 'POST', 'Solicitar ajuste');
    const expectedStatus = status === 503 ? 'unavailable' : 'rejected';
    expect(decision).toMatchObject({ id: expect.any(String), status: expectedStatus, code,
      ...version(motor.start), reason: expect.objectContaining({ es: expect.stringMatching(/\S/) }) });
    await expect(adjustment(page).getByRole('status')).toContainText(decision.reason.es);
    if (code === 'unsafe_content') {
      await expect(page.getByText(override.reason, exact)).toHaveCount(0);
      expect(JSON.stringify(decision)).not.toContain(override.reason);
    }
    expect(plan(await motor.readDay())).toEqual(plan(motor.start));
    expect(await motor.deterministic()).toEqual(before);
    await planNutrition(await motor.open(), motor.start.nutrition);
    await planExercise(await motor.open(), motor.start.exercise);
    expect((await motor.calls()).length).toBeGreaterThan(0);
    const audit = await motor.audit();
    expect(audit).toEqual([expect.objectContaining({
      id: decision.id, day: 1, status: expectedStatus, code, applied: null, ...version(motor.start),
      reason: decision.reason,
    })]);
    if (status === 200) expect(audit[0].proposed).toEqual(proposed);
    if (code === 'unsafe_content') expect(JSON.stringify(audit)).not.toContain(override.reason);
  });
}


test('solo la cuenta propia puede proponer y solo el personal asignado puede consultar el historial',
  async ({ request, motor }) => {
    const page = await motor.open();
    await expect(adjustment(page)).toBeVisible();
    const wrongPerson = 'a4d75c5a-59e1-4f32-8e95-9ad1189d29eb';
    expect((await request.post(`/api/v1/people/${wrongPerson}/plan/1/ai-adjustment`, withAuth(motor.app))).status())
      .toBe(404);
    expect((await request.get(motor.auditPath, withAuth(motor.app))).status()).toBe(403);
    const other = await checked(await request.get('/fixture/session/erp-unassigned'));
    expect([403, 404]).toContain((await request.get(motor.auditPath, withAuth(other))).status());
    expect(await motor.calls()).toEqual([]);
    expect(await motor.audit()).toEqual([]);
    await planNutrition(page, motor.start.nutrition);
  });


test('un cierre confirmado mientras responde el simulador invalida la propuesta',
  async ({ request, motor }) => {
    await motor.configure({ hold: true });
    const page = await motor.open();
    const [, decision] = await Promise.all([
      (async () => {
        try {
          await expect.poll(motor.calls).toHaveLength(1);
          await checked(await request.put('/api/v1/people/me/logs/1', {
            ...withAuth(motor.app), data: { adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], ex: false },
          }), 204);
        } finally {
          await motor.releaseProvider();
        }
      })(),
      mutate(page, 'POST', 'Solicitar ajuste'),
    ]);
    expect(decision).toMatchObject({ status: 'rejected', code: 'stale_version' });
    expect(await motor.audit()).toEqual([expect.objectContaining({ id: decision.id, applied: null })]);
    const closed = await motor.open();
    await expect(adjustment(closed).getByRole('button')).toHaveCount(0);
    await expect(adjustment(closed).getByRole('status')).toContainText(decision.reason.es);
    const base = await motor.deterministic();
    await planNutrition(closed, { kcal: base.kc, proteinG: base.P, carbsG: base.C, fatG: base.F });
  });


test('descarta una propuesta con desbordamiento numérico y registra el fallo sin conservar el valor',
  async ({ motor }) => {
    const raw = JSON.stringify({ ...motor.body, changes: { nutrition: { kcal: 'overflow' } } })
      .replace('"overflow"', '1e500');
    await motor.configure({ raw });
    const page = await motor.open();
    const decision = await mutate(page, 'POST', 'Solicitar ajuste');
    expect(decision).toMatchObject({ status: 'rejected', code: 'invalid_proposal' });
    await expect(adjustment(page).getByRole('status')).toContainText(decision.reason.es);
    expect(await motor.audit()).toEqual([
      expect.objectContaining({ id: decision.id, proposed: null, applied: null }),
    ]);
    expect(plan(await motor.readDay())).toEqual(plan(motor.start));
    await planNutrition(await motor.open(), motor.start.nutrition);
  });
