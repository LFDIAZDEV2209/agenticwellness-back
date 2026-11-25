import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const playwrightRoot = process.env.COP_PLAYWRIGHT_PROJECT;
const { chromium } = playwrightRoot
  ? await import(pathToFileURL(resolve(playwrightRoot, 'node_modules/playwright/index.mjs')))
  : await import('playwright');
const output = await mkdtemp(join(tmpdir(), 'copp-transactional-mail-'));
const templates = {
  'BIEN-01': ['Descargar la app', 'Download the app', '/download'],
  'ACT-01': ['Abrir Agentic Wellness IA', 'Open Agentic Wellness IA', '/platform'],
  'ACT-02': ['Descargar la app', 'Download the app', '/download'],
  'PWD-01': ['Crear nueva contraseña', 'Create a new password', '/reset'],
  'NUT-01': ['Ver mi plan completo', 'View my full plan', '/nutrition'],
  'NUT-02': ['Revisar cambios', 'Review changes', '/nutrition'],
  'INV-01': ['Actualizar inventario', 'Update inventory', '/inventory'],
};

try {
  execFileSync('dotnet', ['run', '--project', 'tools/WelcomeEmailPreview',
    '-p:AllowMissingPrunePackageData=true', '--', output, '--transactional'], { cwd: root, stdio: 'inherit' });
  const browser = await chromium.launch();
  try {
    for (const [code, [spanish, english, path]] of Object.entries(templates)) {
      for (const lang of ['es', 'en']) {
        const file = join(output, `${code}-${lang}.html`);
        await readFile(file);
        const page = await browser.newPage({ viewport: { width: 320, height: 850 } });
        try {
          await page.goto(pathToFileURL(file).href);
          assert.equal(await page.locator('html').getAttribute('lang'), lang);
          assert.equal(await page.getByRole('heading', { level: 1 }).count(), 1);
          const button = page.getByRole('link', { name: lang === 'es' ? spanish : english, exact: true });
          assert.equal(await button.count(), 1, `${code} ${lang}: primary link`);
          const target = new URL(await button.getAttribute('href'));
          assert.equal(target.origin, 'https://preview.example.invalid');
          assert.equal(target.pathname, path);
          assert.equal(await page.locator('script,iframe').count(), 0);
          for (const width of [320, 480, 768]) {
            await page.setViewportSize({ width, height: 850 });
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `${code} ${lang}: overflow at ${width}`);
          }
          assert(!/\{\{\w+\}\}/.test(await page.content()), `${code} ${lang}: leftover variable`);
          const mime = await readFile(join(output, `${code}-${lang}.eml`), 'utf8');
          assert.match(mime, /Content-Type: text\/plain/i);
          assert.match(mime, /Content-Type: text\/html/i);
        } finally {
          await page.close();
        }
      }
    }
  } finally {
    await browser.close();
  }
  console.log('14 correos verificados en HTML, MIME y tres anchos.');
} finally {
  await rm(output, { recursive: true, force: true });
}
