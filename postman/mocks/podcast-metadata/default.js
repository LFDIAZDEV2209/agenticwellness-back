// Contrato local ai-service a977e33. Solo texto editorial sintético.
const http = require('http');
http.createServer((req, res) => {
  const send = (status, body) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  if (req.method !== 'POST' || req.url !== '/internal/media/podcast-metadata') return send(501, { detail: 'Fuera de esta simulación' });
  if (req.headers['x-internal-key'] !== 'simulation-only-key') return send(401, { detail: 'Clave inválida' });
  const body = req.body;
  if (!body || Object.keys(body).sort().join(',') !== 'context,language,title' || !['es', 'en'].includes(body.language)
      || typeof body.title !== 'string' || !body.title.trim() || body.title.length > 200
      || typeof body.context !== 'string' || body.context.length > 16000) return send(422, { detail: 'Contrato inválido' });
  if (req.headers['x-mock-response-code'] === '503') return send(503, { detail: 'Sugerencia no disponible' });
  return send(200, { status: 'ok', isEstimate: true,
    draft: { description: body.language === 'en' ? 'Editorial draft for human review.' : 'Borrador editorial para revisión humana.',
      category: 'Habitos', tags: body.language === 'en' ? ['consistency', 'habits'] : ['constancia', 'hábitos'] },
    model: 'postman-podcast-synthetic', generatedAt: new Date().toISOString() });
}).listen(process.env.PORT);
