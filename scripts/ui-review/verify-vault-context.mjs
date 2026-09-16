/**
 * Verify memory-only gallery navigation with the generated review artifact.
 * This does not test WinUI, real authentication, credentials, or a backend.
 */
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'docs/ui-review/fluent-redesign');
const runtime = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE,
  '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const { chromium } = require(path.join(runtime, 'playwright'));
const report = {
  generatedAt: new Date().toISOString(), scope: 'memory-only-review-gallery-navigation',
  proposalOnly: true, applicationRuntimeTested: false, backendTested: false,
  methodology: [
    'Exercise actual generated gallery controls and framed proposals in headless Microsoft Edge.',
    'Check list scope, search, filters, sorting, scroll, focus, selection, theme reload, and manual review navigation.',
    'Use the shell navigation function only to simulate returning after authentication; no authentication result is asserted.',
    'Instrument web storage writes and outgoing context messages; inspect cookies without saving browser state.',
  ],
  flows: [], browserErrors: [], externalRequests: [],
};
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1600, height: 1100 } });
await context.addInitScript(() => {
  window.__vaultContextAudit = { storageWrites: [], contextMessages: [], navigationEvents: [] };
  const setItem = Storage.prototype.setItem;
  Storage.prototype.setItem = function (key, value) {
    window.__vaultContextAudit.storageWrites.push({ key: String(key), value: String(value) });
    return setItem.call(this, key, value);
  };
  window.addEventListener('message', event => {
    if (event.data?.type?.startsWith('wvw-review-') && event.data.context) {
      window.__vaultContextAudit.contextMessages.push(event.data.context);
      window.__vaultContextAudit.navigationEvents.push({ type: event.data.type, id: event.data.id || event.data.sceneId || '', intent: event.data.context.intent, resultsOnly: event.data.context.resultsOnly });
    }
  });
});
await context.route(/^https?:/i, route => {
  report.externalRequests.push(route.request().url());
  return route.abort();
});
const page = await context.newPage();
page.setDefaultTimeout(7000);
page.on('pageerror', error => report.browserErrors.push(error.message));
const galleryUrl = pathToFileURL(path.join(out, 'index.html')).href;
let frame;
let theme = 'light';
const row = id => frame.locator(`[data-v-row="${id}"] .vault-list-row`);
const check = (checks, name, pass, detail) => checks.push({ name, pass: Boolean(pass), ...(detail === undefined ? {} : { detail }) });
async function preview(id, expectedTheme = theme) {
  frame = await (await page.locator('#screen-frame').elementHandle()).contentFrame();
  await frame.waitForURL(url => url.pathname.endsWith('/' + id + '.html') && url.searchParams.get('theme') === expectedTheme);
  await frame.waitForLoadState('load');
  if (id.startsWith('vault-')) await frame.waitForFunction(() => typeof window.captureReviewVaultContext === 'function');
  return frame;
}
async function fixture(id) {
  await page.locator(`.screen-link[data-scenario="${id}"]`).click();
  await preview(id);
}
async function view() {
  return frame.evaluate(() => ({
    context: window.captureReviewVaultContext?.() || null,
    selected: [...document.querySelectorAll('.v-list .vault-list-row.selected')].map(element => element.closest('[data-v-row]').dataset.vRow),
    details: [...document.querySelectorAll('.v-pane-host .v-detail')].map(element => element.dataset.vRecord),
    focusedRow: document.activeElement?.closest('[data-v-row]')?.dataset.vRow || null,
    title: document.querySelector('.v-list .list-heading h1')?.textContent,
    rows: [...document.querySelectorAll('.v-list [data-v-row]')].map(element => element.dataset.vRow),
  }));
}
async function restored(query, scope = 'all') {
  await frame.waitForFunction(({ query, scope }) => {
    const snapshot = window.captureReviewVaultContext?.();
    return snapshot?.query === query && snapshot.scope === scope;
  }, { query, scope });
}
async function backTo(id, query, scope = 'all') {
  await frame.locator('[data-review-back-results]').click();
  await preview(id);
  await restored(query, scope);
  await frame.waitForFunction(() => !document.querySelector('.v-pane-host .v-detail'));
}
async function run(name, execute) {
  const checks = [];
  const startErrors = report.browserErrors.length;
  try { await execute(checks); }
  catch (error) { checks.push({ name: 'Flow completed', pass: false, detail: error.message.slice(0, 1000), state: await view().catch(() => null), events: await page.evaluate(() => window.__vaultContextAudit.navigationEvents.slice(-10)) }); }
  check(checks, 'No browser script errors in this flow', report.browserErrors.length === startErrors);
  report.flows.push({ name, pass: checks.every(item => item.pass), checks });
  console.log(`${checks.every(item => item.pass) ? 'PASS' : 'FAIL'} ${name}`);
}

