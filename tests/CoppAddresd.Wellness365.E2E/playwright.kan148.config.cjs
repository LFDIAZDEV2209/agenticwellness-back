const { defineConfig } = require('@playwright/test');

const erp = process.env.COP_KAN130_ERP_DIR;
if (!erp || !process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('Configura COP_KAN130_ERP_DIR y COP_KAN131_TEST_DB_CONNECTION para KAN-148.');

const api = 'http://127.0.0.1:51348';
const erpUrl = 'http://127.0.0.1:51349';

module.exports = defineConfig({
  testMatch: 'kan148.spec.cjs',
  workers: 1,
  use: { baseURL: api, trace: 'retain-on-failure' },
  webServer: [{
    command: 'dotnet run -c Release --no-launch-profile --urls ' + api,
    url: api + '/fixture',
    timeout: 180_000,
    env: { Logging__LogLevel__Default: 'Warning' },
  }, {
    command: 'node node_modules/next/dist/bin/next dev --webpack -H 127.0.0.1 -p 51349',
    cwd: erp,
    url: erpUrl + '/login',
    timeout: 180_000,
    env: { NEXT_PUBLIC_GATEWAY_URL: api, NEXT_TELEMETRY_DISABLED: '1' },
  }],
});
