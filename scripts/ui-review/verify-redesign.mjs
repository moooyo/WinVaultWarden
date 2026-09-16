/**
 * Verify the offline review artifact, not the WinUI application or a backend.
 * Run after build-redesign.mjs and render-redesign.mjs have finished.
 */
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { scenarios, coverage } from './redesign-catalog.mjs';

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'docs/ui-review/fluent-redesign');
const runtime = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE,
  '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const { chromium } = require(path.join(runtime, 'playwright'));
const sharp = require(path.join(runtime, 'sharp'));
const dimensions = (process.argv.find(value => value.startsWith('--viewports='))?.slice(12)
  || '1280x900,960x720').split(',').map(value => {
  const [width, height] = value.split('x').map(Number);
  if (!width || !height) throw new Error(`Invalid viewport: ${value}`);
  return { width, height };
});
const only = process.argv.find(value => value.startsWith('--only='))?.slice(7).split(',');
const selected = only ? scenarios.filter(scene => only.includes(scene.id)) : scenarios;
const sceneIds = new Set(scenarios.map(scene => scene.id));
const report = {
  generatedAt: new Date().toISOString(),
  scope: only ? 'selected-offline-proposal-check' : 'complete-offline-proposal-check',
  proposalOnly: true,
  applicationRuntimeTested: false,
  backendTested: false,
  visualJudgmentAutomated: false,
  methodology: [
    'Every selected frame is opened in both themes at every requested viewport.',
    'Geometry checks detect horizontal clipping and unavailable anchored actions; they do not establish aesthetic quality.',
    'Modal checks use real keyboard traversal and verify that the application background is inert.',
    'Gallery checks exercise navigation, search, theme, comparison, and source coverage.',
    'All proposal PNG files are fully decoded. Network requests are blocked and recorded.',
    'Superseded document navigations reported as ERR_ABORTED are recorded separately from failed resource loads.',
  ],
  viewports: dimensions,
  scenarioCount: scenarios.length,
  inventoryCount: coverage.length,
  inventory: [], images: [], frames: [], interactions: [], gallery: [],
};
const assertion = (list, name, pass, detail = null) => list.push({ name, pass: Boolean(pass), ...(detail === null ? {} : { detail }) });
const frameUrl = (id, theme = 'light') => pathToFileURL(path.join(out, 'frames', `${id}.html`)).href + `?theme=${theme}`;
const textOf = async locator => (await locator.textContent())?.trim() || '';

const rawInventory = JSON.parse(await fs.readFile(path.join(root, 'docs/ui-review/page-inventory.json'), 'utf8'));
const inventory = rawInventory.surfaces || rawInventory.entries || rawInventory;
const inventoryIds = new Set(inventory.map(entry => entry.id));
assertion(report.inventory, 'Source inventory remains complete', inventory.length === 178, { actual: inventory.length, expected: 178 });
assertion(report.inventory, 'Every scenario ID is unique', sceneIds.size === scenarios.length);
assertion(report.inventory, 'Every source surface is mapped once',
  coverage.length === inventoryIds.size && new Set(coverage.map(entry => entry.inventoryId)).size === inventoryIds.size);
assertion(report.inventory, 'Coverage references resolve',
  coverage.every(entry => inventoryIds.has(entry.inventoryId) && sceneIds.has(entry.scenarioId)));
assertion(report.inventory, 'Scenario source references resolve',
  scenarios.every(scene => scene.sourceIds.every(id => inventoryIds.has(id))));
const manifest = JSON.parse(await fs.readFile(path.join(out, 'design-manifest.json'), 'utf8'));
report.artifactGeneratedAt = manifest.generatedAt;
assertion(report.inventory, 'Generated manifest matches the catalog',
  manifest.scenarioCount === scenarios.length && manifest.inventoryCount === inventory.length &&
  manifest.scenarios.every((scene, index) => scene.id === scenarios[index]?.id));

for (const scene of selected) for (const theme of ['light', 'dark']) {
  const file = `${scene.id}-${theme}.png`;
  const result = { file, kind: 'proposal', pass: false };
  try {
    const image = sharp(path.join(out, 'renders', file));
    const metadata = await image.metadata();
    await image.resize(1, 1).raw().toBuffer();
    Object.assign(result, { pass: metadata.width === 1280 && metadata.height === 900,
      width: metadata.width, height: metadata.height });
  } catch (error) { result.error = error.message; }
  report.images.push(result);
}
if (!only) {
  const supportingImages = [
    { file: 'overview.png', kind: 'overview', fullPath: path.join(out, 'overview.png') },
    ...[...new Set(scenarios.map(scene => scene.baselineFilename).filter(Boolean))].map(file => ({
      file, kind: 'baseline', fullPath: path.join(out, '../screenshots', file),
    })),
  ];
  for (const { fullPath, ...item } of supportingImages) {
    const result = { ...item, pass: false };
    try {
      const image = sharp(fullPath);
      const metadata = await image.metadata();
      await image.resize(1, 1).raw().toBuffer();
      Object.assign(result, { pass: Boolean(metadata.width && metadata.height), width: metadata.width, height: metadata.height });
    } catch (error) { result.error = error.message; }
    report.images.push(result);
  }
}

