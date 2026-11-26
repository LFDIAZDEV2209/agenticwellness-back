const { defineConfig } = require('@playwright/test');
const base = require('./playwright.config.cjs');

const app = process.env.COP_KAN130_APP_DIR;
if (!app || !process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('KAN-132 requiere el checkout de Antares y PostgreSQL efímero.');

const noBuild = process.env.COP_KAN132_NO_BUILD === '1' ? ' --no-build' : '';

module.exports = defineConfig({
  ...base,
  testMatch: 'kan132.spec.cjs',
  testIgnore: [],
  webServer: [{
    ...base.webServer[0],
    command: 'dotnet run --configuration Release' + noBuild + ' --no-launch-profile --urls ' + base.use.baseURL,
    stdout: 'pipe',
    env: { Logging__LogLevel__Default: 'Warning' },
  }, {
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51362 --strictPort',
    cwd: app,
    url: 'http://127.0.0.1:51362',
    env: { VITE_GATEWAY_BASE_URL: base.use.baseURL, VITE_WEB_SAME_ORIGIN: 'true' },
    timeout: 180_000,
  }],
});
