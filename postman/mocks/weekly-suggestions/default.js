// Contrato local ai-service 612fdfe; solo opciones y hechos sintéticos.
const http = require('http');
http.createServer((req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  if (req.method !== 'POST' || req.url !== '/internal/program/weekly-plan-suggestions') return send(501, { detail: 'Fuera de esta simulación' });
  if (req.headers['x-internal-key'] !== 'simulation-only-key') return send(401, { detail: 'Clave inválida' });
  const body = req.body;
  if (!body || Object.keys(body).sort().join(',') !== 'endDate,facts,language,slots,startDate'
      || !['es', 'en'].includes(body.language) || !Array.isArray(body.facts) || body.facts.length > 60
      || !Array.isArray(body.slots) || body.slots.length > 14 || body.slots.some(s => !Array.isArray(s.options) || s.options.length > 10)) return send(422, { detail: 'Contrato inválido' });
  if (req.headers['x-mock-response-code'] === '503') return send(503, { detail: 'Sugerencias no disponibles' });
  const available = body.slots.filter(s => s.options.length > 0);
  const empty = body.facts.length === 0 || available.length === 0;
  return send(200, { status: empty ? 'insufficient' : 'ok', kind: 'weekly_plan_suggestions', isEstimate: true,
    suggestions: empty ? [] : available.slice(0, 5).map(s => ({ slotRef: s.ref, optionRef: s.options[0].ref,
      reason: `Alternativa simulada [${body.facts[0].ref}].`, sourceRefs: [body.facts[0].ref] })),
    model: empty ? null : 'postman-weekly-synthetic', generatedAt: new Date().toISOString() });
}).listen(process.env.PORT);
