const { test, expect } = require('@playwright/test');
const people = require('../CoppAddresd.UnitTests/Wellness365/Fixtures/people.json');
const golden = require('../CoppAddresd.UnitTests/Wellness365/Fixtures/golden.json');

// UNL de engine.js v5.2. El catálogo HTTP no publica las semanas de desbloqueo.
const unlockWeek = {
  walk: 1, bike: 1, mob: 1, str: 1, breath: 1, bal: 1, chair: 1, wall: 1, bridge: 1, row: 1, dead: 1,
  tri: 2, curl: 2, ellip: 3, calf: 3, kpush: 4, bird: 4, drow: 5, press: 7, hill: 8,
  plank: 9, lunge: 10, int: 14, step: 16, gob: 20, rdl: 26,
};

const provenance = { engine_ver: '5.2', params_version: 1 };
const start = Date.UTC(2026, 0, 1, 18);

async function requireStatus(response, expected, operation) {
  if (response.status() !== expected) {
    throw new Error(`${operation}: HTTP ${response.status()}, esperado ${expected}: ${(await response.text()).slice(0, 500)}`);
  }
  return response;
}

async function seed(request, { age, fit } = {}) {
  await requireStatus(await request.post('/fixture/reset', { data: { postgres: true, age, fit } }), 204, 'fixture PostgreSQL');
  const { accessToken, patientId } = await (await requireStatus(
    await request.get('/fixture/session/app'), 200, 'sesión de paciente')).json();
  const path = `/v1/people/${patientId}`;
  return async (suffix, { method = 'GET', data, status = 200 } = {}) => {
    const operation = `${method} ${suffix || '/people/{id}'}`;
    const response = await request.fetch(path + suffix, {
      method, data, headers: { Authorization: `Bearer ${accessToken}` },
    });
    return (await requireStatus(response, status, operation)).json();
  };
}

async function atDay(request, day) {
  const instant = new Date(start + (day - 1) * 86_400_000).toISOString();
  await requireStatus(await request.put('/fixture/clock', { data: { instant } }), 204, 'reloj de la fixture');
}

test.describe('KAN-129 · paridad y reglas por HTTP', () => {
  test.beforeAll(() => {
    if (!process.env.COP_KAN131_TEST_DB_CONNECTION) {
      throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION con PostgreSQL local efímero.');
    }
  });

  for (const { id, age, fit, sex, h, w0, waist0, target, act, goal, pace, veg, nolac } of people) {
    test(`${id}: golden del día 1 y 365 días del plan real`, async ({ request }) => {
      const call = await seed(request, { age, fit });
      const profile = await call('', { method: 'PUT', data: { sex, h, w0, waist0, target, act, goal, pace, veg, nolac } });
      expect(profile.status).toMatchObject({ ok: true });
      const plan = await call('/plan');
      expect(plan).toMatchObject({ ...provenance, today: 1, ver: profile.ver });
      expect(plan.days).toHaveLength(365);
      const first = plan.days[0];
      const { w: _simulatedWeight, ...expected } = golden[id]['1'];
      expect({
        kc: first.kc, P: first.P, C: first.C, F: first.F,
        mode: first.mode, type: first.type, lvl: first.exercise.level,
        mins: first.exercise.minutes, ex: first.exercise.items.map(item => item.id).join(','),
      }, `${id}, día 1, campos equivalentes al golden v5.2`).toEqual(expected);
      const events = await call('/events');
      expect(events.filter(event => event.k === 'ajuste' || (event.delta ?? 0) !== 0),
        `${id}, sin pesajes no se ajusta la meta calórica`).toEqual([]);

      for (const [index, day] of plan.days.entries()) {
        const label = `${id}, día ${index + 1}`;
        expect(day, label).toMatchObject({ ...provenance, d: index + 1, w: null, pause: false });
        const macroCalories = 4 * day.P + 4 * day.C + 9 * day.F;
        expect(day.kc, `${label}, piso calórico`).toBeGreaterThanOrEqual(sex === 'M' ? 1500 : 1200);
        expect(Math.abs(macroCalories - day.kc) / day.kc, `${label}, tolerancia de macros`).toBeLessThanOrEqual(.03);
        expect(day.exercise.items, `${label}, cantidad de ejercicios`).toHaveLength(4);
        expect(day.exercise.minutes, `${label}, minutos`).toBeGreaterThanOrEqual(15);
        expect(day.exercise.minutes, `${label}, minutos`).toBeLessThanOrEqual(22);
        for (const { id: exercise } of day.exercise.items) {
          // Sin cierres no hay impulso por racha. Se usa la semana del ordinal, no la que devuelve la API.
          expect(unlockWeek[exercise], `${label}, desbloqueo de ${exercise}`).toBeLessThanOrEqual(Math.ceil((index + 1) / 7));
        }
      }

      expect(await call('/plan/2', { status: 422 })).toMatchObject({ code: 'future_day' });
      await atDay(request, 365);
      for (const d of [1, 30, 90, 180, 365]) {
        const day = await call(`/plan/${d}`);
        const label = `${id}, menú del día ${d}`;
        expect(day, label).toMatchObject({ ...provenance, d, w: null });
        expect(day.menu, label).toHaveLength(4);
        expect(Math.abs(day.totals.k - day.kc) / day.kc, label).toBeLessThanOrEqual(.08);
      }
    });
  }

  for (const [reason, age, data] of [['age', 17, {}], ['bmi', 35, { h: 170, w0: 48, target: 45 }]]) {
    test(`el plan real conserva el bloqueo ${reason}`, async ({ request }) => {
      const call = await seed(request, { age });
      if (reason === 'bmi') await call('', { method: 'PUT', data });
      for (const path of ['/plan', '/plan/1']) {
        expect(await call(path, { status: 409 })).toMatchObject({ code: 'plan_locked', status: { ok: false, code: reason } });
      }
    });
  }

  for (const [symptom, change, pausedDays] of [
    ['sueño bajo', { sleep: 4.5, energy: 3 }, []],
    ['dolor intenso', { pain: 8, ex: 0 }, [10, 11]],
    ['mareo', { sym: ['mareo'] }, [10]],
  ]) {
    test(`el cierre con ${symptom} cambia la sesión de los días siguientes`, async ({ request }) => {
      const call = await seed(request, { fit: 1 });
      await atDay(request, 12);
      const baseline = await call('/plan/10');
      expect(baseline).toMatchObject({ w: null, pause: false, exercise: { level: 1 } });
      const close = await call('/logs/9', {
        method: 'PUT', data: { adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], w: 77, ex: 1, ...change },
      });
      expect(close.ok).toBe(true);
      expect(await call('/plan/9')).toMatchObject({ w: 77, pause: false });

      for (const d of [10, 11, 12]) {
        const day = await call(`/plan/${d}`);
        expect(day, `${symptom}, día ${d}`).toMatchObject({ d, w: null, pause: pausedDays.includes(d) });
        if (pausedDays.includes(d)) {
          expect(day).toMatchObject({ train: false, exercise: { minutes: 0, items: [] } });
        } else {
          expect(day.exercise.items).toHaveLength(4);
          const lowered = symptom === 'sueño bajo' && d === 10;
          expect(day.exercise.level).toBe(baseline.exercise.level - Number(lowered));
        }
      }
    });
  }
});
