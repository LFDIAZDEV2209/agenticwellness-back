const { test, expect } = require('@playwright/test');

test.describe('KAN-123 · energía del Motor 365', () => {
  test.beforeEach(async ({ page, request }) => {
    test.skip(!process.env.COP_KAN131_TEST_DB_CONNECTION, 'Requiere PostgreSQL efímero');
    const reset = await request.post('/fixture/reset', { data: { postgres: true } });
    expect(reset.status(), 'preparación del paciente sintético: ' + await reset.text()).toBe(204);
    await page.goto('/fixture');
  });

  async function patient(request) {
    const { accessToken, patientId } = await (await request.get('/fixture/session/app')).json();
    const path = '/v1/people/' + patientId;
    const headers = { Authorization: 'Bearer ' + accessToken };
    return {
      update: async data => {
        const response = await request.put(path, { headers, data });
        expect(response.status(), await response.text()).toBe(200);
        expect(await response.json()).toMatchObject({ status: { ok: true } });
      },
      close: async (day, weight) => {
        const response = await request.put(path + '/logs/' + day, { headers, data: log(weight, .9) });
        expect(response.status(), await response.text()).toBe(200);
      },
      annual: async () => {
        const response = await request.get(path + '/plan', { headers });
        expect(response.status()).toBe(200);
        return (await response.json()).days;
      },
    };
  }

  const log = (w, adh = .8) => ({ adh, sleep: 7, energy: 4, pain: 0, sym: [], w, ex: 1 });
  const flatWeighIns = (length, weight) =>
    Object.fromEntries(Array.from({ length }, (_, i) => [i + 1, log(weight)]));

  async function moveTo(request, day) {
    const instant = new Date(Date.UTC(2026, 0, day, 18)).toISOString();
    const response = await request.put('/fixture/clock', { data: { instant } });
    expect(response.status()).toBe(204);
  }

  async function readDay(page, day, kcal, mode = 'main') {
    await page.getByLabel('Día para energía').fill(String(day));
    const [response] = await Promise.all([
      page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/plan/' + day)),
      page.getByRole('button', { name: 'Leer energía' }).click(),
    ]);
    expect(response.status(), response.url() + ' ' + await response.text()).toBe(200);
    const body = await response.json();
    expect(body).toMatchObject({ d: day, kc: kcal, mode });
    await expect(page.locator('#energy-result')).toHaveText(`Día ${day} · ${kcal} kcal · ${mode}`);
    return body;
  }

  // A los 36 años, BMR de 1077.75 (F) y 2512.5 (M). Cada fila contiene
  // valores de la guía calculados de forma independiente, sin usar el motor.
  const profiles = [
    [{ sex: 'F', h: 155, w0: 45, target: 44 }, [
      [1200, 1200, 1200, 1360, 1420, 1490, 1230, 1200, 1200],
      [1330, 1260, 1200, 1560, 1630, 1700, 1410, 1330, 1300],
      [1500, 1420, 1340, 1750, 1840, 1920, 1590, 1500, 1470],
      [1670, 1580, 1490, 1950, 2050, 2140, 1770, 1670, 1640],
    ]],
    [{ sex: 'M', h: 190, w0: 150, target: 140 }, [
      [2710, 2560, 2410, 3170, 3320, 3470, 2860, 2710, 2650],
      [3110, 2940, 2760, 3630, 3800, 3970, 3280, 3110, 3040],
      [3500, 3310, 3120, 4090, 4280, 4480, 3700, 3500, 3430],
      [3900, 3680, 3470, 4550, 4770, 4980, 4120, 3900, 3810],
    ]],
  ];
  for (const [profile, rows] of profiles) {
    test(`extremo de ${profile.w0} kg, cuatro actividades y nueve combinaciones`, async ({ page, request }) => {
      test.setTimeout(60_000);
      const { update } = await patient(request);
      for (const [i, row] of rows.entries()) {
        let column = 0;
        for (const goal of ['bajar', 'masa', 'def']) {
          for (const pace of ['s', 'm', 'max']) {
            const act = i + 1;
            await test.step(`${profile.sex} · actividad ${act} · ${goal}/${pace}`, async () => {
              const target = goal === 'masa' ? profile.w0 + 5 : profile.target;
              await update({ ...profile, target, act, goal, pace });
              expect((await readDay(page, 1, row[column])).adjK).toBe(0);
            });
            column++;
          }
        }
      }
    });
  }

  test('aplica el piso de 1500 kcal para un hombre de bajo TDEE', async ({ page, request }) => {
    const { update } = await patient(request);
    await update({ sex: 'M', h: 155, w0: 49, target: 48, act: 1, goal: 'bajar', pace: 'max' });
    await readDay(page, 1, 1500);
  });

  test('redondea hacia arriba en el empate y es estable al volver a leer', async ({ page, request }) => {
    const { update } = await patient(request);
    for (const [w0, kcal] of [[82.49, 1750], [82.50, 1760], [82.51, 1760]]) {
      await update({ sex: 'M', h: 156, w0, act: 1, goal: 'bajar', pace: 's' });
      await readDay(page, 1, kcal);
      await page.reload();
      await readDay(page, 1, kcal);
    }
  });

  for (const [goal, w0, outside, edge, main, maintenance] of [
    ['bajar', 75, 69.13, 69.12, 1720, 2020],
    ['masa', 67, 69.06, 69.07, 2100, 1910],
  ]) {
    test(`mantenimiento inclusivo a 0.05 kg · ${goal}`, async ({ page, request }) => {
      const { update, close } = await patient(request);
      await update({ w0, target: goal === 'bajar' ? 69.07 : 69.12, goal, pace: 'm' });
      await close(1, outside);
      await moveTo(request, 2);
      await readDay(page, 2, main);

      await close(1, edge);
      expect((await readDay(page, 2, maintenance, 'maint')).adjK).toBe(0);
    });
  }

  test('rechaza el siguiente incremento positivo si superaría el 12 % del TDEE', async ({ page, request }) => {
    const { update } = await patient(request);
    await moveTo(request, 22);
    await update({
      sex: 'F', h: 155, w0: 45, target: 50, act: 1, goal: 'masa', pace: 's',
      log: flatWeighIns(21, 45),
    });
    expect((await readDay(page, 14, 1360)).adjK).toBe(0);
    expect((await readDay(page, 15, 1460)).adjK).toBe(100);
    expect((await readDay(page, 22, 1460)).adjK).toBe(100);
  });

  async function seedNegativeAdjustment(request) {
    const { update, annual } = await patient(request);
    await moveTo(request, 22);
    await update({
      sex: 'F', h: 165, w0: 70.12, target: 60, act: 1, goal: 'bajar', pace: 's',
      log: flatWeighIns(21, 70.12),
    });
    return annual;
  }

  test('acota el ajuste negativo cuando el TDEE baja tras el cierre semanal', async ({ page, request }) => {
    const annual = await seedNegativeAdjustment(request);
    expect((await readDay(page, 15, 1400)).adjK).toBe(-100);
    // El día 21 permite -200 (12 % de 1666.755…); el día 22, el TDEE
    // cae a 1666.492… incluso sin un nuevo cierre semanal.
    const day = await readDay(page, 22, 1300);
    expect(day.adjK).toBeCloseTo(-199.97905, 5);
    await expect(page.getByText('Ajuste: -199.97905 kcal', { exact: true })).toBeVisible();
    const plan = await annual();
    expect(plan[21]).toMatchObject({ kc: day.kc, adjK: day.adjK });
  });

  test('recalcula un snapshot anual previo al límite energético diario', async ({ page, request }) => {
    const annual = await seedNegativeAdjustment(request);
    await annual();
    const legacy = await request.put('/fixture/kan123/legacy-snapshot');
    expect(legacy.status()).toBe(204);

    await readDay(page, 22, 1300);
    expect((await annual())[21].adjK).toBeCloseTo(-199.97905, 5);
  });
});
