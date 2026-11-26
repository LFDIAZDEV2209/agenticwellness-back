const { defineConfig } = require('@playwright/test');

if (!process.env.COP_KAN131_TEST_DB_CONNECTION) {
  throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION con una BD PostgreSQL local efímera.');
}

module.exports = defineConfig({
  testMatch: 'kan123.spec.cjs',
  workers: 1,
  use: { baseURL: 'http://127.0.0.1:51369' },
  webServer: {
    command: 'dotnet run --no-launch-profile --urls http://127.0.0.1:51369',
    url: 'http://127.0.0.1:51369/fixture',
    timeout: 180_000,
  },
});
