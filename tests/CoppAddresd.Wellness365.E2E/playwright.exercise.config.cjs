const { defineConfig } = require('@playwright/test');

const app = process.env.COP_KAN130_APP_DIR;
if (!app || !process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('Configura COP_KAN130_APP_DIR y COP_KAN131_TEST_DB_CONNECTION para probar Ejercicio 365.');

const api = 'http://127.0.0.1:51425';
const mobile = 'http://127.0.0.1:51426';
module.exports = defineConfig({
  testMatch: 'exercise.spec.cjs',
  workers: 1,
  use: { baseURL: api },
  webServer: [{
    command: 'dotnet run --no-launch-profile --urls ' + api,
    url: api + '/fixture',
    timeout: 180_000,
  }, {
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51426 --strictPort',
    cwd: app,
    url: mobile,
    env: { VITE_GATEWAY_BASE_URL: api, VITE_WEB_SAME_ORIGIN: 'true' },
    timeout: 180_000,
  }],
});