try {
  await page.goto(galleryUrl);
  await preview('vault-no-selection');
  await run('Search, filters, detail, and back preserve keyboard context', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('GitHub');
    await frame.locator('[data-v-filter]').selectOption('login');
    await frame.locator('.v-list .v-menu > summary').click();
    await frame.locator('[data-v-sort]').selectOption('name-desc');
    await frame.locator('[data-v-favorite-filter]').check();
    await frame.locator('.v-list .v-menu > summary').click();
    await row('github').click();
    await preview('vault-detail-github');
    await restored('GitHub');
    let state = await view();
    check(checks, 'The clicked record remains the selected detail', state.details.join() === 'github' && state.selected.join() === 'github');
    check(checks, 'Search and every list filter survive opening details', state.context.itemType === 'login' && state.context.favoritesOnly && state.context.sort === 'name-desc' && state.context.visibleRowIds.join() === 'github');
    await backTo('vault-no-selection', 'GitHub');
    await frame.waitForFunction(() => document.activeElement?.closest('[data-v-row]')?.dataset.vRow === 'github');
    state = await view();
    check(checks, 'Back restores results without automatically selecting an item', !state.details.length && !state.selected.length);
    check(checks, 'Back returns keyboard focus to the opened row', state.focusedRow === 'github');
  });
  await run('Scroll position survives opening and closing details', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-rows]').evaluate(element => { element.scrollTop = element.scrollHeight; });
    await row('passport').focus();
    const before = (await view()).context.listScrollTop;
    check(checks, 'The fixture actually has a scrolled list', before > 0, { scrollTop: before });
    await row('passport').press('Enter');
    await preview('vault-detail-passport');
    await frame.waitForFunction(value => Math.abs(document.querySelector('[data-v-rows]').scrollTop - value) < 2, before);
    check(checks, 'Opening details retains list scroll', Math.abs((await view()).context.listScrollTop - before) < 2);
    await backTo('vault-no-selection', '');
    await frame.waitForFunction(value => Math.abs(document.querySelector('[data-v-rows]').scrollTop - value) < 2, before);
    check(checks, 'Back retains list scroll and row focus', Math.abs((await view()).context.listScrollTop - before) < 2 && (await view()).focusedRow === 'passport');
  });
  await run('Work folder retains its allowed rows and return destination', async checks => {
    await fixture('vault-folder-work');
    await frame.locator('[data-v-search]').fill('Git');
    const allowed = (await view()).rows;
    await row('github').click();
    await preview('vault-detail-github');
    await restored('Git', 'folder-work');
    let state = await view();
    check(checks, 'Details retain the Work scope and exact allowed rows', state.title === 'Work' && state.rows.join() === allowed.join() && state.details.join() === 'github');
    check(checks, 'The navigation highlights the originating Work folder', await frame.locator('.nav-vault-context [data-go="vault-folder-work"]').evaluate(element => element.classList.contains('is-current')));
    await frame.locator('[data-v-search]').fill('');
    state = await view();
    check(checks, 'Clearing search cannot reveal records outside the folder', state.context.visibleRowIds.length === allowed.length && state.context.visibleRowIds.every(id => allowed.includes(id)));
    await backTo('vault-folder-work', '', 'folder-work');
    check(checks, 'Back returns to an unselected Work folder', (await view()).title === 'Work' && !(await view()).selected.length);
  });
  await run('Favorites and organization details preserve scope navigation', async checks => {
    await fixture('vault-favorites');
    const favorites = (await view()).rows;
    await row('github').click();
    await preview('vault-detail-github');
    await restored('', 'favorites');
    check(checks, 'Favorite details retain the favorite rows and active navigation', (await view()).rows.join() === favorites.join() && await frame.locator('.nav-vault-context [data-go="vault-favorites"]').evaluate(element => element.classList.contains('active')));
    await backTo('vault-favorites', '', 'favorites');
    check(checks, 'Favorite results return without a selected item', !(await view()).selected.length);
    await fixture('vault-organization');
    await row('shared').click();
    await preview('vault-detail-shared');
    await restored('', 'organization');
    check(checks, 'Organization details keep the shared row and organization navigation', (await view()).rows.join() === 'shared' && await frame.locator('.nav-vault-context [data-go="vault-organization"]').evaluate(element => element.classList.contains('is-current')));
    check(checks, 'The visible scope label names the organization', (await frame.locator('.vault-switch strong').textContent()).trim() === 'Contoso Review');
    await backTo('vault-organization', '', 'organization');
    check(checks, 'Organization results return without a selected item', !(await view()).selected.length);
  });
  await run('Leaving Send and returning to My vault restores the all-items scope', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('Google');
    await frame.locator('[data-v-filter]').selectOption('login');
    await frame.locator('.app-nav [data-page="send"]').click();
    await preview('send-overview');
    await frame.locator('.app-nav [data-page="vault"]').click();
    await preview('vault-no-selection');
    await restored('Google');
    const state = await view();
    check(checks, 'The all-items query and type survive a different feature page', state.context.itemType === 'login' && state.context.visibleRowIds.join() === 'google');
    check(checks, 'Returning to My vault keeps the results pane unselected', !state.details.length && !state.selected.length);
  });
  await run('Work folder workspace resumes after visiting Send', async checks => {
    await fixture('vault-folder-work');
    await frame.locator('[data-v-search]').fill('Git');
    const before = (await view()).context;
    await frame.locator('.app-nav [data-page="send"]').click();
    await preview('send-overview');
    await frame.locator('.app-nav [data-page="vault"]').click();
    await preview('vault-no-selection');
    await restored('Git', 'folder-work');
    const state = await view();
    check(checks, 'My vault resumes the previous Work workspace and query', state.title === 'Work' && state.context.visibleRowIds.join() === 'github');
    check(checks, 'The originating scope route and allowed rows remain intact', state.context.originScene === 'vault-folder-work' && state.rows.join() === before.scopeRowIds.join());
    check(checks, 'The list retains its previous scroll position without selecting an item', Math.abs(state.context.listScrollTop - before.listScrollTop) < 2 && !state.details.length && !state.selected.length);
    check(checks, 'The Work folder remains highlighted after resuming the workspace', await frame.locator('.nav-vault-context [data-go="vault-folder-work"]').evaluate(element => element.classList.contains('is-current')));
  });
  await run('Different scopes keep independent filters', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('Google');
    await frame.locator('.app-nav [data-go="vault-folder-work"]').click();
    await preview('vault-folder-work');
    check(checks, 'Entering a new folder starts without the all-items query', (await view()).context.query === '');
    await frame.locator('[data-v-search]').fill('Git');
    await frame.locator('.app-nav [data-page="vault"]').click();
    await preview('vault-no-selection');
    await restored('Google');
    check(checks, 'Returning to all items restores its own query', (await view()).context.visibleRowIds.join() === 'google');
    await frame.locator('.app-nav [data-go="vault-folder-work"]').click();
    await preview('vault-folder-work');
    await restored('Git', 'folder-work');
    check(checks, 'Returning to Work restores its independent query', (await view()).context.visibleRowIds.join() === 'github');
  });
  await run('A theme change captures the latest typed query and no-selection state', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('GitHub');
    await page.locator('.theme-control button[data-theme="dark"]').click();
    theme = 'dark';
    await preview('vault-no-selection');
    await restored('GitHub');
    check(checks, 'Dark theme preserves the latest query and unselected results', (await view()).context.visibleRowIds.join() === 'github' && !(await view()).details.length);
    await page.locator('.theme-control button[data-theme="light"]').click();
    theme = 'light';
    await preview('vault-no-selection');
    await restored('GitHub');
    check(checks, 'Returning to light theme preserves the same view context', !(await view()).selected.length && (await view()).context.query === 'GitHub');
  });
  await run('Archive supports same-scenario back, theme change, and reopening', async checks => {
    await fixture('vault-archive');
    await frame.locator('[data-v-search]').fill('Legacy');
    await backTo('vault-archive', 'Legacy', 'archive');
    check(checks, 'Back removes the preselected archive detail without changing scope', !(await view()).details.length && (await view()).context.scopeRowIds.join() === 'archived-email');
    await page.locator('.theme-control button[data-theme="dark"]').click();
    theme = 'dark';
    await preview('vault-archive');
    await restored('Legacy', 'archive');
    await frame.waitForFunction(() => !document.querySelector('.v-pane-host .v-detail'));
    check(checks, 'Theme reload does not reselect the archived item', !(await view()).selected.length);
    await row('archived-email').click();
    await preview('vault-archive');
    await frame.waitForFunction(() => document.querySelector('.v-pane-host .v-detail')?.dataset.vRecord === 'archived-email');
    await restored('Legacy', 'archive');
    check(checks, 'The same archive row opens again with its search intact', (await view()).details.join() === 'archived-email' && (await view()).selected.join() === 'archived-email');
  });
  await run('Trash supports same-scenario back and reopening', async checks => {
    await fixture('vault-trash');
    await frame.locator('[data-v-search]').fill('VPN');
    await backTo('vault-trash', 'VPN', 'trash');
    check(checks, 'Trash results return without selecting an item', !(await view()).selected.length && (await view()).context.scopeRowIds.join() === 'trash-vpn');
    await row('trash-vpn').click();
    await preview('vault-trash');
    await frame.waitForFunction(() => document.querySelector('.v-pane-host .v-detail')?.dataset.vRecord === 'trash-vpn');
    await restored('VPN', 'trash');
    check(checks, 'The deleted item reopens with its query intact', (await view()).details.join() === 'trash-vpn' && (await view()).selected.join() === 'trash-vpn');
  });
  await run('Manual gallery navigation resets to the representative fixture', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('GitHub');
    await fixture('vault-no-selection');
    await frame.waitForFunction(() => document.querySelector('[data-v-search]')?.value === '');
    check(checks, 'Re-selecting the current gallery scenario clears interactive context', (await view()).context.query === '' && !(await view()).selected.length);
    await fixture('vault-detail-github');
    check(checks, 'A manually selected detail uses its full fresh fixture', (await view()).context.scope === 'all' && (await view()).rows.length > 3 && (await view()).context.query === '');
  });
  await run('Lock and authentication boundaries clear view memory', async checks => {
    await fixture('vault-no-selection');
    await frame.locator('[data-v-search]').fill('GitHub');
    await frame.locator('.app-nav [data-page="login"]').click();
    await preview('login-locked');
    check(checks, 'Lock removes the vault and its context capture function', await frame.locator('.v-surface').count() === 0 && await frame.evaluate(() => typeof window.captureReviewVaultContext === 'undefined'));
    await frame.locator('input[type="password"]').fill('ContextAudit-Synthetic-Secret-8234');
    await frame.evaluate(() => navigate('vault-no-selection'));
    await preview('vault-no-selection');
    check(checks, 'A synthetic post-authentication return starts with no prior query', (await view()).context.query === '' && !(await view()).selected.length);
    await frame.locator('[data-v-search]').fill('Google');
    await frame.evaluate(() => navigate('login-account'));
    await preview('login-account');
    await frame.evaluate(() => navigate('vault-no-selection'));
    await preview('vault-no-selection');
    check(checks, 'The sign-in boundary also clears prior list memory', (await view()).context.query === '');
  });
  await run('Context messages contain only view state and never use persistent storage', async checks => {
    const audit = await page.evaluate(() => window.__vaultContextAudit);
    const permitted = new Set(['version', 'sceneId', 'originScene', 'scope', 'title', 'query', 'itemType', 'favoritesOnly', 'sort', 'scopeRowIds', 'visibleRowIds', 'listScrollTop', 'focus', 'resultsOnly', 'intent']);
    check(checks, 'Context messages were observed', audit.contextMessages.length > 0, { messages: audit.contextMessages.length });
    check(checks, 'Every transmitted context field is a view-state field', audit.contextMessages.every(message => Object.keys(message).every(key => permitted.has(key))));
    check(checks, 'No context contains the synthetic password entered during the lock flow', !JSON.stringify(audit.contextMessages).includes('ContextAudit-Synthetic-Secret-8234'));
    const storage = await Promise.all(page.frames().map(item => item.evaluate(() => window.__vaultContextAudit?.storageWrites || [])));
    check(checks, 'No web storage writes were made', storage.flat().length === 0);
    check(checks, 'No cookies were created', (await context.cookies()).length === 0);
    check(checks, 'No external requests were made', report.externalRequests.length === 0);
  });
} finally {
  report.pass = report.flows.every(flow => flow.pass) && report.browserErrors.length === 0 && report.externalRequests.length === 0;
  report.checkCount = report.flows.reduce((total, flow) => total + flow.checks.length, 0);
  report.failureCount = report.flows.reduce((total, flow) => total + flow.checks.filter(item => !item.pass).length, 0);
  await fs.writeFile(path.join(out, 'vault-context-validation.json'), JSON.stringify(report, null, 2) + '\n');
  await browser.close();
  console.log(JSON.stringify({ pass: report.pass, flows: report.flows.length, checks: report.checkCount, failures: report.failureCount }));
  if (!report.pass) process.exitCode = 1;
}
