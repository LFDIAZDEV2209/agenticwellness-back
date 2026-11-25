const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  workers: 1,
  use: { baseURL: 'http://127.0.0.1:51361' },
  webServer: {
    command: 'dotnet run --no-launch-profile --urls http://127.0.0.1:51361',
    url: 'http://127.0.0.1:51361/fixture',
    timeout: 180_000,
  },
});
