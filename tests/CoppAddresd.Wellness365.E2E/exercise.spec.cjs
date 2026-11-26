const { test, expect } = require('@playwright/test');

const people = '/api/v1/people';
const unlocks = Object.fromEntries([
  [1, 'walk bike mob str breath bal chair wall bridge row dead'], [2, 'tri curl'],
  [3, 'ellip calf'], [4, 'kpush bird'], [5, 'drow'], [7, 'press'], [8, 'hill'],
  [9, 'plank'], [10, 'lunge'], [14, 'int'], [16, 'step'], [20, 'gob'], [26, 'rdl'],
].flatMap(([week, ids]) => ids.split(' ').map(id => [id, week])));
const phases = ['Adaptación', 'Construcción', 'Intensificación', 'Consolidación'];
const instant = day => new Date(Date.UTC(2026, 0, day, 18)).toISOString();
const phaseFor = week => week <= 8 ? 0 : week <= 20 ? 1 : week <= 36 ? 2 : 3;

async function seed(request, data) {
  expect((await request.post('/fixture/reset', { data: { postgres: true, ...data } })).ok()).toBeTruthy();
  return (await request.get('/fixture/session/app')).json();
}

async function setDay(request, day) {
  expect((await request.put('/fixture/clock', { data: { instant: instant(day) } })).ok()).toBeTruthy();
}

