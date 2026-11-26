const { defineConfig } = require('@playwright/test');

const app = process.env.COP_KAN130_APP_DIR;
const erp = process.env.COP_KAN130_ERP_DIR;

module.exports = defineConfig({
  workers: 1,
  testIgnore: ['exercise.spec.cjs', 'kan128.spec.cjs', 'kan129.spec.cjs'],
  use: { baseURL: 'http://127.0.0.1:51361' },
  webServer: [{
    command: 'dotnet run --no-launch-profile --urls http://127.0.0.1:51361',
    url: 'http://127.0.0.1:51361/fixture',
    timeout: 180_000,
  }, ...(app && erp ? [
    {
      command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51362 --strictPort',
      cwd: app,
      url: 'http://127.0.0.1:51362',
      env: { VITE_GATEWAY_BASE_URL: 'http://127.0.0.1:51361', VITE_WEB_SAME_ORIGIN: 'true' },
      timeout: 180_000,
    },
    {
      command: 'node node_modules/next/dist/bin/next dev --webpack -H 127.0.0.1 -p 51363',
      cwd: erp,
      url: 'http://127.0.0.1:51363/login',
      env: { NEXT_PUBLIC_GATEWAY_URL: 'http://127.0.0.1:51361', NEXT_TELEMETRY_DISABLED: '1' },
      timeout: 180_000,
    },
  ] : [])],
});
