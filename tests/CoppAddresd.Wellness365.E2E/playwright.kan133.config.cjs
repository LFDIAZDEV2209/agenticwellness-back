const { defineConfig } = require('@playwright/test');

if (!process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION para KAN-133.');

const api = 'http://127.0.0.1:51391';
const erp = process.env.COP_KAN130_ERP_DIR;
module.exports = defineConfig({
  workers: 1,
  testMatch: 'kan133.spec.cjs',
  use: { baseURL: api, trace: 'retain-on-failure' },
  webServer: [{
    command: 'dotnet run -c Release' + (process.env.COP_KAN133_NO_BUILD ? ' --no-build' : '') +
      ' --no-launch-profile --urls ' + api,
    url: api + '/fixture',
    timeout: 180_000,
    env: { Logging__LogLevel__Default: 'Warning' },
  }, ...(erp ? [{
    command: 'node node_modules/next/dist/bin/next dev --webpack -H 127.0.0.1 -p 51393',
    cwd: erp,
    url: 'http://127.0.0.1:51393/login',
    timeout: 180_000,
    env: { NEXT_PUBLIC_GATEWAY_URL: api, NEXT_TELEMETRY_DISABLED: '1' },
  }] : [])],
});
