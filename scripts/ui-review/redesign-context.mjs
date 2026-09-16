/**
 * Memory-only list context for the interactive review gallery.
 * Only list controls, scope identifiers, scroll position, and focus are retained.
 */
const contextSanitizer = String.raw`
  function cleanReviewVaultContext(value) {
    if (!value || typeof value !== 'object' || value.version !== 1) return null;
    const token = input => typeof input === 'string' && /^[a-zA-Z0-9:_-]{1,100}$/.test(input) ? input : '';
    const ids = input => Array.isArray(input) ? [...new Set(input.slice(0, 512).map(token).filter(Boolean))] : [];
    const sceneId = token(value.sceneId), originScene = token(value.originScene), scope = token(value.scope);
    if (!sceneId || !originScene || !scope) return null;
    const types = ['', 'login', 'card', 'identity', 'note', 'ssh', 'bank', 'license', 'passport'];
    const focusKind = ['search', 'type', 'favorite', 'sort', 'row'].includes(value.focus?.kind) ? value.focus.kind : 'search';
    return {
      version: 1, sceneId, originScene, scope,
      title: typeof value.title === 'string' ? value.title.replace(/[\u0000-\u001f]/g, '').slice(0, 80) : 'All items',
      query: typeof value.query === 'string' ? value.query.replace(/[\u0000-\u001f]/g, '').slice(0, 512) : '',
      itemType: types.includes(value.itemType) ? value.itemType : '',
      favoritesOnly: value.favoritesOnly === true,
      sort: ['default', 'name', 'name-desc'].includes(value.sort) ? value.sort : 'default',
      scopeRowIds: ids(value.scopeRowIds), visibleRowIds: ids(value.visibleRowIds),
      listScrollTop: Number.isFinite(value.listScrollTop) ? Math.max(0, Math.min(1000000, value.listScrollTop)) : 0,
      focus: { kind: focusKind, rowId: focusKind === 'row' ? token(value.focus?.rowId) : '' },
      resultsOnly: value.resultsOnly === true,
      intent: ['back-results', 'open-item'].includes(value.intent) ? value.intent : 'navigate',
    };
  }
`;

