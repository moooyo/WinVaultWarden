import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gunzipSync } from 'node:zlib';

// Render the original, resource-complete handoff. Extracted templates are metadata only.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const options = {};
for (let index = 2; index < process.argv.length; index++) {
  const argument = process.argv[index];
  if (argument === '--help') {
    console.log('Usage: node render-v4-reference.mjs --source <handoff.html> --out <directory> [--widths 1280,1024,760] [--themes light,dark] [--scenes vault,send] [--browser msedge]');
    process.exit(0);
  }
  if (!argument.startsWith('--') || !process.argv[index + 1]) throw new Error(`Invalid argument: ${argument}`);
  options[argument.slice(2)] = process.argv[++index];
}

const source = path.resolve(options.source || path.join(process.env.USERPROFILE, 'Downloads', 'WinVaultWarden UI \u91cd\u8bbe\u8ba1.html'));
const out = path.resolve(options.out || path.join(root, 'artifacts/ui-review/v4/reference'));
const widths = (options.widths || '1280,1024,760').split(',').map(Number);
const themes = (options.themes || 'light,dark').split(',');
const requestedScenes = options.scenes ? new Set(options.scenes.split(',')) : null;
if (widths.some(width => ![1280, 1024, 760].includes(width))) throw new Error('Supported widths are 1280, 1024, and 760.');
if (themes.some(theme => !['light', 'dark'].includes(theme))) throw new Error('Supported themes are light and dark.');
const runtime = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE, '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const require = createRequire(import.meta.url);
const { chromium } = require(path.join(runtime, 'playwright'));
const playwrightVersion = require(path.join(runtime, 'playwright/package.json')).version;

const label = {
  current: '\uff08\u5f53\u524d\uff09', vault: '\u5bc6\u7801\u5e93', allItems: '\u5168\u90e8\u6761\u76ee',
  generator: '\u751f\u6210\u5668', report: '\u5b89\u5168\u62a5\u544a', settings: '\u8bbe\u7f6e',
  security: '\u5b89\u5168', appearance: '\u5916\u89c2\u4e0e\u884c\u4e3a', account: '\u8d26\u6237',
  lock: '\u7acb\u5373\u9501\u5b9a', locked: '\u5bc6\u7801\u5e93\u5df2\u9501\u5b9a', edit: '\u7f16\u8f91 \u00b7 F2',
  name: '\u540d\u79f0\uff08\u5fc5\u586b\uff09', cancel: '\u53d6\u6d88', close: '\u5173\u95ed',
  history: '\u67e5\u770b\u5bc6\u7801\u5386\u53f2', allSends: '\u5168\u90e8 Send',
  firstReport: '\u5b89\u5168\u62a5\u544a\uff1a\u7b2c\u4e00\u6b21\u6253\u5f00',
  reportEmpty: '\u8fd8\u6ca1\u6709\u68c0\u67e5\u8fc7\u5bc6\u7801\u662f\u5426\u6cc4\u9732',
  startReport: '\u5f00\u59cb\u68c0\u67e5', progress: '\u68c0\u67e5\u8fdb\u5ea6',
  signOutDemo: '\u9000\u51fa\u767b\u5f55\uff0c\u67e5\u770b\u767b\u5f55\u6d41\u7a0b',
  login: '\u767b\u5f55', continue: '\u7ee7\u7eed', password: '\u4e3b\u5bc6\u7801',
  passwordHeading: '\u8f93\u5165\u4e3b\u5bc6\u7801', code: '\u9a8c\u8bc1\u7801',
  twoFactor: '\u4e24\u6b65\u767b\u5f55 \u00b7 \u8eab\u4efd\u9a8c\u8bc1\u5668\u5e94\u7528',
  onboarding: '\u4e0b\u6b21\u7528 Windows Hello \u89e3\u9501\uff1f', skip: '\u4ee5\u540e\u518d\u8bf4',
  register: '\u521b\u5efa\u8d26\u6237', motionOff: '\u5173', followBrowser: '\u8ddf\u968f\u6d4f\u89c8\u5668',
};

