const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testMatch: 'kan129.spec.cjs',
  workers: 1,
  retries: 0,
  use: { baseURL: 'http://127.0.0.1:51429' },
  webServer: {
    command: 'dotnet run --configuration Release --no-launch-profile --urls http://127.0.0.1:51429',
    url: 'http://127.0.0.1:51429/fixture',
    timeout: 180_000,
    stdout: 'ignore',
  },
});
