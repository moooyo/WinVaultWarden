import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

// This dependency-free generator reads captures without changing their bytes.
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(repository, 'docs/ui-review/native-implementation');
const escape = value => String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
const themeOf = filename => /(?:^|[-_])dark(?:[-_.]|$)/i.test(filename) ? 'dark' : /(?:^|[-_])light(?:[-_.]|$)/i.test(filename) ? 'light' : 'unspecified';
const titleOf = filename => {
  const value = filename.replace(/\.(?:png|jpe?g)$/i, '').replace(/(?:[-_])(?:dark|light)$/i, '').replace(/[-_]+/g, ' ');
  return value.charAt(0).toUpperCase() + value.slice(1).replace(/\baot\b/gi, 'AOT').replace(/\bui\b/gi, 'UI');
};
const themeNames = { light: 'Light theme', dark: 'Dark theme', unspecified: 'Theme unspecified' };

await fs.mkdir(output, { recursive: true });
const files = (await fs.readdir(output, { withFileTypes: true }))
  .filter(entry => entry.isFile() && /\.(?:png|jpe?g)$/i.test(entry.name))
  .map(entry => entry.name)
  .sort((left, right) => left.localeCompare(right, 'en', { numeric: true }));
const captures = files.map(filename => ({ filename, title: titleOf(filename), theme: themeOf(filename), href: encodeURIComponent(filename) }));
const hasUnspecified = captures.some(capture => capture.theme === 'unspecified');
const builtAt = new Date().toISOString();

