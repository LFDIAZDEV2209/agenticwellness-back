const { performance } = require('node:perf_hooks');
const { test, expect } = require('@playwright/test');

const db = process.env.COP_KAN131_TEST_DB_CONNECTION;

async function readJson(response, expected = 200) {
  const status = typeof response.status === 'function' ? response.status() : response.status;
  if (status !== expected) throw new Error(`HTTP ${status}, esperado ${expected}: ${(await response.text()).slice(0, 300)}`);
  return response.json();
}

async function session(request, client, reset = false) {
  if (reset) {
    const response = await request.post('/fixture/reset', { data: { postgres: true } });
    expect(response.status(), 'fixture PostgreSQL').toBe(204);
  }
  return readJson(await request.get(`/fixture/session/${client}`));
}

function browserApi(page, token) {
  return (url, method = 'GET', data = undefined) => page.evaluate(async ({ url, method, data, token }) => {
    const response = await fetch(url, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(data ? { 'Content-Type': 'application/json' } : {}) },
      body: data && JSON.stringify(data),
    });
    const body = await response.json();
    document.querySelector('[role=status]').textContent = response.ok
      ? [response.status, body.ver, body.params_version].filter(v => v != null).join(' · ')
      : `${response.status} · ${body.code}`;
    return { status: response.status, body };
  }, { url, method, data, token });
}

async function currentPlan(call, path, check) {
  await expect.poll(async () => {
    const result = await call(path);
    // El backend puede terminar un recálculo en background. Un 200 obsoleto nunca se reintenta.
    if (result.status === 503 && result.body.retryable === true) return false;
    expect(result.status, path).toBe(200);
    check(result.body);
    return true;
  }, { timeout: 15_000, message: `recálculo disponible en ${path}` }).toBe(true);
}