async function openExercise(browser, session, lang, day) {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, locale: lang });
  await context.addInitScript(({ userId, lang }) => {
    localStorage.setItem('antares_lang', lang);
    const at = new Date().toISOString();
    localStorage.setItem('copp.wellness-privacy.audit.v1.' + at + '.00000000.fixture',
      JSON.stringify({ actor: userId, action: 'accepted', at, acceptedAt: at, scope: 'sensitive-data', version: '2026-11-25-draft.1' }));
  }, { userId: session.userId, lang });
  await context.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith(people + '/')) {
      return route.fulfill({ response: await route.fetch({ url: 'http://127.0.0.1:51425' + path }) });
    }
    const respond = json => route.fulfill({ json });
    if (path === '/api/auth/refresh') return respond({ accessToken: session.accessToken, expiresIn: 1200 });
    if (path === '/api/auth/me') return respond({ id: session.userId, email: 'qa@example.invalid',
      firstName: 'Persona', lastName: 'QA', roles: [], permissions: [] });
    if (path === '/api/v1/me/patient-profile') return respond({ patientId: session.patientId,
      firstName: 'Persona', lastName: 'QA' });
    if (path === '/api/v1/me/account-legal-consents') return respond({
      locale: lang, enforcementEnabled: true, acceptanceRequired: false, documents: [] });
    if (path === '/api/v1/me/legal-consents') return respond({ locale: lang, scopes: [] });
    return respond([]);
  });
  const page = await context.newPage();
  try {
    await page.goto('http://127.0.0.1:51426');
    await expect(page.getByText(lang === 'en' ? `Day ${day} of 365` : `Día ${day} de 365`, { exact: true }))
      .toBeVisible({ timeout: 20_000 });
    await page.getByRole('button', { name: lang === 'en' ? /Exercise.*Today's workout/ : /Ejercicio.*Tu rutina de hoy/ }).click();
    return { page, context };
  } catch (error) {
    await context.close();
    throw error;
  }
}

for (const [goal, pattern, day, lang] of [
  [0, 'CFCFCMC', 57, 'es'], [1, 'PLJRFKR', 142, 'es'], [2, 'PLJHFKR', 365, 'en'],
]) {
  test(`objetivo ${goal}: 365 días del motor y sesión real en ${lang}`, async ({ browser, request }) => {
    test.setTimeout(90_000);
    const session = await seed(request, { goal });
    const headers = { Authorization: 'Bearer ' + session.accessToken };
    if (goal === 0) {
      // Crea una racha antes de un desbloqueo para probar que no lo adelanta.
      await setDay(request, 13);
      for (let d = 1; d <= 13; d++)
        expect((await request.put(people + '/me/logs/' + d, { headers, data: {
          adh: .95, sleep: 7, energy: 4, pain: 0, ex: true,
        } })).status()).toBe(204);
    }
    await setDay(request, day);
    const annual = await request.get('/v1/people/' + session.patientId + '/plan', { headers });
    expect(annual.ok()).toBeTruthy();
    const { days, today } = await annual.json();
    expect(today).toBe(day);
    expect(days).toHaveLength(365);
    const issues = [];
    const seen = new Set();
    for (const [index, entry] of days.entries()) {
      const week = Math.min(52, Math.floor(index / 7) + 1);
      const phase = phaseFor(week);
      const session = entry.exercise;
      const deload = week % 4 === 0;
      const expectedMinutes = Math.max(15, 15 + Math.round(7 * (week - 1) / 51) - (deload ? 2 : 0));
      const check = (passes, detail) => { if (!passes) issues.push(`Day ${index + 1}: ${detail}`); };
      check(entry.d === index + 1 && entry.wk === week && session.phase === phases[phase],
        `expected week ${week}, ${phases[phase]}; received ${entry.wk}, ${session.phase}`);
      check(entry.type === pattern[index % 7], `pattern ${entry.type}`);
      check(session.deload === deload && session.minutes === expectedMinutes, `duration ${session.minutes}, expected ${expectedMinutes}`);
      check(session.items.length === 4, `exercise count ${session.items.length}`);
      check(session.items.reduce((sum, item) => sum + item.minutes, 0) === session.minutes, 'item minutes do not add up');
      for (const item of session.items) {
        seen.add(item.id);
        check(unlocks[item.id] <= week && item.minutes >= 2, `invalid exercise ${item.id} (${item.minutes} min)`);
        if (item.sets != null)
          check(item.sets === [2, 2, 3, 3][phase] && item.reps === [8, 10, 12, 15][phase] &&
            item.rest === [45, 45, 60, 60][phase] && item.rir === (goal === 0 ? '2–3' : '1–2'),
            `wrong dose for ${item.id}: ${item.sets} × ${item.reps}, ${item.rest}s, RIR ${item.rir}`);
      }
    }
    expect.soft(issues.slice(0, 15), `${issues.length} annual exercise discrepancies`).toEqual([]);
    expect.soft([...seen]).toEqual(expect.arrayContaining(goal === 2 ? ['int', 'gob', 'rdl'] : ['walk', 'curl', 'ellip', 'plank']));

    const { page, context } = await openExercise(browser, session, lang, day);
    try {
      const entry = days[day - 1];
      const phase = lang === 'en' ? entry.exercise.phaseName.en : entry.exercise.phase;
      const meta = lang === 'en' ? `Day ${day} · ${entry.exercise.minutes} min · Phase ${phase}`
        : `Día ${day} · ${entry.exercise.minutes} min · Fase ${phase}`;
      await expect(page.getByText(meta, { exact: true })).toBeVisible();
      await expect(page.getByText(`${lang === 'en' ? 'Week' : 'Semana'} ${entry.wk}`, { exact: true }).first()).toBeVisible();
      const self = await request.get(people + '/me/plan', { headers });
      expect(self.ok()).toBeTruthy();
      const { exercise } = await self.json();
      expect(exercise.items.map(item => item.id)).toEqual(entry.exercise.items.map(item => item.id));
      const erp = await (await request.get('/fixture/session/erp')).json();
      const assigned = await request.get(people + '/' + session.patientId + '/plan', {
        headers: { Authorization: 'Bearer ' + erp.accessToken },
      });
      expect(assigned.ok()).toBeTruthy();
      expect((await assigned.json()).exercise).toEqual(exercise);
      const cards = page.getByRole('list', { name: lang === 'en' ? 'Exercise' : 'Ejercicio', exact: true }).locator(':scope > li');
      await expect(cards).toHaveCount(4);
      for (const [index, item] of exercise.items.entries()) {
        for (const language of ['es', 'en']) {
          expect(item.guide[language]).toMatchObject({ n: expect.any(String), s: expect.any(Array), b: expect.any(String) });
          expect(item.guide[language].s[0]).toBeTruthy();
          expect(item.description[language]).toBeTruthy();
        }
        const card = cards.nth(index);
        await expect(card.getByRole('heading', { name: item.guide[lang].n, exact: true })).toBeVisible();
        await expect(card.getByText(`${item.minutes} min`, { exact: true })).toBeVisible();
        await expect(card.getByText(item.description[lang], { exact: true })).toBeVisible();
        const dose = item.strength;
        if (dose) await expect(card.getByText(
          new RegExp(`${dose.sets} .*× ${dose.reps} .* ${dose.restSeconds} s · RIR ${dose.repsInReserve}`),
        )).toBeVisible();
        await card.locator('summary').click();
        await expect(card.getByText(item.guide[lang].s[0], { exact: true })).toBeVisible();
      }
      await page.getByRole('button', { name: lang === 'en' ? 'Start session' : 'Empezar sesión' }).click();
      await expect(page.getByRole('heading', { name: exercise.items[0].guide[lang].n })).toBeVisible();
      await expect(page.getByRole('timer')).toBeVisible();
    } finally {
      await context.close();
    }

    if (goal === 0) {
      const closure = await request.put(people + '/me/logs/' + day, { headers, data: {
        adh: .95, sleep: 7, energy: 4, pain: 7, ex: false,
      } });
      expect(closure.status()).toBe(204);
      await setDay(request, day + 1);
      const paused = await request.get(people + '/me/plan', { headers });
      expect(await paused.json()).toMatchObject({ status: { state: 'paused' }, exercise: null });
      const screen = await openExercise(browser, session, lang, day + 1);
      try {
        await expect(screen.page.getByRole('status')).toHaveText('Plan en pausa');
        await expect(screen.page.getByRole('button', { name: 'Empezar sesión' })).toHaveCount(0);
      } finally {
        await screen.context.close();
      }
    }
  });
}
