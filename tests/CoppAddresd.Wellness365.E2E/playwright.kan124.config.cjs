const { defineConfig } = require('@playwright/test');
const base = require('./playwright.config.cjs');

if (!process.env.COP_KAN131_TEST_DB_CONNECTION)
  throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION para KAN-124.');

const app = process.env.COP_KAN130_APP_DIR;
const [api] = base.webServer;
const noBuild = process.env.COP_KAN124_NO_BUILD === '1' ? ' --no-build' : '';
module.exports = defineConfig({
  ...base,
  testMatch: 'kan124.spec.cjs',
  testIgnore: [],
  webServer: [{
    ...api,
    command: 'dotnet run --configuration Release' + noBuild + ' --no-launch-profile --urls ' + base.use.baseURL,
    stdout: 'pipe',
    env: { Logging__LogLevel__Default: 'Warning' },
  }, ...(app ? [{
    command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51362 --strictPort',
    cwd: app,
    url: 'http://127.0.0.1:51362',
    env: { VITE_GATEWAY_BASE_URL: base.use.baseURL, VITE_WEB_SAME_ORIGIN: 'true' },
    timeout: 180_000,
  }] : [])],
});