export const frameContextScript = String.raw`
(function () {
  const surface = document.querySelector('.v-surface');
  if (!surface) return;
  const list = surface.querySelector('.v-list');
  const rowsHost = list?.querySelector('[data-v-rows]');
  const paneHost = surface.querySelector('.v-pane-host');
  if (!list || !rowsHost || !paneHost) return;
  ${contextSanitizer}
  const originalRows = [...rowsHost.querySelectorAll('[data-v-row]')];
  const originalOrder = originalRows.map(row => row.dataset.vRow);
  const rowNodes = new Map(originalRows.map(row => [row.dataset.vRow, row]));
  const findScene = id => scenes.find(scene => scene.id === id);
  const scene = currentScene || findScene(current);
  const scopeOrigin = scene.dialog === 'permanent-delete' || scene.variant === 'trash' ? 'vault-trash'
    : scene.variant === 'archive' ? 'vault-archive'
    : scene.variant === 'favorites' ? 'vault-favorites'
    : scene.variant === 'folder' ? 'vault-folder-' + String(scene.folder || 'Work').toLowerCase()
    : scene.variant === 'organization' ? 'vault-organization' : 'vault-no-selection';
  let view = {
    scope: scopeOrigin === 'vault-no-selection' ? 'all' : scopeOrigin.slice(6),
    originScene: scopeOrigin,
    title: list.querySelector('.list-heading h1')?.textContent || 'All items',
    scopeRowIds: originalOrder,
    focus: { kind: 'search', rowId: '' },
  };
  let navigationIntent = 'navigate';
  let captureScheduled = false;
  let applyingContext = false;
  let restoredToken = '';
  const post = data => { if (parent !== window) parent.postMessage(data, '*'); };
  const control = name => list.querySelector('[data-v-' + name + ']');
  function captureFocus() {
    const focused = document.activeElement;
    if (focused?.closest('[data-v-row]')?.parentElement === rowsHost) {
      return { kind: 'row', rowId: focused.closest('[data-v-row]').dataset.vRow };
    }
    const kind = focused === control('search') ? 'search' : focused === control('filter') ? 'type'
      : focused === control('favorite-filter') ? 'favorite' : focused === control('sort') ? 'sort' : '';
    return kind ? { kind, rowId: '' } : view.focus;
  }
  window.captureReviewVaultContext = function () {
    if (['empty', 'loading'].includes(scene.variant) || list.dataset.vLocalFolder) return null;
    view.focus = captureFocus();
    return cleanReviewVaultContext({
      version: 1, sceneId: current, ...view,
      query: control('search')?.value || '', itemType: control('filter')?.value || '',
      favoritesOnly: !!control('favorite-filter')?.checked, sort: control('sort')?.value || 'default',
      scopeRowIds: [...rowsHost.querySelectorAll('[data-v-row]')].map(row => row.dataset.vRow),
      visibleRowIds: [...rowsHost.querySelectorAll('[data-v-row]')].filter(row => !row.hidden).map(row => row.dataset.vRow),
      listScrollTop: rowsHost.scrollTop, intent: navigationIntent,
      resultsOnly: !!paneHost.querySelector('.v-detail-empty'),
    });
  };
  function queueCapture() {
    if (applyingContext || captureScheduled) return;
    captureScheduled = true;
    queueMicrotask(() => {
      captureScheduled = false;
      if (!applyingContext) post({ type: 'wvw-review-vault-context', context: window.captureReviewVaultContext() });
    });
  }
  function emptyResultsPane() {
    const empty = document.createElement('section');
    empty.className = 'detail-pane empty-pane v-detail-empty';
    const emblem = document.createElement('div');
    emblem.className = 'empty-emblem';
    const shield = document.querySelector('.vault-switch .icon');
    if (shield) emblem.append(shield.cloneNode(true));
    const heading = document.createElement('h2');
    heading.textContent = 'Choose an item';
    const description = document.createElement('p');
    description.textContent = view.scope === 'archive' ? 'Select an archived item to see its details.'
      : view.scope === 'trash' ? 'Select a deleted item to restore it or review its details.'
      : view.scope === 'organization' ? 'Choose an item shared with you in the Engineering collection.'
      : 'Select a login, card or key to see its details.';
    empty.append(emblem, heading, description);
    paneHost.replaceChildren(empty);
    rowsHost.querySelectorAll('.vault-list-row').forEach(row => {
      row.classList.remove('selected');
      row.setAttribute('aria-current', 'false');
    });
  }
  function ensureBackButton() {
    const top = paneHost.querySelector('.v-detail .detail-top');
    if (!top || top.querySelector('[data-review-back-results]')) return;
    const breadcrumb = top.querySelector('.breadcrumb');
    const wrapper = document.createElement('div');
    wrapper.className = 'v-review-origin';
    wrapper.style.cssText = 'display:flex;flex-wrap:wrap;align-items:center;gap:4px 12px;min-width:0;flex:1';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'button v-text-action';
    button.dataset.reviewBackResults = '';
    button.textContent = '\u2190 Back to results';
    button.setAttribute('aria-label', 'Back to ' + view.title + ' results');
    wrapper.append(button);
    if (breadcrumb) { breadcrumb.style.maxWidth = '100%'; wrapper.append(breadcrumb); }
    top.prepend(wrapper);
  }
  function setNavigationScope() {
    const title = list.querySelector('.list-heading h1');
    if (title) title.textContent = view.title;
    const scopeLabel = document.querySelector('.vault-switch strong');
    if (scopeLabel) scopeLabel.textContent = view.scope === 'organization' ? 'Contoso Review' : 'All vaults';
    document.querySelectorAll('.app-nav [data-go^="vault-folder-"],.app-nav [data-go="vault-organization"]').forEach(button => {
      button.classList.toggle('is-current', button.dataset.go === view.originScene);
    });
    document.querySelector('.app-nav [data-go="vault-favorites"]')?.classList.toggle('active', view.scope === 'favorites');
    const existing = list.querySelector('.v-list-context');
    if (view.scope === 'organization' && !existing) {
      const label = document.createElement('p');
      label.className = 'v-list-context';
      label.textContent = 'Contoso Review \u00b7 Shared collection';
      list.querySelector('.list-heading')?.after(label);
    } else if (view.scope !== 'organization') existing?.remove();
  }
  function focusResults(context) {
    let target = context.focus.kind === 'row' ? rowNodes.get(context.focus.rowId)?.querySelector('.vault-list-row')
      : control(({ search: 'search', type: 'filter', favorite: 'favorite-filter', sort: 'sort' })[context.focus.kind]);
    if (target?.closest('[data-v-row]')?.hidden || !target?.isConnected || target?.disabled) target = control('search');
    const menu = target?.closest('details');
    if (menu) menu.open = true;
    target?.focus({ preventScroll: true });
    rowsHost.scrollTop = context.listScrollTop;
  }
  window.restoreReviewVaultContext = function (message) {
    if (message?.sceneId !== current || !message.token) return false;
    if (message.token === restoredToken) return true;
    const context = cleanReviewVaultContext(message.context);
    if (!context || !findScene(context.originScene) || findScene(context.originScene).page !== 'vault') return false;
    const selectedId = paneHost.querySelector('.v-detail')?.dataset.vRecord;
    const allowed = new Set(context.scopeRowIds.filter(id => rowNodes.has(id)));
    if (!allowed.size && context.scopeRowIds.length) return false;
    if (message.mode !== 'results' && selectedId && !allowed.has(selectedId)) return false;
    applyingContext = true;
    restoredToken = message.token;
    view = { scope: context.scope, originScene: context.originScene, title: context.title, scopeRowIds: [...allowed], focus: context.focus };
    // Detach other scopes so the existing filter handler cannot reveal them later.
    originalRows.forEach(row => row.remove());
    const empty = rowsHost.querySelector('[data-v-empty]');
    originalOrder.filter(id => allowed.has(id)).forEach(id => rowsHost.insertBefore(rowNodes.get(id), empty));
    if (control('search')) control('search').value = context.query;
    if (control('filter')) control('filter').value = context.itemType;
    if (control('favorite-filter')) control('favorite-filter').checked = context.favoritesOnly;
    if (control('sort')) control('sort').value = context.sort;
    setNavigationScope();
    if (message.mode === 'results') emptyResultsPane();
    control('search')?.dispatchEvent(new Event('input', { bubbles: true }));
    control('sort')?.dispatchEvent(new Event('change', { bubbles: true }));
    ensureBackButton();
    requestAnimationFrame(() => {
      rowsHost.scrollTop = context.listScrollTop;
      if (message.focusResults) focusResults(context);
      applyingContext = false;
      post({ type: 'wvw-review-context-restored', token: message.token, context: window.captureReviewVaultContext() });
    });
    return true;
  };
  // Window capture runs before the existing document handlers consume events.
  ['input', 'change', 'focusin'].forEach(type => window.addEventListener(type, event => {
    if (event.target.closest?.('.v-list')) queueCapture();
  }, true));
  window.addEventListener('scroll', event => { if (event.target === rowsHost) queueCapture(); }, true);
  window.addEventListener('click', event => {
    if (event.target.closest?.('[data-v-action="row"]')) {
      navigationIntent = 'open-item';
      // Keep the intent through document listeners in the same user event.
      setTimeout(() => { navigationIntent = 'navigate'; }, 0);
    }
    const button = event.target.closest?.('[data-review-back-results]');
    if (!button) return;
    event.preventDefault(); event.stopImmediatePropagation();
    navigationIntent = 'back-results';
    navigate(view.originScene);
    navigationIntent = 'navigate';
  }, true);
  window.addEventListener('message', event => {
    if (event.source !== parent) return;
    if (event.data?.type === 'wvw-review-capture-for-theme') {
      post({ type: 'wvw-review-theme-context', token: event.data.token, sceneId: current, context: window.captureReviewVaultContext() });
      return;
    }
    if (event.data?.type !== 'wvw-review-restore-vault-context' || event.data.sceneId !== current) return;
    if (!window.restoreReviewVaultContext(event.data)) {
      post({ type: 'wvw-review-context-restored', token: event.data.token, context: window.captureReviewVaultContext() });
    }
  });
  new MutationObserver(ensureBackButton).observe(paneHost, { childList: true, subtree: true });
  ensureBackButton();
  post({ type: 'wvw-review-context-ready', sceneId: current, context: window.captureReviewVaultContext() });
})();
`;

