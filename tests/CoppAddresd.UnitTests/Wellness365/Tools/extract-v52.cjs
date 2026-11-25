// Solo desarrollo. Uso: node <ruta-a-este-script> <raíz del kit v5.2 extraído>.
const { createHash } = require('node:crypto');
const { readFileSync, writeFileSync } = require('node:fs');
const path = require('node:path');

const root = process.argv[2];
if (!root) throw new Error('Falta la raíz del kit v5.2.');
const kit = path.resolve(root);
const repo = path.resolve(__dirname, '../../../..');
const fixtures = path.resolve(__dirname, '../Fixtures');
const catalog = path.join(repo, 'src/CoppAddresd.Domain/Wellness365/Reference');
const hashes = {
  'engine/engine.js': 'd4ea07adc07876bcc57e1e35851dd887485b64768877a366b698244025fb061f',
  'engine/catalog.json': '0061df83ed3f6a2804a594679fc26ced07a561657ed1cd0f45fa154ade241f64',
  'engine/guide.json': 'e3a6cb94655cda96e745c9c09f7fb167081243d5ab9a82698807045149a357f5',
  'test/run.js': 'f4c176c453a2fd4644c80ecd1969616f141aa8ed3b280163465dc76e8dc4a3a7',
  'test/people.json': 'b479b5ad29d33c041fb429780079b6d638c5b51274bac1dbe4a0342153bc91e0',
  'test/golden.json': '9d66691d0e40f13ec87f9485631122965c547bafa99b83169b8b24a2b5223fcc'
};
for (const [file, expected] of Object.entries(hashes)) {
  const actual = createHash('sha256').update(readFileSync(path.join(kit, file))).digest('hex');
  if (actual !== expected) throw new Error(`${file}: SHA-256 ${actual}, esperado ${expected}`);
}

const engine = require(path.join(kit, 'engine/engine.js'));
const people = require(path.join(kit, 'test/people.json'));
const defaults = { ver: 1, log: {}, weigh: .8, adhFut: .85, adhBase: .8, met: 0, fit: 0, pace: 'm' };
const read = file => readFileSync(path.join(kit, file));
for (const file of ['people.json', 'golden.json']) writeFileSync(path.join(fixtures, file), read(`test/${file}`));
for (const file of ['catalog.json', 'guide.json']) writeFileSync(path.join(catalog, file), read(`engine/${file}`));
const foods = Object.fromEntries(Object.entries(engine.FD).map(
  ([id, [es, en, kcal, protein, carbs, fat, group, unitGrams, unitEs, unitEn]]) =>
    [id, { es, en, kcal, protein, carbs, fat, group, unitGrams, unitEs, unitEn }]
));
writeFileSync(path.join(catalog, 'food-and-templates.json'),
  JSON.stringify({ foods, templates: engine.TPL }, null, 2) + '\n');

const exercise = r => ({
  reasons: r.why, paused: r.pause, boost: r.boost, swapForPain: r.swapPain, simple: r.simple,
  items: r.ex.ex.map(o => ({
    id: o.id, tag: o.tag, newlyUnlocked: o.nw, minutes: o.m,
    strength: o.sets === undefined ? null : {
      sets: o.sets, reps: o.reps, restSeconds: o.rest, holdSeconds: o.hold, repsInReserve: o.rir
    }
  }))
});
const plans = [];
const histories = [];
for (const raw of people) {
  const p = { ...defaults, ...raw };
  const simulation = engine.simulate(p);
  const days = [1, 8, 29, 30, 60, 90, 120, 180, 200, 300, 365].map(day => ({ day, swaps: null }));
  days.push({ day: 30, swaps: [1, 2, 3, 4] });
  for (const { day, swaps } of days) {
    const { r, day: menu } = engine.planDay(p, day, swaps);
    plans.push({ id: p.id, day, swaps, exercise: exercise(r),
      menu: { meals: menu.meals.map(m => ({ index: m.t, items: m.its })),
        totals: menu.tot, deviation: menu.dev } });
  }
  const r = simulation.recs[p.day - 1];
  histories.push({ id: p.id, weeks: engine.estWeeks(p), reached: simulation.reached,
    today: { weight: r.w, endWeight: r.wEnd, tdee: r.tdee, fiber: r.fiber,
      water: r.water, calorieAdjustment: r.adjK },
    events: simulation.ev });
}
for (const [file, data] of [['reference-plans.json', plans], ['reference-histories.json', histories]]) {
  writeFileSync(path.join(fixtures, file), '[\n' + data.map(x => JSON.stringify(x)).join(',\n') + '\n]\n');
}