const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: dimensions[0], deviceScaleFactor: 1, reducedMotion: 'reduce' });
context.setDefaultTimeout(5000);
context.setDefaultNavigationTimeout(10000);
const externalRequests = [];
await context.route(/^https?:\/\//, async route => {
  externalRequests.push(route.request().url());
  await route.abort('blockedbyclient');
});
const page = await context.newPage();
const errors = [];
const supersededNavigations = [];
page.on('pageerror', error => errors.push({ type: 'pageerror', message: error.message }));
page.on('console', message => { if (message.type() === 'error') errors.push({ type: 'console', message: message.text() }); });
page.on('requestfailed', request => {
  if (request.resourceType() === 'document' && request.failure()?.errorText === 'net::ERR_ABORTED') {
    supersededNavigations.push(request.url());
    return;
  }
  if (!/^https?:/.test(request.url())) errors.push({ type: 'requestfailed', message: request.url(), failure: request.failure() });
});

async function openFrame(id, theme = 'light') {
  await page.goto(frameUrl(id, theme));
  await page.evaluate(() => document.fonts.ready);
}

async function inspectFrame(scene, viewport, theme) {
  const startErrors = errors.length;
  const startNetwork = externalRequests.length;
  const checks = [];
  await openFrame(scene.id, theme);
  const geometry = await page.evaluate(() => {
    const visible = element => {
      const style = getComputedStyle(element);
      return element.getClientRects().length && style.visibility !== 'hidden' && style.display !== 'none';
    };
    const describe = element => ({
      element: element.tagName.toLowerCase(), className: typeof element.className === 'string' ? element.className : '',
      text: (element.getAttribute('aria-label') || element.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 120),
    });
    const clipped = [];
    const clippedText = [];
    for (const element of document.querySelectorAll('input,select,textarea,button,.form-field,.form-field>span,.field-label,.field-value,.generated-value,.section h3,.dialog-scroll p')) {
      if (!visible(element)) continue;
      const rect = element.getBoundingClientRect();
      let left = 0;
      let right = innerWidth;
      for (let ancestor = element.parentElement; ancestor; ancestor = ancestor.parentElement) {
        const style = getComputedStyle(ancestor);
        if (['hidden', 'clip', 'auto', 'scroll'].includes(style.overflowX)) {
          const box = ancestor.getBoundingClientRect();
          left = Math.max(left, box.left);
          right = Math.min(right, box.right);
        }
      }
      if (rect.left < left - 2 || rect.right > right + 2) clipped.push({ ...describe(element),
        left: Math.round(rect.left), right: Math.round(rect.right), visibleLeft: Math.round(left), visibleRight: Math.round(right) });
      if (element.matches('.field-label,.form-field>span,.dialog-scroll p') && element.childNodes.length) {
        const range = document.createRange();
        range.selectNodeContents(element);
        for (const line of range.getClientRects()) {
          if (line.left < left - 2 || line.right > right + 2) {
            clippedText.push({ ...describe(element), textLeft: Math.round(line.left), textRight: Math.round(line.right),
              visibleLeft: Math.round(left), visibleRight: Math.round(right) });
            break;
          }
        }
      }
    }
    const footer = [...document.querySelectorAll('.app-dialog footer button,.editor-footer button')].filter(visible).map(element => {
      const rect = element.getBoundingClientRect();
      const center = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
      return { ...describe(element), visible: rect.top >= -1 && rect.bottom <= innerHeight + 1 && rect.left >= -1 && rect.right <= innerWidth + 1 &&
        Boolean(center && (element.contains(center) || center.contains(element))) };
    });
    const unnamed = [...document.querySelectorAll('button,input:not([type=hidden]),select,textarea')].filter(visible).filter(element => {
      if (element.getAttribute('aria-label')?.trim() || element.getAttribute('aria-labelledby')?.trim() || element.getAttribute('title')?.trim()) return false;
      if (element.matches('button') && element.textContent.trim()) return false;
      if (element.labels?.length && [...element.labels].some(label => label.textContent.trim())) return false;
      return true;
    }).map(describe);
    return { width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight,
      clipped, clippedText, footer, unnamed,
      actionTargets: [...document.querySelectorAll('[data-go],[data-v-target],[data-review-next],[data-review-followup]')]
        .flatMap(element => [element.dataset.go, element.dataset.vTarget, element.dataset.reviewNext, element.dataset.reviewFollowup].filter(Boolean)),
      modal: Boolean(document.querySelector('.app-dialog')), selectedRows: [...document.querySelectorAll('.vault-list-row.selected')].map(describe),
      selectedIdentity: document.querySelector('.vault-list-row.selected .item-copy strong')?.textContent.trim(),
      detailIdentity: document.querySelector('.v-pane-host [data-v-title]')?.textContent.trim(),
      exposedDetail: Boolean(document.querySelector('.v-pane-host .v-detail')) };
  });
  assertion(checks, 'Theme is applied', await page.getAttribute('html', 'data-theme') === theme);
  assertion(checks, 'Page does not overflow horizontally', geometry.width <= viewport.width + 1, { width: geometry.width, viewportWidth: viewport.width });
  assertion(checks, 'Controls and fields stay inside horizontal clipping boundaries', !geometry.clipped.length, geometry.clipped);
  assertion(checks, 'Visible field and explanation text is not horizontally clipped', !geometry.clippedText.length, geometry.clippedText);
  assertion(checks, 'Anchored action buttons remain visible and unobscured', geometry.footer.every(button => button.visible), geometry.footer);
  if (scene.dialog || scene.variant === 'editor') assertion(checks, 'Editor or dialog exposes an anchored action area', geometry.footer.length > 0);
  assertion(checks, 'Visible form controls have accessible names', !geometry.unnamed.length, geometry.unnamed);
  assertion(checks, 'Scenario navigation targets resolve', geometry.actionTargets.every(id => sceneIds.has(id)), geometry.actionTargets.filter(id => !sceneIds.has(id)));
  if (scene.page === 'vault' && ['no-selection', 'empty', 'no-results', 'loading'].includes(scene.variant)) {
    assertion(checks, 'Unselected state has no highlighted item', geometry.selectedRows.length === 0, geometry.selectedRows);
    assertion(checks, 'Unselected state does not expose a credential detail', !geometry.exposedDetail);
  }
  if (geometry.detailIdentity) {
    assertion(checks, 'The highlighted row matches the displayed item identity', geometry.selectedRows.length === 1 &&
      geometry.selectedIdentity === geometry.detailIdentity, { selected: geometry.selectedIdentity, detail: geometry.detailIdentity });
  }
  if (geometry.modal) {
    const state = await page.evaluate(() => {
      const modal = document.querySelector('.app-dialog');
      const background = document.querySelector('.app-window');
      const focusables = [...modal.querySelectorAll('button,input,select,textarea,summary,a[href],[tabindex]')]
        .filter(element => !element.disabled && element.tabIndex >= 0 && element.getClientRects().length);
      return { inert: Boolean(background?.inert), focusInside: modal.contains(document.activeElement), count: focusables.length };
    });
    assertion(checks, 'Modal declares dialog semantics', await page.locator('.app-dialog').getAttribute('role') === 'dialog' &&
      await page.locator('.app-dialog').getAttribute('aria-modal') === 'true');
    assertion(checks, 'Modal application background is inert', state.inert);
    assertion(checks, 'Modal initial focus is inside the dialog', state.focusInside);
    if (state.count) {
      await page.evaluate(() => {
        const list = [...document.querySelector('.app-dialog').querySelectorAll('button,input,select,textarea,summary,a[href],[tabindex]')]
          .filter(element => !element.disabled && element.tabIndex >= 0 && element.getClientRects().length);
        list.at(-1).focus();
      });
      await page.keyboard.press('Tab');
      const forward = await page.evaluate(() => document.querySelector('.app-dialog').contains(document.activeElement));
      await page.keyboard.press('Shift+Tab');
      const backward = await page.evaluate(() => document.querySelector('.app-dialog').contains(document.activeElement));
      assertion(checks, 'Modal keyboard focus stays inside in both directions', forward && backward);
    } else assertion(checks, 'Modal has a keyboard focus target', false);
  }
  assertion(checks, 'No browser errors', errors.length === startErrors, errors.slice(startErrors));
  assertion(checks, 'No external network requests', externalRequests.length === startNetwork, externalRequests.slice(startNetwork));
  report.frames.push({ id: scene.id, theme, viewport, pass: checks.every(check => check.pass), checks });
}