export const galleryContextScript = String.raw`
  ${contextSanitizer}
  const reviewVaultContexts = new Map();
  let reviewVaultLatest = null;
  let reviewVaultPending = null;
  let reviewThemePending = null;
  let reviewVaultToken = 0;
  function reviewVaultScopeFor(screen) {
    if (screen?.page !== 'vault') return null;
    if (screen.dialog === 'permanent-delete' || screen.variant === 'trash') return 'trash';
    if (screen.variant === 'archive') return 'archive';
    if (screen.variant === 'favorites') return 'favorites';
    if (screen.variant === 'folder') return 'folder-' + String(screen.folder || 'Work').toLowerCase();
    if (screen.variant === 'organization') return 'organization';
    return 'all';
  }
  function reviewVaultRemember(raw) {
    const context = cleanReviewVaultContext(raw);
    if (!context || context.sceneId !== state.screen || byId.get(context.sceneId)?.page !== 'vault') return null;
    const origin = byId.get(context.originScene);
    if (origin?.page !== 'vault' || reviewVaultScopeFor(origin) !== context.scope) return null;
    reviewVaultLatest = context;
    reviewVaultContexts.set(context.scope, context);
    return context;
  }
  function reviewContextManualSelection() {
    reviewVaultContexts.clear();
    reviewVaultLatest = null;
    reviewVaultPending = null;
    reviewThemePending = null;
    // Selecting the current scenario also reloads its representative fixture.
    frameUrl = '';
  }
  function reviewContextFrameLoaded() {
    if (reviewVaultPending?.sceneId === state.screen) {
      $('screen-frame').contentWindow?.postMessage({ type: 'wvw-review-restore-vault-context', ...reviewVaultPending }, '*');
    } else if (reviewThemePending?.sceneId === state.screen) {
      $('screen-frame').contentWindow?.postMessage({ type: 'wvw-review-capture-for-theme', token: reviewThemePending.token }, '*');
    }
  }
  function reviewContextMessage(event) {
    if (event.source !== $('screen-frame').contentWindow) return false;
    const message = event.data;
    if (message?.type === 'wvw-review-context-ready') {
      if (message.sceneId !== state.screen) return true;
      if (!reviewVaultPending) reviewVaultRemember(message.context);
      reviewContextFrameLoaded();
      return true;
    }
    if (message?.type === 'wvw-review-theme-context') {
      if (message.token !== reviewThemePending?.token || message.sceneId !== state.screen) return true;
      const theme = reviewThemePending.theme;
      reviewThemePending = null;
      const context = reviewVaultRemember(message.context);
      reviewVaultPending = context ? {
        sceneId: state.screen, token: 'context-' + (++reviewVaultToken), context,
        mode: context.resultsOnly ? 'results' : 'preserve', focusResults: false,
      } : null;
      state.theme = theme;
      renderScreen(true);
      return true;
    }
    if (message?.type === 'wvw-review-vault-context') {
      if (!reviewVaultPending) reviewVaultRemember(message.context);
      return true;
    }
    if (message?.type === 'wvw-review-context-restored') {
      if (message.token === reviewVaultPending?.token) {
        reviewVaultPending = null;
        reviewVaultRemember(message.context);
        if (reviewThemePending) reviewContextFrameLoaded();
      }
      return true;
    }
    return false;
  }
  function reviewContextNavigate(id, rawContext) {
    const target = byId.get(id);
    if (!target) return;
    reviewThemePending = null;
    if (['login', 'register'].includes(target.page)) {
      reviewContextManualSelection();
      selectScreen(id, { preserveVaultContext: true });
      return;
    }
    const source = byId.get(state.screen);
    const from = reviewVaultRemember(rawContext) || (reviewVaultLatest?.sceneId === state.screen ? reviewVaultLatest : null);
    const targetScope = reviewVaultScopeFor(target);
    const opensItem = target.page === 'vault' && (from?.intent === 'open-item' || ['detail', 'editor', 'reprompt', 'new', 'batch', 'pending-save'].includes(target.variant));
    const isBack = source?.page === 'vault' && from?.intent === 'back-results' && id === from.originScene;
    const resumesWorkspace = source?.page !== 'vault' && target.page === 'vault' && target.variant === 'no-selection';
    let context = null;
    if (target.page === 'vault') {
      if (isBack || opensItem) context = source?.page === 'vault' ? from : null;
      else if (resumesWorkspace) context = reviewVaultLatest;
      else context = reviewVaultContexts.get(targetScope) || null;
      if (context && !isBack && !opensItem && !resumesWorkspace && context.scope !== targetScope) context = null;
    }
    reviewVaultPending = context ? {
      sceneId: id, token: 'context-' + (++reviewVaultToken), context,
      mode: isBack || target.variant === 'no-selection' ? 'results' : 'preserve',
      focusResults: isBack || target.variant === 'no-selection',
    } : null;
    const reloadsFrame = id !== state.screen || from?.intent === 'open-item';
    if (from?.intent === 'open-item' && id === state.screen) frameUrl = '';
    selectScreen(id, { preserveVaultContext: true });
    // Archive and Trash use one scenario for list and details, so no load may occur.
    if (!reloadsFrame) reviewContextFrameLoaded();
  }
  function reviewContextBeforeThemeChange(theme) {
    if (!['light', 'dark'].includes(theme)) return true;
    if (theme === state.theme) { reviewThemePending = null; return true; }
    if (byId.get(state.screen)?.page !== 'vault') return false;
    // The response captures the latest controls before the theme reload starts.
    reviewThemePending = { sceneId: state.screen, token: 'theme-' + (++reviewVaultToken), theme };
    reviewContextFrameLoaded();
    return true;
  }
`;

export const contextIntegrationInstructions = [
  'Import frameContextScript in redesign-shell.mjs and append it after the existing page scripts.',
  'Include context: window.captureReviewVaultContext?.() || null in the wvw-review-navigate message sent by navigate(id).',
  'Import galleryContextScript in redesign-gallery.mjs and interpolate it inside the gallery IIFE after state and frameUrl are declared.',
  'Change selectScreen(id) to selectScreen(id, options = {}); after validating id, call reviewContextManualSelection() unless options.preserveVaultContext is true.',
  'At the top of the existing gallery message listener, call if (reviewContextMessage(event)) return; route valid wvw-review-navigate messages through reviewContextNavigate(event.data.id, event.data.context).',
  'Call reviewContextFrameLoaded() from the iframe load listener, alongside fitFrame().',
  'In the theme click handler, first call if (reviewContextBeforeThemeChange(button.dataset.theme)) return; keep the existing theme update as the fallback for other pages.',
];
