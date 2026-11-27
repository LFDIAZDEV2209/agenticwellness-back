const { defineConfig } = require('@playwright/test');

const app = process.env.COP_KAN130_APP_DIR;
if (!app || !process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('KAN-162 requiere el checkout de Antares y PostgreSQL local efímero.');

const api = 'http://127.0.0.1:51461';
const appOrigin = 'http://127.0.0.1:51462';
const provider = 'http://127.0.0.1:51463';
const noBuild = process.env.COP_KAN162_NO_BUILD === '1' ? ' --no-build' : '';

module.exports = defineConfig({
  workers: 1,
  testMatch: 'kan162.spec.cjs',
  timeout: 60_000,
  use: { baseURL: api, trace: 'retain-on-failure' },
  metadata: { appOrigin, provider },
  webServer: [{
    command: 'node kan162-provider.cjs',
    url: provider + '/health',
  }, {
    command: 'dotnet run -c Release' + noBuild + ' --no-launch-profile --urls ' + api,
    url: api + '/fixture',
    timeout: 180_000,
    env: {
      Logging__LogLevel__Default: 'Warning',
      Motor365Ai__EnableLocalProposals: 'true',
      Motor365Ai__RequestsPerMinute: '100',
      Motor365Ai__ProposalUrl: provider + '/proposals',
    },
  }, {
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51462 --strictPort',
    cwd: app,
    url: appOrigin,
    env: { VITE_GATEWAY_BASE_URL: api, VITE_WEB_SAME_ORIGIN: 'true' },
    timeout: 180_000,
  }],
});
