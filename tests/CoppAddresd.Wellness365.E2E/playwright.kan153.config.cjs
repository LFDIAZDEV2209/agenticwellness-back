const { defineConfig } = require('@playwright/test');

const endpoint = 'http://127.0.0.1:51381';
const app = process.env.COP_KAN130_APP_DIR;
const erp = process.env.COP_KAN130_ERP_DIR;
const gateway = 'http://127.0.0.1:51384';

module.exports = defineConfig({
  workers: 1,
  testMatch: 'kan153.spec.cjs',
  use: {
    baseURL: endpoint, channel: process.env.COP_KAN153_BROWSER_CHANNEL,
    trace: 'retain-on-failure', screenshot: 'only-on-failure',
  },
  webServer: [{
    command: `dotnet run --no-launch-profile --urls ${endpoint}`,
    url: endpoint + '/fixture',
    timeout: 180_000,
  }, ...(app && erp ? [
    {
      command: `dotnet run --no-launch-profile --project ../../src/Services/CoppAddresd.Gateway --urls ${gateway}`,
      url: gateway + '/health',
      env: {
        ReverseProxy__Routes__api__ClusterId: 'api',
        ReverseProxy__Routes__api__Match__Path: '/api/v1/{**catch-all}',
        ReverseProxy__Clusters__api__Destinations__fixture__Address: endpoint + '/',
      },
      timeout: 180_000,
    },
    {
      command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 51382 --strictPort',
      cwd: app,
      url: 'http://127.0.0.1:51382',
      env: { VITE_GATEWAY_BASE_URL: gateway, VITE_WEB_SAME_ORIGIN: 'true' },
      timeout: 180_000,
    },
    {
      command: 'node node_modules/next/dist/bin/next dev --webpack -H 127.0.0.1 -p 51383',
      cwd: erp,
      url: 'http://127.0.0.1:51383/login',
      env: { NEXT_PUBLIC_GATEWAY_URL: gateway, NEXT_TELEMETRY_DISABLED: '1' },
      timeout: 180_000,
    },
  ] : [])],
});
