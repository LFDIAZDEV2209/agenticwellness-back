const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testMatch: 'kan128.spec.cjs',
  workers: 1,
  retries: 0,
  use: { baseURL: 'http://127.0.0.1:51368' },
  webServer: {
    command: 'dotnet run --configuration Release --no-launch-profile --urls http://127.0.0.1:51368',
    url: 'http://127.0.0.1:51368/fixture',
    env: { COP_KAN128_DIAGNOSTICS: '1' },
    stdout: 'ignore',
    stderr: 'pipe',
    timeout: 180_000,
  },
});
