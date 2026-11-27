const { defineConfig } = require('@playwright/test');

const erp = process.env.COP_KAN130_ERP_DIR;
const app = process.env.COP_KAN130_APP_DIR;
if (!erp || !app || !process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('Configura COP_KAN130_APP_DIR, COP_KAN130_ERP_DIR y COP_KAN131_TEST_DB_CONNECTION para KAN-150.');

const api = 'http://127.0.0.1:51350';
const erpUrl = 'http://127.0.0.1:51351';

module.exports = defineConfig({
  testMatch: 'kan150.spec.cjs',
  workers: 1,
  timeout: 60_000,
  use: { baseURL: api, trace: 'retain-on-failure' },
  webServer: [{
    command: 'dotnet run -c Release --no-launch-profile --urls ' + api,
    url: api + '/fixture',
    timeout: 180_000,
    env: { Logging__LogLevel__Default: 'Warning', COP_KAN128_DIAGNOSTICS: '1' },
  }, {
    command: 'node node_modules/next/dist/bin/next dev --webpack -H 127.0.0.1 -p 51351',
    cwd: erp,
    url: erpUrl + '/login',
    timeout: 180_000,
    env: { NEXT_PUBLIC_GATEWAY_URL: api, NEXT_TELEMETRY_DISABLED: '1' },
  }, {
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51352 --strictPort',
    cwd: app,
    url: 'http://127.0.0.1:51352',
    timeout: 180_000,
    env: { VITE_GATEWAY_BASE_URL: api, VITE_WEB_SAME_ORIGIN: 'true' },
  }],
});
