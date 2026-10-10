import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'docs/ui-review/v3/reference');
const runtime = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE, '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const { chromium } = require(path.join(runtime, 'playwright'));
const source = 'D:/Code/design_handoff_winvaultwarden_v3/WinVaultWarden Prototype v3.dc.html';
await fs.mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1328, height: 888 }, deviceScaleFactor: 1, reducedMotion: 'reduce' });
const page = await context.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const records = [];

async function openReference(width, theme) {
  await page.goto(pathToFileURL(source).href, { waitUntil: 'networkidle' });
  await page.waitForFunction(() => typeof window.__dcSetProps === 'function');
  await page.evaluate(({ width }) => {
    window.__dcSetProps(window.__dcRootName(), {
      motion: '\u5173',
      winSize: width === 1024 ? '1024 \u00d7 720' : width === 760 ? '760 \u00d7 640' : '\u8ddf\u968f\u6d4f\u89c8\u5668',
    });
  }, { width });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForSelector('div[style*="transform-origin"]');
  await page.waitForTimeout(400);
  if (theme === 'dark') {
    await page.locator('[title="\u5207\u6362\u6df1\u8272 / \u6d45\u8272"]').click();
    await page.waitForTimeout(100);
  }
}

async function capture(width, theme, name) {
  const frame = page.locator('div[style*="transform-origin"]').first();
  const bounds = await frame.boundingBox();
  if (bounds?.width !== width) throw new Error(`Expected reference width ${width}, received ${bounds?.width}.`);
  await page.mouse.move(0, 0);
  await frame.screenshot({ path: path.join(out, `${width}-${theme}-${name}.png`), animations: 'disabled' });
  records.push({ width, theme, scene: name, bounds, file: `${width}-${theme}-${name}.png`, referenceOnly: true });
}

try {
  for (const width of [1280, 1024, 760]) {
    for (const theme of ['light', 'dark']) {
      await openReference(width, theme);
      await capture(width, theme, 'vault');
      await page.getByText('\u751f\u6210\u5668', { exact: true }).first().click();
      await capture(width, theme, 'generator');
      await page.getByText('\u5b89\u5168\u62a5\u544a', { exact: true }).first().click();
      await capture(width, theme, 'report');
      await page.locator('[title="\u8bbe\u7f6e"]').click();
      await capture(width, theme, 'settings-account');
      const securityTooltip = page.locator('[title="\u5b89\u5168"]');
      if (await securityTooltip.count()) await securityTooltip.click();
      else await page.getByText('\u5b89\u5168', { exact: true }).click();
      await capture(width, theme, 'settings-security');
      await page.getByText('Send', { exact: true }).first().click();
      await capture(width, theme, 'send');
      if (width === 1280) {
        await page.locator('[data-row="nasx4k"]').click();
        await capture(width, theme, 'send-file-detail');
        await page.locator('[title="\u7f16\u8f91 \u00b7 F2"]').click();
        await capture(width, theme, 'send-file-editor');
        await page.getByText('\u53d6\u6d88', { exact: true }).first().click();
        await page.locator('[data-row="wifi9q"]').click();
        await capture(width, theme, 'send-text-detail');
        await page.locator('[title="\u7f16\u8f91 \u00b7 F2"]').click();
        await capture(width, theme, 'send-text-editor');
        await openReference(width, theme);
        await page.locator('[data-row="github"]').click();
        await capture(width, theme, 'vault-login-detail');
        await page.locator('[title="\u7f16\u8f91 \u00b7 F2"]').click();
        await page.getByPlaceholder('\u540d\u79f0\uff08\u5fc5\u586b\uff09').waitFor({ state: 'visible' });
        await capture(width, theme, 'vault-login-editor');
        await page.getByText('\u53d6\u6d88', { exact: true }).first().click();
        await page.locator('[title="\u7acb\u5373\u9501\u5b9a"]').click();
        await capture(width, theme, 'locked');
        await page.getByText('\u9000\u51fa\u767b\u5f55\uff0c\u67e5\u770b\u767b\u5f55\u6d41\u7a0b', { exact: true }).click();
        await capture(width, theme, 'login');
        await page.getByText('\u521b\u5efa\u8d26\u6237', { exact: true }).click();
        await capture(width, theme, 'register');
      } else {
        await page.locator('[data-row="nasx4k"]').click();
        await capture(width, theme, 'send-file-detail');
        await page.locator('[title="\u7f16\u8f91 \u00b7 F2"]').click();
        await capture(width, theme, 'send-file-editor');
        await openReference(width, theme);
        await page.locator('[data-row="github"]').click();
        await capture(width, theme, 'vault-login-detail');
        await page.locator('[title="\u7f16\u8f91 \u00b7 F2"]').click();
        await page.getByPlaceholder('\u540d\u79f0\uff08\u5fc5\u586b\uff09').waitFor({ state: 'visible' });
        await capture(width, theme, 'vault-login-editor');
      }
    }
  }
} finally {
  await fs.writeFile(path.join(out, 'manifest.json'), JSON.stringify({ source, capturedAt: new Date().toISOString(), referenceOnly: true, errors, records }, null, 2));
  await context.close();
  await browser.close();
}
console.log(JSON.stringify({ captures: records.length, errors, output: out }));
