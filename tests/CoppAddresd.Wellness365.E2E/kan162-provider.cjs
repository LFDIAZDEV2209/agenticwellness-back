const { createServer } = require('node:http');

let scenario = { status: 503, body: {} };
let calls = [];
let release = null;

function reply(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  res.end(JSON.stringify(body));
}

async function read(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > 16_384) throw new Error('Payload de fixture demasiado grande');
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}

// Sustituye solo al proveedor. No aplica, valida ni audita propuestas.
createServer(async (req, res) => {
  try {
    switch (req.method + ' ' + req.url) {
      case 'GET /health':
        return reply(res, 200, { ok: true });
      case 'PUT /fixture/scenario':
        release?.();
        release = null;
        scenario = await read(req);
        calls = [];
        return reply(res, 200, { ok: true });
      case 'GET /fixture/calls':
        return reply(res, 200, calls);
      case 'POST /fixture/release':
        release?.();
        release = null;
        return reply(res, 200, { ok: true });
      case 'POST /proposals': {
        const { status, body, hold, raw } = scenario;
        calls.push(await read(req));
        if (hold) await new Promise(resolve => { release = resolve; });
        if (typeof raw === 'string') {
          res.writeHead(status, { 'Content-Type': 'application/json' });
          return res.end(raw);
        }
        return reply(res, status, body);
      }
      default:
        return reply(res, 404, {});
    }
  } catch {
    if (!res.destroyed) reply(res, 400, {});
  }
}).listen(51463, '127.0.0.1');
