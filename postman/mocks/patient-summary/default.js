// Contrato local: ai-service 041a602, docs/patient-summary.md. Solo hechos sintéticos.
const http = require('http');
http.createServer((req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  if (req.method !== 'POST' || req.url !== '/internal/program/patient-summary') return send(501, { detail: 'Fuera de esta simulación' });
  if (req.headers['x-internal-key'] !== 'simulation-only-key') return send(401, { detail: 'Clave inválida' });
  const body = req.body;
  if (!body || Object.keys(body).sort().join(',') !== 'endDate,facts,language,startDate' || !['es', 'en'].includes(body.language)
      || !Array.isArray(body.facts) || body.facts.length > 60 || body.facts.some(f => !/^[a-zA-Z][a-zA-Z0-9_.:-]{0,79}$/.test(f.ref)
        || typeof f.text !== 'string' || !f.text.trim() || f.text.length > 2000)) return send(422, { detail: 'Contrato inválido' });
  if (req.headers['x-mock-response-code'] === '503') return send(503, { detail: 'Resumen de IA no disponible' });
  const empty = body.facts.length === 0;
  return send(200, { status: empty ? 'insufficient' : 'ok', kind: 'patient_summary', isEstimate: true,
    summary: empty ? '' : `Resumen simulado [${body.facts[0].ref}].`, sourceRefs: empty ? [] : [body.facts[0].ref], attention: [],
    model: empty ? null : 'postman-summary-synthetic', generatedAt: new Date().toISOString() });
}).listen(process.env.PORT);