async function verifyInteractions() {
  await page.setViewportSize({ width: 1280, height: 900 });
  async function flow(id, name, run) {
    if (only && !only.includes(id)) return;
    const checks = [];
    const check = (description, pass, detail = null) => assertion(checks, description, pass, detail);
    const startErrors = errors.length;
    const startNetwork = externalRequests.length;
    try {
      await openFrame(id);
      await run(check);
    } catch (error) { check('Interaction completed', false, error.message); }
    check('No browser errors during interaction', errors.length === startErrors, errors.slice(startErrors));
    check('No external request during interaction', externalRequests.length === startNetwork, externalRequests.slice(startNetwork));
    report.interactions.push({ id, name, pass: checks.every(result => result.pass), checks });
  }
  await flow('vault-no-selection', 'Vault search, count, and exact selection', async check => {
    const initialCount = await page.locator('[data-v-row]:visible').count();
    const count = page.locator('[data-v-list-count]');
    check('Initial list count describes the visible rows', parseInt(await textOf(count), 10) === initialCount);
    await page.locator('[data-v-search]').fill('GitHub');
    check('Search shows the matching item and updates the count', await page.locator('[data-v-row]:visible').count() === 1 &&
      parseInt(await textOf(count), 10) === 1 && (await textOf(page.locator('[data-v-row]:visible .item-copy strong'))) === 'GitHub');
    await page.locator('[data-v-search]').fill('nonexistent-vault-item-90315');
    check('No-match search shows zero results and an empty state', await page.locator('[data-v-row]:visible').count() === 0 &&
      parseInt(await textOf(count), 10) === 0 && await page.locator('[data-v-empty]').isVisible());
    await page.locator('[data-v-action="clear-search"]').first().click();
    check('Clearing search restores rows and count without selecting an item', await page.locator('[data-v-row]:visible').count() === initialCount &&
      parseInt(await textOf(count), 10) === initialCount && await page.locator('.vault-list-row.selected').count() === 0);
    const row = page.locator('[data-v-row="github"] .vault-list-row');
    await row.click();
    await page.waitForFunction(() => document.querySelector('.v-pane-host [data-v-title]')?.textContent.trim() === 'GitHub');
    check('Choosing GitHub opens GitHub rather than a generic login', (await textOf(page.locator('.v-pane-host [data-v-title]'))) === 'GitHub' &&
      (await textOf(page.locator('.vault-list-row.selected .item-copy strong'))) === 'GitHub');
  });
  await flow('vault-no-results', 'Clear the initial no-results state', async check => {
    check('The initial failed search contains no visible result', await page.locator('[data-v-row]:visible').count() === 0);
    await page.locator('[data-v-action="clear-search"]').first().click();
    check('Clear restores the populated unselected vault', await page.locator('[data-v-row]:visible').count() > 0 &&
      await page.locator('.vault-list-row.selected').count() === 0 && await page.locator('[data-v-search]').inputValue() === '');
  });
  await flow('vault-detail-login', 'Sensitive-field reveal and masking', async check => {
    const secret = page.locator('.v-pane-host [data-v-bind="password"]');
    const button = page.locator('.v-pane-host [data-v-field="password"] [data-v-action="reveal"]');
    check('Saved password starts masked', /^•+$/.test(await textOf(secret)));
    await button.click();
    check('Reveal displays the synthetic password and updates accessibility state', (await textOf(secret)) === await secret.getAttribute('data-v-secret') &&
      await button.getAttribute('aria-pressed') === 'true');
    await button.click();
    check('Hide restores masking and accessibility state', /^•+$/.test(await textOf(secret)) && await button.getAttribute('aria-pressed') === 'false');
  });
  await flow('vault-new-login', 'A new item starts blank with saving disabled', async check => {
    const form = page.locator('.v-editor-form');
    check('New-item identity and secret fields are empty', await form.locator('[data-v-key="name"]').inputValue() === '' &&
      await form.locator('[data-v-key="password"]').inputValue() === '');
    check('An unnamed draft cannot be created', await form.locator('[data-v-action="save"]').isDisabled());
    await form.locator('[data-v-key="name"]').fill('Review draft');
    check('A named draft enables the create action', await form.locator('[data-v-action="save"]').isEnabled());
    await form.locator('[data-v-owner]').selectOption('Contoso Review');
    check('Organization creation requires a writable collection', await form.locator('[data-v-action="save"]').isDisabled() &&
      await form.locator('.v-collections').isVisible());
    await form.locator('input[name="collection"]').check();
    check('Selecting a collection enables the valid draft', await form.locator('[data-v-action="save"]').isEnabled());
    await form.locator('[data-v-key="name"]').fill('');
    check('Removing the required name disables creating again', await form.locator('[data-v-action="save"]').isDisabled());
  });
  await flow('vault-editor-login', 'Unsaved edit guard and item identity', async check => {
    check('An unchanged edit has no active save action', await page.locator('[data-v-action="save"]').isDisabled());
    await page.locator('[data-v-key="name"]').fill('Contoso review edit');
    await page.locator('[data-v-action="cancel-edit"]').click();
    check('Leaving an edited draft asks before discarding it', await page.locator('dialog[open].v-local-dialog').isVisible() &&
      /Discard unsaved changes/i.test(await textOf(page.locator('dialog[open] h2'))));
    await page.locator('[data-v-dialog-cancel]').click();
    check('Cancelling discard preserves the draft', await page.locator('[data-v-key="name"]').inputValue() === 'Contoso review edit');
    await page.locator('[data-v-action="save"]').click();
    check('Saving the preview preserves the same updated identity in row and details',
      (await textOf(page.locator('.v-pane-host [data-v-title]'))) === 'Contoso review edit' &&
      (await textOf(page.locator('.vault-list-row.selected .item-copy strong'))) === 'Contoso review edit');
  });
  await flow('vault-new-folder', 'Folder creation requires a name and retains navigation context', async check => {
    const input = page.locator('[data-v-folder-name]');
    const submit = page.locator('[data-v-action="create-folder"]');
    check('A blank folder cannot be created', await submit.isDisabled());
    await input.fill('   ');
    check('Whitespace does not satisfy the folder name', await submit.isDisabled());
    await input.fill('Review folder');
    check('A named folder enables creation', await submit.isEnabled());
    await submit.click();
    check('Created folder has a matching sidebar entry and list heading',
      (await textOf(page.locator('[data-v-folder-name="Review folder"]>span:not(.icon)'))) === 'Review folder' &&
      (await textOf(page.locator('.list-heading h1'))) === 'Review folder');
    check('The new folder shows no selected secret or fabricated contents', (await textOf(page.locator('[data-v-list-count]'))) === '0 items' &&
      await page.locator('[data-v-row]:visible').count() === 0 && await page.locator('.v-pane-host .v-detail').count() === 0);
  });
  await flow('login-account', 'Server and account validation', async check => {
    const input = page.locator('[data-review-form] input[type=email]');
    await input.fill('invalid-email');
    await page.locator('[data-review-next]').click();
    check('Invalid email cannot advance', page.url() === frameUrl('login-account') && await input.evaluate(element => !element.validity.valid));
    await page.locator('[data-server-select]').selectOption('eu');
    check('Hosted server hides and disables the custom address', await page.locator('[data-server-address]').isHidden() &&
      await page.locator('[data-server-address] input').isDisabled());
    check('Hosted server identity is visible', (await textOf(page.locator('[data-auth-server]'))).includes('vault.bitwarden.eu'));
    await page.locator('[data-server-select]').selectOption('self');
    check('Returning to a custom server restores its address field', await page.locator('[data-server-address]').isVisible() &&
      await page.locator('[data-server-address] input').isEnabled());
  });
  await flow('login-password', 'Password visibility and required validation', async check => {
    const input = page.locator('.review-password input').first();
    check('Password starts masked', await input.getAttribute('type') === 'password');
    await page.locator('[data-review-submit]').last().click();
    check('Empty password cannot submit', await input.evaluate(element => !element.validity.valid) && page.url() === frameUrl('login-password'));
    await input.fill('ReviewOnlyExample42!');
    const toggle = page.locator('[data-password-toggle]').first();
    await toggle.click();
    check('Reveal updates masking and accessible state', await input.getAttribute('type') === 'text' && await toggle.getAttribute('aria-pressed') === 'true');
    await toggle.click();
    check('Hide restores password masking', await input.getAttribute('type') === 'password' && await toggle.getAttribute('aria-pressed') === 'false');
    check('Password stage names the review account and server', (await textOf(page.locator('.review-account'))).includes('review@winvaultwarden.example') &&
      (await textOf(page.locator('.review-account'))).includes('127.0.0.1:18083'));
  });
  await flow('login-two-step', 'Verification code validation', async check => {
    const input = page.locator('input[autocomplete="one-time-code"]');
    await input.fill('      ');
    await page.locator('[data-review-submit]').last().click();
    check('Spaces alone are rejected as a verification code', await input.evaluate(element => !element.validity.valid));
    await input.fill('482 916');
    await page.locator('[data-review-submit]').last().click();
    check('Six digits with a separator are accepted by the preview', await input.evaluate(element => element.validity.valid));
  });
  await flow('settings-delete-email', 'Account deletion requires the exact identity', async check => {
    const input = page.locator('[data-exact-value]');
    await input.fill('other@example.com');
    await page.locator('[data-review-next]').click();
    check('A different email cannot advance deletion', page.url() === frameUrl('settings-delete-email') &&
      await input.evaluate(element => !element.validity.valid));
    const expected = await input.getAttribute('data-exact-value');
    await input.fill(expected);
    await page.locator('[data-review-next]').click();
    await page.waitForURL(url => url.pathname.endsWith('/settings-delete-credential.html'));
    check('Matching identity advances to a separate final credential step', page.url().includes('settings-delete-credential.html') &&
      await page.locator('.app-dialog input[type=password]').count() === 1);
  });
  await flow('settings-delete-credential', 'Final deletion is credential gated', async check => {
    const input = page.locator('.app-dialog input[type=password]');
    await page.locator('.app-dialog [data-review-submit]').click();
    check('An empty final credential blocks deletion', await input.evaluate(element => !element.validity.valid) &&
      page.url() === frameUrl('settings-delete-credential'));
  });
  await flow('settings-pin-setup', 'PIN confirmation and length', async check => {
    await page.locator('[data-password-new]').fill('123');
    await page.locator('[data-password-confirm]').fill('123');
    await page.locator('.app-dialog [data-review-submit]').click();
    check('A short PIN is rejected', await page.locator('[data-password-new]').evaluate(element => !element.validity.valid));
    await page.locator('[data-password-new]').fill('654321');
    await page.locator('[data-password-confirm]').fill('654322');
    await page.locator('.app-dialog [data-review-submit]').click();
    check('A mismatched PIN confirmation is rejected', await page.locator('[data-password-confirm]').evaluate(element => !element.validity.valid));
  });
  await flow('send-received-empty', 'Received Send URL validation', async check => {
    const input = page.locator('[data-send-link]');
    const submit = page.locator('[data-send-open]');
    check('Opening is disabled before a link is provided', await submit.isDisabled());
    await input.fill('https://vault.example/#/send/access-id');
    check('A link without its decryption key is rejected', await submit.isDisabled());
    await input.fill('https://vault.example/#/send/access-id/key');
    check('A complete root-path Send link is accepted', await submit.isEnabled());
    await input.fill('not-a-url');
    check('Invalid input disables opening again', await submit.isDisabled());
  });
  await flow('send-overview', 'Send type filtering', async check => {
    await page.locator('[data-tool="send-filter"][data-filter="file"]').click();
    check('File filter contains only file Sends', await page.locator('[data-send-type]:visible').count() === 1 &&
      await page.locator('[data-send-type="file"]').isVisible());
    await page.locator('[data-tool="send-filter"][data-filter="all"]').click();
    check('All filter restores the three synthetic Sends', await page.locator('[data-send-type]:visible').count() === 3);
  });
  await flow('send-edit-text', 'Send content and custom expiration controls', async check => {
    await page.locator('[data-send-name]').fill('');
    check('A Send without a name cannot be created', await page.locator('[data-send-submit]').isDisabled());
    await page.locator('[data-send-deletion]').selectOption({ label: 'Custom' });
    check('Custom deletion exposes its date field', await page.locator('[data-custom-delete]').isVisible());
    await page.locator('[data-send-expiration]').selectOption({ label: 'Custom' });
    check('Custom expiry exposes its date and time field', await page.locator('[data-custom-expiry]').isVisible());
  });
  await flow('export-confirm', 'Plaintext export credential gate', async check => {
    check('Export is initially disabled', await page.locator('[data-export-submit]').isDisabled());
    await page.locator('[data-export-password]').fill('ReviewOnlyExample42!');
    check('Entering a credential enables the verification action', await page.locator('[data-export-submit]').isEnabled());
    await page.locator('[data-export-password]').fill('');
    check('Removing a credential disables export again', await page.locator('[data-export-submit]').isDisabled());
  });
  await flow('device-approve', 'Device request identity confirmation', async check => {
    check('Device approval requires explicit phrase confirmation', await page.locator('[data-device-submit]').isDisabled());
    await page.locator('[data-device-verified]').check();
    check('Matching-phrase acknowledgement enables approval', await page.locator('[data-device-submit]').isEnabled());
    await page.locator('[data-device-verified]').uncheck();
    check('Removing acknowledgement disables approval', await page.locator('[data-device-submit]').isDisabled());
  });
  await flow('emergency-takeover', 'Takeover password confirmation', async check => {
    check('Takeover starts disabled', await page.locator('[data-takeover-submit]').isDisabled());
    await page.locator('[data-takeover-password]').fill('ReviewOnlyExample42!');
    await page.locator('[data-takeover-confirm]').fill('different');
    check('Mismatched takeover passwords remain blocked', await page.locator('[data-takeover-submit]').isDisabled());
    await page.locator('[data-takeover-confirm]').fill('ReviewOnlyExample42!');
    check('Matching nonempty passwords enable the review action', await page.locator('[data-takeover-submit]').isEnabled());
  });
  await flow('emergency-invite', 'Emergency invitation boundaries', async check => {
    await page.locator('[data-emergency-wait]').fill('0');
    check('A zero-day wait is rejected', await page.locator('[data-invite-submit]').isDisabled());
    await page.locator('[data-emergency-wait]').fill('91');
    check('A wait above ninety days is rejected', await page.locator('[data-invite-submit]').isDisabled());
    await page.locator('[data-emergency-wait]').fill('7');
    await page.locator('[data-emergency-email]').fill('not-an-email');
    check('An invalid contact email is rejected', await page.locator('[data-invite-submit]').isDisabled());
    await page.locator('[data-emergency-type]').selectOption({ label: 'Take over account' });
    check('Changing the access type explains the takeover consequence', /resetting|control/i.test(await textOf(page.locator('[data-emergency-access-help]'))));
  });
  await flow('import-preview', 'Import preview is tied to the selected format', async check => {
    check('The parsed fixture enables import', await page.locator('[data-import-submit]').isEnabled());
    await page.locator('[data-import-format]').selectOption({ label: 'Bitwarden CSV' });
    check('Changing format invalidates the old preview', await page.locator('[data-import-submit]').isDisabled() &&
      /preview required/i.test(await textOf(page.locator('.transfer-preview'))));
  });
  await flow('generator-password', 'Generator option consistency', async check => {
    const before = await textOf(page.locator('[data-tool-generated]'));
    await page.locator('[data-tool="regenerate"]').click();
    check('Regenerate changes the illustrative password', await textOf(page.locator('[data-tool-generated]')) !== before);
    await page.locator('[data-gen="length-number"]').fill('32');
    check('The generated value follows the requested length', (await textOf(page.locator('[data-tool-generated]'))).length === 32);
    for (const key of ['upper', 'lower', 'digits', 'symbols']) await page.locator(`[data-gen="${key}"]`).uncheck();
    check('No character groups disables copying and explains the issue', await page.locator('[data-gen-copy]').isDisabled() &&
      /at least one character type/i.test(await textOf(page.locator('[data-gen-status]'))));
  });
  await flow('generator-username', 'Username output follows the chosen mode', async check => {
    await page.locator('[data-gen="username-type"]').selectOption('plus');
    check('Plus addressing exposes the required email field', await page.locator('[data-gen-email]').isVisible() && await page.locator('[data-gen-word]').isHidden());
    check('A missing email disables copying', await page.locator('[data-gen-copy]').isDisabled());
    await page.locator('[data-gen="email"]').fill('alex@example.com');
    check('A valid address generates a matching email alias', /^alex\+.+@example\.com$/.test(await textOf(page.locator('[data-tool-generated]'))) &&
      await page.locator('[data-gen-copy]').isEnabled());
    await page.locator('[data-gen="username-type"]').selectOption('catchall');
    check('Catch-all mode exposes the domain instead of email', await page.locator('[data-gen-domain]').isVisible() && await page.locator('[data-gen-email]').isHidden());
  });
}