test.describe('KAN-128 · frescura y carga del Motor 365', () => {
  test.beforeAll(() => {
    if (!db) throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION con una BD PostgreSQL local efímera.');
  });

  test('perfil y cierre no sirven una revisión anterior al paciente', async ({ page, request }) => {
    const app = await session(request, 'app', true);
    await page.goto('/fixture');
    const call = browserApi(page, app.accessToken);
    const person = `/v1/people/${app.patientId}`;

    // Publicar una revisión de partida mediante escritura de producción, no fabricando el snapshot.
    const initial = await call(person, 'PUT', { w0: 82 });
    expect(initial).toMatchObject({ status: 200, body: { status: { ok: true } } });
    let initialKcal;
    await currentPlan(call, person + '/plan', plan => {
      expect(plan).toMatchObject({ id: app.patientId, ver: initial.body.ver });
      expect(plan.days).toHaveLength(365);
      initialKcal = plan.days[0].kc;
    });

    const profile = await call(person, 'PUT', { act: 4, ver: initial.body.ver });
    expect(profile).toMatchObject({ status: 200, body: { status: { ok: true }, ver: initial.body.ver + 1 } });
    await currentPlan(call, person + '/plan', plan => {
      expect(plan.ver).toBe(profile.body.ver);
      expect(plan.days[0].kc).toBeGreaterThan(initialKcal);
    });

    const log = await call(person + '/logs/1', 'PUT', { adh: .9, sleep: 7, energy: 4, pain: 0, sym: [], w: 79, ex: 1 });
    expect(log).toMatchObject({ status: 200, body: { ok: true, ver: profile.body.ver + 1 } });
    await currentPlan(call, person + '/plan', plan => {
      expect(plan.ver).toBe(log.body.ver);
      expect(plan.days[0]).toMatchObject({ d: 1, w: 79 });
    });
    await currentPlan(call, person + '/plan/1', day => {
      expect(day).toMatchObject({ d: 1, w: 79 });
      expect(day.menu).toHaveLength(4);
    });
    await expect(page.getByRole('status')).toHaveText(/^200 · \d+$/);
  });

  test('publicar parámetros invalida los resultados anual y diario sin otro cambio de perfil o cierre', async ({ page, request }) => {
    const app = await session(request, 'app', true);
    const erp = await session(request, 'erp-admin');
    await page.goto('/fixture');
    const call = browserApi(page, app.accessToken);
    const administer = browserApi(page, erp.accessToken);
    const personPath = `/v1/people/${app.patientId}`;
    const planPath = personPath + '/plan';
    expect(await call(personPath, 'PUT', { w0: 81 })).toMatchObject({
      status: 200, body: { status: { ok: true } },
    });
    const baseline = await call(planPath);
    const baselineDay = await call(planPath + '/1');
    expect(baseline.status).toBe(200);
    expect(baselineDay).toMatchObject({ status: 200, body: { params_version: baseline.body.params_version } });

    const settings = await administer('/v1/engine/params');
    expect(settings).toMatchObject({ status: 200, body: { params_version: baseline.body.params_version } });
    const updated = await administer('/v1/engine/params', 'PUT', {
      params: { floorF: settings.body.params.floorF === 2000 ? 2100 : 2000 },
      reason: 'Fixture KAN-128',
      expectedVersion: baseline.body.params_version,
    });
    const version = baseline.body.params_version + 1;
    expect(updated).toMatchObject({ status: 200, body: { params_version: version } });
    await Promise.all([
      currentPlan(call, planPath, plan => {
        expect.soft(plan.params_version, 'versión anual tras publicar parámetros').toBe(version);
        expect(plan.days).toHaveLength(365);
        expect.soft(plan.days.every(d => d.params_version === version), 'versión de los 365 días').toBe(true);
      }),
      currentPlan(call, planPath + '/1', day => {
        expect.soft(day.params_version, 'versión diaria tras publicar parámetros').toBe(version);
        expect.soft(day.kc, 'objetivo diario recalculado').not.toBe(baselineDay.body.kc);
      }),
    ]);
  });

  test('una lectura diaria prepara el snapshot anual en background y el cambio de día no lo vuelve obsoleto', async ({ page, request }) => {
    const warmup = await session(request, 'app', true);
    await page.goto('/fixture');
    const setup = browserApi(page, warmup.accessToken);
    expect((await setup(`/v1/people/${warmup.patientId}`, 'PUT', { w0: 81 })).status).toBe(200);
    const app = await session(request, 'app', true);
    const erp = await session(request, 'erp-admin');
    const call = browserApi(page, app.accessToken);
    const parameters = await browserApi(page, erp.accessToken)('/v1/engine/params');
    expect(parameters.status).toBe(200);
    const version = parameters.body.params_version;
    const plan = `/v1/people/${app.patientId}/plan`;
    const snapshots = async () => readJson(await request.get('/fixture/kan128/snapshots'));
    expect(await snapshots()).toEqual([]);

    await currentPlan(call, plan + '/1', day => expect(day.params_version).toBe(version));
    // Sin consultar el año, el worker debe publicar la revisión vigente.
    await expect.poll(snapshots, { timeout: 10_000 }).toEqual([version]);
    await currentPlan(call, plan, annual => expect(annual.today).toBe(1));
    expect((await request.put('/fixture/clock', { data: { instant: '2026-01-02T18:00:00Z' } })).status()).toBe(204);
    await currentPlan(call, plan, annual => {
      expect(annual.today).toBe(2);
      expect(annual.params_version).toBe(version);
    });
  });

  test('un snapshot cacheado no evita el bloqueo y la traza no contiene identificadores directos', async ({ page, request }) => {
    const app = await session(request, 'app', true);
    await page.goto('/fixture');
    const call = browserApi(page, app.accessToken);
    const person = `/v1/people/${app.patientId}`;
    expect((await call(person, 'PUT', { w0: 81 })).status).toBe(200);
    const plan = person + '/plan';
    await currentPlan(call, plan, annual => expect(annual.ver).toBeGreaterThan(0));
    await currentPlan(call, plan, annual => expect(annual.ver).toBeGreaterThan(0));
    const snapshotVersions = await readJson(await request.get('/fixture/kan128/snapshots'));
    expect(snapshotVersions).toHaveLength(1);

    const goodTrace = await readJson(await request.get('/fixture/kan128/annual-trace'));
    expect(goodTrace.tags['person.pseudonym']).toMatch(/^[a-f0-9]{32}$/);
    expect(goodTrace.tags['cache.hit']).toBe(true);
    expect(goodTrace.tags['engine.version']).toBeTruthy();
    expect(JSON.stringify(goodTrace)).not.toContain(app.patientId);

    expect((await request.post('/fixture/kan128/withdraw-consent')).status()).toBe(204);
    for (const path of [plan, plan + '/1']) {
      expect(await call(path)).toMatchObject({ status: 409, body: { code: 'plan_locked', status: { code: 'terms' } } });
    }
    const blockedTrace = await readJson(await request.get('/fixture/kan128/annual-trace'));
    expect(blockedTrace.tags).toMatchObject({ 'person.pseudonym': goodTrace.tags['person.pseudonym'], outcome: 'blocked' });
    expect(await readJson(await request.get('/fixture/kan128/snapshots'))).toEqual(snapshotVersions);
  });

  test('carga 1.000 planes de 365 días y mide p95 y hit ratio de caché', async ({ request, baseURL }, testInfo) => {
    test.skip(process.env.COP_KAN128_LOAD !== '1', 'Carga explícita con COP_KAN128_LOAD=1');
    test.setTimeout(180_000);
    const admin = await session(request, 'erp-admin', true);
    const ids = await readJson(await request.post('/fixture/kan128/cohort'));
    expect(new Set(ids).size).toBe(1000);
    const headers = { Authorization: `Bearer ${admin.accessToken}` };
    const counters = async () => readJson(await request.get('/fixture/kan128/cache-counters'));
    const settings = await readJson(await request.get('/v1/engine/params', { headers }));
    const version = settings.params_version;
    const concurrency = 8;

    // Calentar runtime y conexiones fuera de la cohorte y de las mediciones.
    const warmup = await session(request, 'app');
    for (const path of ['/plan', '/plan/1']) {
      const response = await fetch(baseURL + '/v1/people/' + warmup.patientId + path, {
        headers: { Authorization: 'Bearer ' + warmup.accessToken },
        signal: AbortSignal.timeout(15_000),
      });
      await readJson(response);
    }

    async function sample(path, annual) {
      let next = 0;
      const latencies = [];
      let staleResponses = 0;
      const errors = {};
      await Promise.all(Array.from({ length: concurrency }, async () => {
        while (next < ids.length) {
          const id = ids[next++];
          const start = performance.now();
          const response = await fetch(baseURL + '/v1/people/' + id + path, {
            headers, signal: AbortSignal.timeout(15_000),
          });
          if (!response.ok) {
            errors[response.status] = (errors[response.status] ?? 0) + 1;
            await response.arrayBuffer();
            continue;
          }
          const body = await response.json();
          latencies.push(performance.now() - start);
          if (annual) {
            if (body.id !== id || body.days?.length !== 365 || body.days.some((day, index) => day.d !== index + 1)) {
              throw new Error('Identidad o secuencia anual incorrecta');
            }
          } else if (body.d !== 1 || body.menu?.length !== 4) {
            throw new Error('Día o número de comidas incorrecto');
          }
          if (body.params_version !== version || body.engine_ver !== settings.engine_ver ||
              (annual && body.days.some(day => day.params_version !== version))) staleResponses++;
        }
      }));
      latencies.sort((a, b) => a - b);
      return {
        requests: ids.length, successes: latencies.length, errors, staleResponses,
        p95Ms: latencies.length ? latencies[Math.ceil(.95 * latencies.length) - 1] : null,
      };
    }

    const before = await counters();
    const firstAnnual = await sample('/plan', true);
    const firstDay = await sample('/plan/1', false);
    const afterFirst = await counters();
    const repeatedAnnual = await sample('/plan', true);
    const repeatedDay = await sample('/plan/1', false);
    const afterRepeat = await counters();
    function cacheDelta(from, to) {
      const hits = to.hits - from.hits;
      const misses = to.misses - from.misses;
      return { hits, misses, hitRatio: hits + misses ? hits / (hits + misses) : null };
    }
    const report = {
      people: ids.length, daysPerPlan: 365, concurrency, paramsVersion: version,
      engineVersion: settings.engine_ver, firstAnnual, firstDay, repeatedAnnual, repeatedDay,
      firstCache: cacheDelta(before, afterFirst), repeatedCache: cacheDelta(afterFirst, afterRepeat),
    };
    console.log(JSON.stringify(report));
    await testInfo.attach('kan128-load.json', { body: JSON.stringify(report, null, 2), contentType: 'application/json' });

    for (const [name, result, limit] of [
      ['primer plan', firstAnnual, 300], ['primer día', firstDay, 150],
      ['plan repetido', repeatedAnnual, 300], ['día repetido', repeatedDay, 150],
    ]) {
      expect.soft(result.errors, `errores HTTP: ${name}`).toEqual({});
      expect.soft(result.p95Ms ?? Infinity, `p95 ${name} (ms)`).toBeLessThan(limit);
      expect.soft(result.staleResponses, `respuestas obsoletas: ${name}`).toBe(0);
    }
    expect.soft(report.firstCache.misses, 'primer acceso registra misses').toBeGreaterThan(0);
    expect.soft(report.repeatedCache.hits, 'lecturas repetidas registran hits').toBeGreaterThan(0);
  });
});