const html = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="light dark">
  <meta name="description" content="Actual WinVaultWarden native implementation captures from a Debug and Native AOT review session using synthetic data.">
  <title>Native implementation review</title>
  <style>
    :root { color-scheme: light; --canvas:#f3f6fa; --paper:#fff; --ink:#172c45; --muted:#60718a; --line:#d5dfeb; --accent:#245ea8; --wash:#e8f0fa; font:15px/1.6 'Segoe UI',Arial,sans-serif; color:var(--ink); background:var(--canvas); }
    * { box-sizing:border-box; }
    body { margin:0; }
    a { color:var(--accent); text-underline-offset:3px; }
    a:focus-visible,button:focus-visible { outline:3px solid #4387d6; outline-offset:4px; }
    button { font:inherit; cursor:pointer; }
    main { max-width:1600px; margin:auto; padding:46px 36px 34px; }
    header { max-width:970px; }
    .eyebrow { color:var(--accent); font-size:11px; letter-spacing:1.4px; text-transform:uppercase; font-weight:700; margin:0 0 12px; }
    h1 { font:700 clamp(30px,4vw,46px)/1.18 Tahoma,'Segoe UI',sans-serif; letter-spacing:-1.4px; margin:0; }
    .intro { color:var(--muted); font-size:17px; margin:19px 0 16px; }
    .context { border-left:3px solid var(--accent); background:var(--wash); border-radius:0 6px 6px 0; padding:13px 17px; font-size:13px; margin:0; }
    .prototype { margin:17px 0 0; font-size:13px; color:var(--muted); }
    .toolbar { display:flex; align-items:center; justify-content:space-between; gap:16px; flex-wrap:wrap; border-top:1px solid var(--line); border-bottom:1px solid var(--line); padding:17px 0; margin:31px 0 25px; }
    .filters { display:flex; align-items:center; flex-wrap:wrap; gap:8px; }
    .filter-label { font-size:12px; color:var(--muted); margin-right:5px; }
    .filter { color:var(--ink); background:var(--paper); border:1px solid var(--line); padding:6px 14px; border-radius:6px; font-size:13px; }
    .filter[aria-pressed="true"] { background:var(--accent); border-color:var(--accent); color:#fff; }
    .result { color:var(--muted); font-size:12px; margin:0; }
    .grid { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:23px; }
    .capture { min-width:0; margin:0; border:1px solid var(--line); border-radius:9px; overflow:hidden; background:var(--paper); }
    .image-link { display:block; position:relative; padding:12px; background:#e6ecf4; aspect-ratio:1.48; border-bottom:1px solid var(--line); }
    .capture[data-theme="dark"] .image-link { background:#283547; }
    .image-link img { display:block; height:100%; width:100%; object-fit:contain; }
    .image-hint { position:absolute; right:10px; bottom:10px; color:var(--ink); background:var(--paper); border:1px solid var(--line); padding:3px 8px; border-radius:4px; font-size:11px; opacity:0; }
    .image-link:hover .image-hint,.image-link:focus-visible .image-hint { opacity:1; }
    figcaption { padding:17px 19px 16px; }
    .card-meta { display:flex; gap:12px; align-items:center; justify-content:space-between; font-size:10px; color:var(--muted); text-transform:uppercase; letter-spacing:.55px; }
    .captured { color:var(--accent); font-weight:700; }
    .captured::before { content:''; display:inline-block; width:6px; height:6px; background:currentColor; border-radius:50%; margin-right:6px; vertical-align:1px; }
    h2 { font-size:17px; font-weight:650; margin:9px 0 5px; line-height:1.45; }
    .filename { font:11px/1.6 Consolas,'Courier New',monospace; color:var(--muted); overflow-wrap:anywhere; margin:0; }
    .card-links { display:flex; gap:17px; border-top:1px solid var(--line); margin-top:14px; padding-top:11px; font-size:12px; }
    .card-links a { text-decoration:none; }
    .empty { color:var(--muted); padding:40px 16px; text-align:center; border:1px dashed var(--line); border-radius:8px; }
    [hidden] { display:none!important; }
    footer { color:var(--muted); font-size:12px; margin-top:35px; padding-top:19px; border-top:1px solid var(--line); }
    footer p { margin:5px 0; }
    @media (max-width:1150px) { .grid { grid-template-columns:repeat(2,minmax(0,1fr)); } }
    @media (max-width:650px) { main { padding:28px 16px; } .grid { grid-template-columns:1fr; gap:18px; } .intro { font-size:15px; } h1 { letter-spacing:-.9px; } .toolbar { margin-top:25px; } }
    @media (prefers-color-scheme:dark) { :root { color-scheme:dark; --canvas:#101a27; --paper:#182638; --ink:#e6edf6; --muted:#a3b2c7; --line:#34445a; --accent:#91bdff; --wash:#203751; } .filter[aria-pressed="true"] { color:#14243a; } .image-link { background:#283547; } }
    @media print { @page { size:A4 landscape; margin:12mm; } :root { color-scheme:light; background:white; color:#172c45; } main { padding:0; } .toolbar,.card-links,.image-hint { display:none; } .grid { grid-template-columns:repeat(2,minmax(0,1fr)); gap:16px; margin-top:22px; } .capture { break-inside:avoid; } h1 { font-size:30px; } .intro { font-size:13px; } .context,.prototype,footer { font-size:11px; } }
  </style>
</head>
<body>
<main>
  <header>
    <p class="eyebrow">WinVaultWarden · Captured application UI</p>
    <h1>Native implementation review</h1>
    <p class="intro">These screenshots show the actual native WinUI client with synthetic test data. This implementation review session includes Debug and Native AOT client builds.</p>
    <p class="context">Each image records a visible application state from this session. The gallery preserves the original files, including any visible defects. It does not claim complete scenario coverage or that every pictured interaction was validated.</p>
    <p class="prototype">The earlier <a href="../fluent-redesign/index.html">Fluent redesign</a> is a design prototype. Use this gallery to review the implemented native application.</p>
  </header>
  <div class="toolbar">
    <div class="filters" role="group" aria-label="Filter captures by theme">
      <span class="filter-label">Captured theme</span>
      <button class="filter" type="button" data-filter="all" aria-pressed="true">All</button>
      <button class="filter" type="button" data-filter="light" aria-pressed="false">Light</button>
      <button class="filter" type="button" data-filter="dark" aria-pressed="false">Dark</button>
      ${hasUnspecified ? '<button class="filter" type="button" data-filter="unspecified" aria-pressed="false">Unspecified</button>' : ''}
    </div>
    <p class="result" id="result" aria-live="polite">${captures.length} capture${captures.length === 1 ? '' : 's'} available</p>
  </div>
  <section class="grid" aria-label="Native implementation screenshots">
    ${captures.map(capture => `<figure class="capture" data-theme="${capture.theme}">
      <a class="image-link" href="${capture.href}" target="_blank" rel="noopener" aria-label="Open ${escape(capture.title)} at original size"><img src="${capture.href}" alt="Native application capture: ${escape(capture.title)}, ${themeNames[capture.theme].toLowerCase()}" loading="lazy" decoding="async"><span class="image-hint">Open original size ↗</span></a>
      <figcaption><div class="card-meta"><span class="captured">Native capture</span><span>${themeNames[capture.theme]}</span></div><h2>${escape(capture.title)}</h2><p class="filename">${escape(capture.filename)}</p><div class="card-links"><a href="${capture.href}" target="_blank" rel="noopener">Open original size ↗</a><a href="${capture.href}" download>Download image</a></div></figcaption>
    </figure>`).join('\n')}
  </section>
  <p class="empty" id="empty" ${captures.length ? 'hidden' : ''}>No captures match this theme.</p>
  <footer>
    <p>Theme labels are derived from filenames. Images are displayed without cropping or retouching; open the original to inspect its native resolution.</p>
    <p>Generated ${builtAt} from this directory by <a href="../../../scripts/ui-review/build-native-review.mjs">build-native-review.mjs</a>. Rebuild to include newly added JPG or PNG files.</p>
  </footer>
</main>
<script>
  'use strict';
  const cards = [...document.querySelectorAll('.capture')];
  const buttons = [...document.querySelectorAll('[data-filter]')];
  for (const button of buttons) {
    button.addEventListener('click', () => {
      const selected = button.dataset.filter;
      let visible = 0;
      for (const card of cards) {
        card.hidden = selected !== 'all' && card.dataset.theme !== selected;
        if (!card.hidden) visible++;
      }
      for (const item of buttons) item.setAttribute('aria-pressed', String(item === button));
      document.getElementById('result').textContent = 'Showing ' + visible + ' of ' + cards.length + ' captures';
      document.getElementById('empty').hidden = visible !== 0;
    });
  }
</script>
</body>
</html>`;

await fs.writeFile(path.join(output, 'index.html'), html);
console.log(JSON.stringify({ output: path.join(output, 'index.html'), captures: captures.length, themes: captures.reduce((counts, capture) => ({ ...counts, [capture.theme]: (counts[capture.theme] || 0) + 1 }), {}) }, null, 2));

// Verification is opt-in and uses an isolated headless browser, never the user's desktop.
if (process.argv.includes('--validate')) {
  const require = createRequire(import.meta.url);
  let playwright;
  try { playwright = require('playwright'); }
  catch {
    const runtimeModules = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE || '', '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
    playwright = require(path.join(runtimeModules, 'playwright'));
  }
  const browser = await playwright.chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const javascriptErrors = [];
    const networkRequests = [];
    page.on('pageerror', error => javascriptErrors.push(error.message));
    page.on('request', request => { if (/^https?:/.test(request.url())) networkRequests.push(request.url()); });
    await page.goto(pathToFileURL(path.join(output, 'index.html')).href);
    const imageResults = await page.locator('.capture img').evaluateAll(async images => Promise.all(images.map(async image => {
      image.loading = 'eager';
      try { await image.decode(); }
      catch (error) { return { file: decodeURIComponent(new URL(image.src).pathname.split('/').at(-1)), decoded: false, error: error.message }; }
      return { file: decodeURIComponent(new URL(image.src).pathname.split('/').at(-1)), decoded: image.naturalWidth > 0 && image.naturalHeight > 0 };
    })));
    const links = await page.locator('a[href], img[src]').evaluateAll(elements => [...new Set(elements.map(element => element.href || element.src))]);
    const missingResources = [];
    for (const href of links) {
      const url = new URL(href);
      if (url.protocol !== 'file:') continue;
      url.hash = '';
      try { await fs.access(fileURLToPath(url)); }
      catch { missingResources.push(href); }
    }

    const filterResults = [];
    for (const theme of await page.locator('[data-filter]').evaluateAll(buttons => buttons.map(button => button.dataset.filter))) {
      await page.locator(`[data-filter="${theme}"]`).click();
      const expected = captures.filter(capture => theme === 'all' || capture.theme === theme).length;
      const visible = await page.locator('.capture:visible').count();
      const pressed = await page.locator('[data-filter][aria-pressed="true"]').evaluateAll(buttons => buttons.map(button => button.dataset.filter));
      const emptyVisible = await page.locator('#empty').isVisible();
      filterResults.push({ theme, expected, visible, passed: expected === visible && pressed.length === 1 && pressed[0] === theme && emptyVisible === (expected === 0) });
    }
    await page.locator('[data-filter="all"]').click();

    const layouts = [];
    for (const viewport of [{ name: 'wide', width: 1440, height: 1000 }, { name: 'narrow', width: 390, height: 844 }]) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height });
      const geometry = await page.evaluate(() => {
        const insideViewport = element => {
          const box = element.getBoundingClientRect();
          return box.width > 0 && box.left >= -1 && box.right <= innerWidth + 1;
        };
        return {
          viewportWidth: innerWidth,
          scrollWidth: document.documentElement.scrollWidth,
          columns: getComputedStyle(document.querySelector('.grid')).gridTemplateColumns.split(/\s+/).length,
          cardsFit: [...document.querySelectorAll('.capture')].every(insideViewport),
          filtersFit: [...document.querySelectorAll('[data-filter]')].every(insideViewport),
        };
      });
      layouts.push({ viewport: viewport.name, ...geometry, passed: geometry.scrollWidth <= geometry.viewportWidth && geometry.cardsFit && geometry.filtersFit });
    }
    const failedImages = imageResults.filter(image => !image.decoded);
    const passed = imageResults.length === captures.length && failedImages.length === 0 && missingResources.length === 0 && javascriptErrors.length === 0 && networkRequests.length === 0 && filterResults.every(result => result.passed) && layouts.every(result => result.passed);
    const report = {
      generatedAt: new Date().toISOString(),
      scope: 'Offline gallery checks only; this does not validate native-client behavior or scenario coverage. Source images were not modified.',
      status: passed ? 'passed' : 'failed',
      browser: 'Headless Microsoft Edge',
      captures: captures.length,
      decodedImages: imageResults.length - failedImages.length,
      checkedResourceLinks: links.length,
      failedImages,
      missingResources,
      javascriptErrors,
      networkRequests,
      filters: filterResults,
      layouts,
    };
    await fs.writeFile(path.join(output, 'gallery-validation.json'), JSON.stringify(report, null, 2) + '\n');
    console.log(JSON.stringify(report, null, 2));
    if (!passed) process.exitCode = 1;
  } finally {
    await browser.close();
  }
}