async function verifyGallery() {
  const checks = report.gallery;
  async function waitForPreview(id, theme = 'light') {
    const handle = await page.locator('#screen-frame').elementHandle();
    const frame = await handle.contentFrame();
    await frame.waitForURL(url => url.pathname.endsWith(`/${id}.html`) && url.searchParams.get('theme') === theme);
    await frame.waitForLoadState('load');
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  const startErrors = errors.length;
  const startNetwork = externalRequests.length;
  await page.goto(pathToFileURL(path.join(out, 'index.html')).href);
  await page.locator('#screen-frame').waitFor();
  await waitForPreview('vault-no-selection');
  assertion(checks, 'Gallery starts with no vault item selected', (await page.locator('#screen-frame').getAttribute('src'))?.includes('vault-no-selection.html'));
  assertion(checks, 'Gallery includes every scenario', await page.locator('.screen-link').count() === scenarios.length);
  await page.locator('#screen-search').fill('vault-no-selection');
  assertion(checks, 'Gallery search finds an exact scenario', await page.locator('.screen-link:visible').count() === 1);
  await page.locator('#screen-search').fill('no-existing-scenario-90315');
  assertion(checks, 'Gallery search shows an empty state', await page.locator('.screen-link:visible').count() === 0 && await page.locator('#screen-empty').isVisible());
  await page.locator('#screen-search').fill('');
  assertion(checks, 'Clearing gallery search restores all scenarios', await page.locator('.screen-link:visible').count() === scenarios.length);
  for (const scene of scenarios) {
    await page.locator(`.screen-link[data-scenario="${scene.id}"]`).click();
    await waitForPreview(scene.id);
    const title = await textOf(page.locator('#selected-title'));
    const frame = await page.locator('#screen-frame').getAttribute('src');
    const image = await page.locator('#download-current').getAttribute('href');
    assertion(checks, `Navigate to ${scene.id}`, title === scene.title && frame?.includes(`${scene.id}.html`) && image?.includes(`${scene.id}-light.png`));
  }
  await page.locator('.screen-link[data-scenario="vault-no-selection"]').click();
  await waitForPreview('vault-no-selection');
  await page.locator('.theme-control button[data-theme="dark"]').click();
  await waitForPreview('vault-no-selection', 'dark');
  assertion(checks, 'Theme changes gallery, preview, and export together', await page.getAttribute('html', 'data-theme') === 'dark' &&
    (await page.locator('#screen-frame').getAttribute('src'))?.includes('?theme=dark') &&
    (await page.locator('#download-current').getAttribute('href'))?.includes('-dark.png'));
  await page.locator('#compare-button').click();
  assertion(checks, 'Comparison mode shows the selected theme export', await page.locator('#comparison').isVisible() &&
    await page.locator('#frame-wrap').isHidden() && (await page.locator('#proposal-image').getAttribute('src'))?.includes('vault-no-selection-dark.png'));
  await page.locator('#proposal-image').evaluate(image => image.decode());
  assertion(checks, 'Comparison image decodes', await page.locator('#proposal-image').evaluate(image => image.naturalWidth === 1280));
  await page.locator('#optimized-button').click();
  assertion(checks, 'Returning from comparison restores the preview', await page.locator('#frame-wrap').isVisible() && await page.locator('#comparison').isHidden());
  await page.locator('#coverage-tab').click();
  assertion(checks, 'Coverage lists all source surfaces', await page.locator('#coverage-rows tr:visible').count() === inventory.length);
  await page.locator('#coverage-scope').selectOption('direct');
  assertion(checks, 'Coverage scope filter uses the source mapping', await page.locator('#coverage-rows tr:visible').count() === coverage.filter(entry => entry.scope === 'direct').length);
  await page.locator('#coverage-search').fill('no-existing-coverage-90315');
  assertion(checks, 'Coverage search shows an empty state', await page.locator('#coverage-rows tr:visible').count() === 0 && await page.locator('#coverage-empty').isVisible());
  await page.locator('#clear-coverage').click();
  assertion(checks, 'Coverage clear restores the complete inventory', await page.locator('#coverage-rows tr:visible').count() === inventory.length);
  const localLinks = await page.evaluate(() => [...new Set([...document.querySelectorAll('a[href]')]
    .map(anchor => anchor.href).filter(href => href.startsWith('file:')))]);
  const missingLinks = [];
  for (const href of localLinks) {
    try { await fs.access(fileURLToPath(href)); }
    catch (error) { missingLinks.push({ href, error: error.message }); }
  }
  assertion(checks, 'Local gallery links resolve to existing files', !missingLinks.length, { checked: localLinks.length, missing: missingLinks });
  assertion(checks, 'No gallery browser errors', errors.length === startErrors, errors.slice(startErrors));
  assertion(checks, 'No gallery external network requests', externalRequests.length === startNetwork, externalRequests.slice(startNetwork));
}

try {
  for (const viewport of dimensions) {
    await page.setViewportSize(viewport);
    for (const theme of ['light', 'dark']) {
      for (const scene of selected) {
        try { await inspectFrame(scene, viewport, theme); }
        catch (error) { report.frames.push({ id: scene.id, theme, viewport, pass: false, checks: [{ name: 'Frame inspection completed', pass: false, detail: error.message }] }); }
      }
      console.log(`Inspected ${selected.length} ${theme} frames at ${viewport.width}x${viewport.height}.`);
    }
  }
  await verifyInteractions();
  if (!only) {
    try { await verifyGallery(); }
    catch (error) { assertion(report.gallery, 'Gallery inspection completed', false, error.message); }
  }
} finally { await context.close(); await browser.close(); }

report.summary = {
  frameChecks: report.frames.length,
  passedFrames: report.frames.filter(frame => frame.pass).length,
  failedFrames: report.frames.filter(frame => !frame.pass).length,
  decodedImages: report.images.filter(image => image.pass).length,
  decodedProposalImages: report.images.filter(image => image.pass && image.kind === 'proposal').length,
  failedImages: report.images.filter(image => !image.pass).length,
  inventoryFailures: report.inventory.filter(check => !check.pass).length,
  interactionFailures: report.interactions.filter(check => !check.pass).length,
  galleryFailures: report.gallery.filter(check => !check.pass).length,
  externalNetworkRequests: externalRequests.length,
};
report.supersededNavigations = supersededNavigations;
report.pass = Object.entries(report.summary).filter(([key]) => /fail|external/i.test(key)).every(([, value]) => value === 0);
await fs.writeFile(path.join(out, 'review-validation.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ pass: report.pass, ...report.summary, failures: report.frames.filter(frame => !frame.pass).map(frame => ({
  id: frame.id, theme: frame.theme, viewport: frame.viewport, checks: frame.checks.filter(check => !check.pass),
})), galleryFailures: report.gallery.filter(check => !check.pass), interactionFailures: report.interactions.filter(check => !check.pass) }, null, 2));
if (!report.pass) process.exitCode = 1;