const allScenes = [
  'vault', 'vault-login-detail', 'vault-login-editor', 'vault-password-history', 'vault-ssh-detail',
  'generator', 'report', 'report-first', 'report-progress',
  'settings-account', 'settings-security', 'settings-ssh', 'settings-appearance',
  'send', 'send-text-detail', 'send-text-editor', 'send-file-detail', 'send-file-editor',
  'locked', 'login', 'auth-password', 'auth-two-factor', 'auth-onboarding', 'register',
];
if (requestedScenes && [...requestedScenes].some(scene => !allScenes.includes(scene))) throw new Error('Unknown scene name.');
const wants = scene => !requestedScenes || requestedScenes.has(scene);
const wantsAny = scenes => scenes.some(wants);

function packedScript(html, type) {
  const escaped = type.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = html.match(new RegExp(`<script[^>]*type="${escaped}"[^>]*>([\\s\\S]*?)<\\/script>`));
  if (!match) throw new Error(`Missing packed script: ${type}`);
  return JSON.parse(match[1]);
}

const sourceBytes = await fs.readFile(source);
const sourceHtml = sourceBytes.toString('utf8');
const outerTemplate = packedScript(sourceHtml, '__bundler/template');
const outerManifest = packedScript(sourceHtml, '__bundler/manifest');
const boards = [...outerTemplate.matchAll(/<iframe\b([^>]*)>/g)].map(match => {
  const attributes = Object.fromEntries([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(attribute => [attribute[1], attribute[2]]));
  return { ...attributes, version: Number(attributes.title?.match(/\bv(\d+)\b/i)?.[1] || 0), id: attributes.src?.split('#')[1] };
});
const latest = boards.filter(board => board.title?.includes(label.current)).sort((a, b) => b.version - a.version)[0];
if (!latest || latest.version !== 4 || !outerManifest[latest.id]) throw new Error('The original handoff does not identify a current v4 board.');
const packedPage = outerManifest[latest.id];
const pageBytes = Buffer.from(packedPage.data, 'base64');
const innerHtml = (packedPage.compressed ? gunzipSync(pageBytes) : pageBytes).toString('utf8');
const innerManifest = packedScript(innerHtml, '__bundler/manifest');
const innerTemplate = packedScript(innerHtml, '__bundler/template');
const expectedTitle = innerTemplate.match(/<title>([^<]*)<\/title>/)?.[1];
const sourceAssets = Object.values(innerManifest);
const mimeCounts = sourceAssets.reduce((counts, asset) => ({ ...counts, [asset.mime]: (counts[asset.mime] || 0) + 1 }), {});

await fs.mkdir(out, { recursive: true });
const records = [];
const unavailable = [];
const errors = [];
const requestFailures = [];
const blockedNetwork = [];
const variants = [];
let stage = 'launch';
let page;
let referenceFrame;
let browser;
let context;
let browserVersion;
const startedAt = new Date().toISOString();

async function writeManifest(completed = false) {
  await fs.writeFile(path.join(out, 'manifest.json'), JSON.stringify({
    source, sourceSha256: createHash('sha256').update(sourceBytes).digest('hex'), sourceBytes: sourceBytes.length,
    startedAt, capturedAt: new Date().toISOString(), completed, referenceOnly: true,
    renderer: { playwrightVersion, browser: options.browser || 'msedge', browserVersion, headless: true, externalNetworkAllowed: false },
    latest: { title: latest.title, version: latest.version, id: latest.id, documentTitle: expectedTitle, packedLayers: 2, mimeCounts, externalMappings: packedScript(innerHtml, '__bundler/ext_resources') },
    requested: { widths, themes, scenes: requestedScenes ? [...requestedScenes] : allScenes },
    variants, errors, requestFailures, blockedNetwork, unavailable, records,
  }, null, 2));
}

async function clickVisible(locator) {
  for (let index = 0; index < await locator.count(); index++) {
    const candidate = locator.nth(index);
    if (await candidate.isVisible()) { await candidate.click(); return; }
  }
  throw new Error('No visible matching control.');
}

const text = value => referenceFrame.getByText(value, { exact: true });
const title = value => referenceFrame.locator(`[title=${JSON.stringify(value)}]`);
const canvas = () => referenceFrame.locator('div[style*="transform-origin"]').first();

async function openReference(width, theme) {
  stage = `${width}-${theme}:load-original`;
  await page.goto(pathToFileURL(source).href, { waitUntil: 'load', timeout: 60000 });
  const iframe = page.locator(`iframe[title=${JSON.stringify(latest.title)}]`).first();
  await iframe.waitFor({ state: 'attached' });
  const height = width === 1024 ? 720 : width === 760 ? 640 : 760;
  // Resize only the board viewport, retaining the original nested bundler and assets.
  await iframe.evaluate((element, size) => {
    element.width = String(size.width + 48); element.height = String(size.height + 128);
    element.style.width = `${size.width + 48}px`; element.style.height = `${size.height + 128}px`;
  }, { width, height });
  const handle = await iframe.elementHandle();
  referenceFrame = await handle.contentFrame();
  if (!referenceFrame) throw new Error('The current v4 iframe has no content frame.');
  await referenceFrame.waitForFunction(() => typeof window.__dcSetProps === 'function' && typeof window.__dcRootName === 'function' && window.__dcRootName(), null, { timeout: 30000 });
  const actualTitle = await referenceFrame.title();
  if (actualTitle !== expectedTitle) throw new Error(`Unexpected reference document title: ${actualTitle}`);
  await referenceFrame.evaluate(({ width, motion, follow }) => {
    window.__dcSetProps(window.__dcRootName(), { motion, winSize: width === 1024 ? '1024 \u00d7 720' : width === 760 ? '760 \u00d7 640' : follow });
  }, { width, motion: label.motionOff, follow: label.followBrowser });
  await canvas().waitFor({ state: 'visible' });
  await referenceFrame.waitForFunction(({ width, height }) => {
    const element = document.querySelector('div[style*="transform-origin"]');
    const bounds = element?.getBoundingClientRect();
    return bounds && Math.abs(bounds.width - width) < 0.5 && Math.abs(bounds.height - height) < 0.5;
  }, { width, height });
  const fonts = await referenceFrame.evaluate(async () => {
    await document.fonts.ready;
    const initialLoaded = [...document.fonts].filter(face => face.status === 'loaded').length;
    const checks = await Promise.allSettled([
      document.fonts.load('400 14px "JetBrains Mono"', 'WinVaultWarden 012345'),
      document.fonts.load('400 14px "Open Sans"', 'WinVaultWarden 012345'),
    ]);
    await document.fonts.ready;
    return {
      status: document.fonts.status, initialLoaded, checks: checks.map(result => ({ status: result.status, count: result.status === 'fulfilled' ? result.value.length : 0, error: result.status === 'rejected' ? String(result.reason) : null })),
      faces: [...document.fonts].map(face => ({ family: face.family, weight: face.weight, status: face.status })),
    };
  });
  if (theme === 'dark') await clickVisible(title('\u5207\u6362\u6df1\u8272 / \u6d45\u8272'));
  const resources = await referenceFrame.evaluate(() => performance.getEntriesByType('resource').map(entry => ({ name: entry.name, initiatorType: entry.initiatorType, duration: Math.round(entry.duration) })));
  variants.push({ width, height, theme, title: actualTitle, rootName: await referenceFrame.evaluate(() => window.__dcRootName()), fonts, resources });
}

async function capture(width, theme, scene, evidence = {}) {
  if (!wants(scene)) return;
  stage = `${width}-${theme}:${scene}`;
  const target = canvas();
  const bounds = await target.boundingBox();
  const height = width === 1024 ? 720 : width === 760 ? 640 : 760;
  if (!bounds || Math.abs(bounds.width - width) > 0.5 || Math.abs(bounds.height - height) > 0.5) throw new Error(`Unexpected reference bounds: ${JSON.stringify(bounds)}`);
  await page.mouse.move(0, 0);
  const visibleState = await target.evaluate(element => ({
    title: document.title, textLength: element.innerText.length,
    background: getComputedStyle(element).backgroundColor, color: getComputedStyle(element).color,
    fontFamily: getComputedStyle(element).fontFamily, dialogs: [...element.querySelectorAll('[role="dialog"]')].map(dialog => dialog.getAttribute('aria-label')),
    progress: [...element.querySelectorAll('[role="progressbar"]')].map(progress => ({ name: progress.getAttribute('aria-label'), value: progress.getAttribute('aria-valuenow'), max: progress.getAttribute('aria-valuemax') })),
  }));
  if (visibleState.textLength < 20) throw new Error('The reference canvas has insufficient rendered content.');
  if (theme === 'dark' && visibleState.background !== 'rgb(32, 32, 32)') throw new Error('The requested dark theme is not active.');
  const file = `${width}-${theme}-${scene}.png`;
  const absolutePath = path.join(out, file);
  await target.screenshot({ path: absolutePath, animations: 'disabled' });
  records.push({ width, height, theme, scene, bounds, file, absolutePath, referenceOnly: true, evidence: { ...evidence, ...visibleState } });
}

async function group(width, theme, scenes, run) {
  if (!wantsAny(scenes)) return;
  const before = new Set(records.map(record => record.scene + ':' + record.width + ':' + record.theme));
  try { await run(); }
  catch (error) {
    for (const scene of scenes.filter(wants)) {
      const key = `${scene}:${width}:${theme}`;
      if (!before.has(key) && !records.some(record => `${record.scene}:${record.width}:${record.theme}` === key)) unavailable.push({ width, theme, scene, stage, error: error.message, referenceOnly: true });
    }
    console.log(JSON.stringify({ width, theme, stage, unavailable: error.message }));
    await openReference(width, theme);
  }
}

async function goList(pageName, rowId, backLabel) {
  await clickVisible(text(pageName));
  const row = referenceFrame.locator(`[data-row="${rowId}"]`);
  if (!await row.isVisible()) await clickVisible(text(backLabel));
  await row.waitFor({ state: 'visible' });
}

async function goSettings(section) {
  await clickVisible(title(label.settings));
  if (section !== label.account) {
    const titled = title(section);
    if (await titled.count()) await clickVisible(titled);
    else await clickVisible(text(section));
  }
}

try {
  browser = await chromium.launch({ channel: options.browser || 'msedge', headless: true, args: ['--disable-background-networking', '--disable-component-update', '--disable-sync', '--no-first-run'] });
  browserVersion = browser.version();
  context = await browser.newContext({ viewport: { width: 1500, height: 1100 }, deviceScaleFactor: 1, reducedMotion: 'reduce', serviceWorkers: 'block', acceptDownloads: false });
  // All resources needed by the reference are already in its manifest.
  await context.route('**/*', async route => {
    const url = route.request().url();
    if (/^https?:/i.test(url)) { blockedNetwork.push({ url, stage }); await route.abort('blockedbyclient'); }
    else await route.continue();
  });
  page = await context.newPage();
  page.setDefaultTimeout(10000);
  page.on('pageerror', error => errors.push({ message: error.message, stack: error.stack, stage }));
  page.on('requestfailed', request => requestFailures.push({ url: request.url(), failure: request.failure()?.errorText, stage }));
  page.on('dialog', dialog => dialog.dismiss());

  for (const width of widths) for (const theme of themes) {
    await openReference(width, theme);
    await group(width, theme, ['vault', 'vault-login-detail', 'vault-login-editor', 'vault-password-history', 'vault-ssh-detail'], async () => {
      await capture(width, theme, 'vault');
      if (!wantsAny(['vault-login-detail', 'vault-login-editor', 'vault-password-history', 'vault-ssh-detail'])) return;
      await referenceFrame.locator('[data-row="github"]').click();
      await capture(width, theme, 'vault-login-detail', { selectedRow: 'github' });
      if (wants('vault-login-editor')) {
        await clickVisible(title(label.edit));
        await referenceFrame.getByPlaceholder(label.name).waitFor({ state: 'visible' });
        await capture(width, theme, 'vault-login-editor', { selectedRow: 'github' });
        await clickVisible(text(label.cancel));
      }
      if (wants('vault-password-history')) {
        await clickVisible(title(label.history));
        const dialog = referenceFrame.locator('[data-anim="fdlg"]');
        await dialog.waitFor({ state: 'visible' });
        await capture(width, theme, 'vault-password-history');
        await clickVisible(dialog.getByText(label.close, { exact: true }));
      }
      if (wants('vault-ssh-detail')) {
        await goList(label.vault, 'ssh', label.allItems);
        await referenceFrame.locator('[data-row="ssh"]').click();
        await capture(width, theme, 'vault-ssh-detail', { selectedRow: 'ssh' });
      }
    });
    await group(width, theme, ['generator'], async () => {
      await clickVisible(text(label.generator)); await capture(width, theme, 'generator');
    });
    await group(width, theme, ['report', 'report-first', 'report-progress'], async () => {
      await clickVisible(text(label.report)); await capture(width, theme, 'report');
      if (!wantsAny(['report-first', 'report-progress'])) return;
      await clickVisible(text(label.firstReport));
      await text(label.reportEmpty).waitFor({ state: 'visible' });
      await capture(width, theme, 'report-first');
      if (wants('report-progress')) {
        await clickVisible(text(label.startReport));
        await referenceFrame.getByRole('progressbar', { name: label.progress }).waitFor({ state: 'visible' });
        await capture(width, theme, 'report-progress');
      }
    });
    await group(width, theme, ['settings-account', 'settings-security', 'settings-ssh', 'settings-appearance'], async () => {
      for (const [scene, section] of [['settings-account', label.account], ['settings-security', label.security], ['settings-ssh', 'SSH \u4ee3\u7406'], ['settings-appearance', label.appearance]]) {
        if (!wants(scene)) continue;
        await goSettings(section); await capture(width, theme, scene);
      }
    });
    await group(width, theme, ['send', 'send-text-detail', 'send-text-editor', 'send-file-detail', 'send-file-editor'], async () => {
      await goList('Send', 'wifi9q', label.allSends); await capture(width, theme, 'send');
      for (const [kind, rowId] of [['text', 'wifi9q'], ['file', 'nasx4k']]) {
        if (!wantsAny([`send-${kind}-detail`, `send-${kind}-editor`])) continue;
        await goList('Send', rowId, label.allSends);
        await referenceFrame.locator(`[data-row="${rowId}"]`).click();
        await capture(width, theme, `send-${kind}-detail`, { selectedRow: rowId });
        if (wants(`send-${kind}-editor`)) {
          await clickVisible(title(label.edit));
          await referenceFrame.getByPlaceholder(label.name).waitFor({ state: 'visible' });
          await capture(width, theme, `send-${kind}-editor`, { selectedRow: rowId });
          await clickVisible(text(label.cancel));
        }
      }
    });
    await group(width, theme, ['locked', 'login', 'auth-password', 'auth-two-factor', 'auth-onboarding', 'register'], async () => {
      await clickVisible(title(label.lock));
      await text(label.locked).waitFor({ state: 'visible' });
      await capture(width, theme, 'locked');
      if (!wantsAny(['login', 'auth-password', 'auth-two-factor', 'auth-onboarding', 'register'])) return;
      await clickVisible(text(label.signOutDemo));
      await referenceFrame.getByLabel('\u90ae\u7bb1', { exact: true }).waitFor({ state: 'visible' });
      await capture(width, theme, 'login');
      if (wantsAny(['auth-password', 'auth-two-factor', 'auth-onboarding'])) {
        await clickVisible(text(label.continue));
        await text(label.passwordHeading).waitFor({ state: 'visible' });
        await capture(width, theme, 'auth-password');
        if (wantsAny(['auth-two-factor', 'auth-onboarding'])) {
          await referenceFrame.getByLabel(label.password, { exact: true }).fill('ReferenceOnly!1234');
          await clickVisible(text(label.login));
          await text(label.twoFactor).waitFor({ state: 'visible' });
          await capture(width, theme, 'auth-two-factor');
          if (wants('auth-onboarding')) {
            await referenceFrame.getByLabel(label.code, { exact: true }).fill('123456');
            await clickVisible(text(label.continue));
            await text(label.onboarding).waitFor({ state: 'visible' });
            await capture(width, theme, 'auth-onboarding');
            await clickVisible(text(label.skip));
          }
        }
      }
      if (wants('register')) {
        await openReference(width, theme);
        await clickVisible(text(label.signOutDemo));
        await clickVisible(text(label.register));
        await referenceFrame.getByLabel('\u540d\u79f0\uff08\u53ef\u9009\uff09', { exact: true }).waitFor({ state: 'visible' });
        await capture(width, theme, 'register');
      }
    });
    await writeManifest();
    console.log(JSON.stringify({ width, theme, captures: records.filter(record => record.width === width && record.theme === theme).length, unavailable: unavailable.filter(record => record.width === width && record.theme === theme).length, pageErrors: errors.length }));
  }
  await writeManifest(true);
} catch (error) {
  errors.push({ message: error.message, stack: error.stack, stage, fatal: true });
  await writeManifest();
  process.exitCode = 1;
} finally {
  await context?.close();
  await browser?.close();
}
console.log(JSON.stringify({ captures: records.length, unavailable: unavailable.length, pageErrors: errors.length, output: out, manifest: path.join(out, 'manifest.json'), referenceOnly: true }));
