const { defineConfig } = require('@playwright/test');
const base = require('./playwright.config.cjs');

if (!process.env.COP_KAN131_TEST_DB_CONNECTION || !process.env.COP_KAN130_APP_DIR || !process.env.COP_KAN130_ERP_DIR) {
  throw new Error('Configura COP_KAN131_TEST_DB_CONNECTION, COP_KAN130_APP_DIR y COP_KAN130_ERP_DIR para KAN-127.');
}

module.exports = defineConfig(base, { testMatch: 'kan127.spec.cjs' });
