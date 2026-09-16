import { galleryContextScript } from './redesign-context.mjs';
/**
 * Build the self-contained review workspace for the Fluent redesign proposal.
 * Screen frames, raster exports, and the baseline atlas are sibling artifacts.
 */
export function renderGallery({ scenarios = [], coverage = [], inventory = [] } = {}) {
  const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (character) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[character]));
  const list = (value) => Array.isArray(value) ? value : value ? [value] : [];
  const surfaces = Array.isArray(inventory) ? inventory : inventory.surfaces || [];
  const records = scenarios.map((scenario) => ({
    ...scenario,
    title: scenario.title || scenario.id,
    group: scenario.group || 'Other surfaces',
    sourceIds: list(scenario.sourceIds),
    changes: list(scenario.changes),
    notes: list(scenario.notes),
    baselineFilename: scenario.baselineFilename ? String(scenario.baselineFilename).replace(/^screenshots[\\/]/, '') : null,
  }));
  const initial = records.find((scenario) => scenario.id === 'vault-no-selection')
    || records.find((scenario) => scenario.page === 'vault' && /overview|default|all/i.test(scenario.variant || ''))
    || records.find((scenario) => scenario.page === 'vault') || records[0];
  if (!initial) throw new Error('The review gallery requires at least one scenario.');
  const mapping = new Map(coverage.map((entry) => [entry.inventoryId, entry]));
  const rows = surfaces.map((surface) => ({
    ...surface,
    inventoryId: surface.id,
    ...(mapping.get(surface.id) || { scenarioId: null, scope: 'boundary', notes: 'No proposal mapping was supplied.' }),
  }));
  for (const entry of coverage) {
    if (!rows.some((row) => row.inventoryId === entry.inventoryId)) {
      rows.push({ title: entry.inventoryId, ...entry });
    }
  }
  const groups = [...new Set(records.map((scenario) => scenario.group))];
  const counts = { direct: 0, family: 0, boundary: 0 };
  rows.forEach((row) => { counts[row.scope] = (counts[row.scope] || 0) + 1; });
  const icon = (name, className = '') => {
    const paths = {
      shield: '<path d="M12 3 4.5 6v5.5c0 4.2 3 7.7 7.5 9.5 4.5-1.8 7.5-5.3 7.5-9.5V6L12 3Z"/><path d="M9 11.5h6v5H9zM10 11.5V10a2 2 0 0 1 4 0v1.5"/>',
      search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m15.5 15.5 4.5 4.5"/>',
      arrow: '<path d="M7 17 17 7M7 7h10v10"/>',
      download: '<path d="M12 3v12m-5-5 5 5 5-5M5 16v5h14v-5"/>',
      panel: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M15 4v16"/>',
      sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/>',
      moon: '<path d="M20.5 14A8.8 8.8 0 0 1 10 3.5 9 9 0 1 0 20.5 14Z"/>',
      grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
      chevron: '<path d="m9 5 7 7-7 7"/>',
      menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
      check: '<path d="m5 12 4 4L19 6"/>',
    };
    return `<svg class="icon ${escape(className)}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] || paths.grid}</svg>`;
  };
  const data = JSON.stringify({ scenarios: records, coverage: rows, initialId: initial.id })
    .replace(/</g, '\\u003c').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
  const navigation = groups.map((group) => `<section class="nav-group" data-group="${escape(group)}"><h2>${escape(group)}<span>${records.filter((scenario) => scenario.group === group).length}</span></h2>${records.filter((scenario) => scenario.group === group).map((scenario) => `<button type="button" class="screen-link${scenario.id === initial.id ? ' selected' : ''}" data-scenario="${escape(scenario.id)}"${scenario.id === initial.id ? ' aria-current="page"' : ''}><span>${escape(scenario.title)}</span>${icon('chevron')}</button>`).join('')}</section>`).join('');
  const coverageMarkup = rows.map((row) => {
    const scenario = records.find((record) => record.id === row.scenarioId);
    return `<tr data-coverage-search="${escape([row.inventoryId, row.title, row.source, row.scope, scenario?.title, ...list(row.notes)].join(' ').toLowerCase())}" data-scope="${escape(row.scope)}"><td><strong>${escape(row.title || row.inventoryId)}</strong><code>${escape(row.inventoryId)}</code>${row.source ? `<span class="source-path">${escape(row.source)}</span>` : ''}</td><td><span class="scope-tag ${escape(row.scope)}">${escape(row.scope)}</span></td><td>${scenario ? `<button type="button" class="text-button coverage-screen" data-open-scenario="${escape(scenario.id)}">${escape(scenario.title)} ${icon('arrow')}</button>` : '<span class="muted">No native proposal</span>'}</td><td>${list(row.notes).map((note) => `<p>${escape(note)}</p>`).join('') || '<span class="muted">See the scenario notes.</span>'}</td></tr>`;
  }).join('');

  return `<!doctype html>
<html lang="en" data-theme="light"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light dark"><meta name="description" content="Offline Fluent design proposal for WinVaultWarden, with screen comparisons, light and dark exports, and source-surface coverage."><title>WinVaultWarden · Fluent design review</title><style>
:root{color-scheme:light;--canvas:#f3f3f3;--chrome:#f8f8f8;--surface:#fff;--soft:#f7f7f7;--hover:#eaeaea;--line:#e0e0e0;--line-strong:#d0d0d0;--ink:#202020;--muted:#626262;--accent:#0067c0;--accent-soft:#e5f1fb;--accent-ink:#005a9e;--stage:#e7e7e7;--positive:#107c10;--caution:#876100;--caution-soft:#fff5d7;--shadow:0 2px 6px #0000000c,0 10px 30px #00000009;--body:'Segoe UI Variable Text','Segoe UI',sans-serif;--display:'Segoe UI Variable Display','Segoe UI',sans-serif;font:13px/1.5 var(--body);font-synthesis:none;color:var(--ink);background:var(--canvas)}
html[data-theme=dark]{color-scheme:dark;--canvas:#202020;--chrome:#252525;--surface:#2c2c2c;--soft:#282828;--hover:#373737;--line:#3c3c3c;--line-strong:#505050;--ink:#f5f5f5;--muted:#b5b5b5;--accent:#60cdff;--accent-soft:#1d3c4c;--accent-ink:#8edcff;--stage:#181818;--positive:#8de58d;--caution:#fce100;--caution-soft:#3c351b;--shadow:0 2px 6px #00000020,0 10px 30px #00000020}
*{box-sizing:border-box}body{margin:0}button,input,select{font:inherit}button,input,select,a{touch-action:manipulation}button{cursor:pointer;color:inherit}a{color:var(--accent-ink);text-underline-offset:3px}button{border:0;background:none}h1,h2,h3,p{margin:0}button:focus-visible,a:focus-visible,input:focus-visible,select:focus-visible,summary:focus-visible{outline:2px solid var(--accent);outline-offset:3px}button:disabled{cursor:default;opacity:.45}input,select{color:var(--ink)}[hidden]{display:none!important}.icon{width:18px;height:18px;flex:none;display:inline-block;vertical-align:middle}code,.mono{font-family:'Cascadia Code',Consolas,monospace;font-size:11px}.muted{color:var(--muted)}.skip-link{position:fixed;top:-60px;left:12px;padding:10px 15px;z-index:100;background:var(--surface);border:1px solid var(--accent);border-radius:5px}.skip-link:focus{top:12px}.app-header{height:58px;display:flex;align-items:center;gap:24px;padding:0 22px;border-bottom:1px solid var(--line);background:var(--chrome)}.brand{display:flex;align-items:center;gap:10px;white-space:nowrap;font-weight:600;font-size:15px}.brand-mark{height:28px;width:28px;background:var(--accent);border-radius:7px;display:grid;place-items:center;color:#fff}.brand-mark .icon{width:21px;height:21px}html[data-theme=dark] .brand-mark{color:#102832}.header-divider{height:20px;width:1px;background:var(--line-strong)}.review-label{color:var(--muted);font-size:12px;white-space:nowrap}.header-tabs{align-self:stretch;display:flex;gap:22px;margin-left:6px}.header-tab{position:relative;color:var(--muted);font-weight:600;padding:0 2px}.header-tab[aria-selected=true]{color:var(--ink)}.header-tab[aria-selected=true]:after{content:'';position:absolute;height:3px;border-radius:3px;bottom:0;left:0;right:0;background:var(--accent)}.header-links{display:flex;align-items:center;gap:18px;margin-left:auto;font-size:12px}.header-links a{text-decoration:none;color:var(--muted);white-space:nowrap}.header-links a:hover{color:var(--ink)}.header-links .icon{width:13px;height:13px;margin-left:3px}.menu-button{display:none}.app-body{height:calc(100dvh - 58px);display:grid;grid-template-columns:224px minmax(0,1fr)}.sidebar{display:flex;flex-direction:column;min-height:0;min-width:0;border-right:1px solid var(--line);background:var(--chrome)}.sidebar-head{padding:20px 16px 14px}.sidebar-title{font-size:13px;font-weight:600;display:flex;justify-content:space-between;align-items:center;margin-bottom:12px}.sidebar-title small{font-size:11px;font-weight:400;color:var(--muted)}.search{position:relative;display:flex;align-items:center}.search>.icon{position:absolute;left:10px;color:var(--muted);width:16px;height:16px;pointer-events:none}.search input{width:100%;height:34px;min-width:0;background:var(--surface);border:1px solid var(--line-strong);border-bottom-color:#898989;border-radius:5px;padding:0 28px 0 33px;outline-offset:2px;font-size:12px}.search input::placeholder{color:var(--muted)}.sidebar nav{overflow:auto;flex:1;scrollbar-width:thin;padding:0 8px 20px}.nav-group{margin-top:9px}.nav-group h2{padding:10px 10px 5px;display:flex;justify-content:space-between;gap:5px;color:var(--muted);font-size:11px;font-weight:600}.nav-group h2 span{font-weight:400;font-variant-numeric:tabular-nums}.screen-link{position:relative;display:flex;align-items:center;gap:8px;text-align:left;width:100%;padding:8px 10px 8px 14px;border-radius:5px;font-size:12px;line-height:1.45;margin:1px 0;min-height:35px}.screen-link span{flex:1;min-width:0}.screen-link>.icon{height:12px;width:12px;opacity:0;color:var(--muted)}.screen-link:hover{background:var(--hover)}.screen-link.selected{background:var(--hover);font-weight:600}.screen-link.selected:before{content:'';width:3px;height:17px;border-radius:4px;position:absolute;left:3px;top:calc(50% - 8.5px);background:var(--accent)}.screen-link.selected>.icon,.screen-link:hover>.icon{opacity:1}.sidebar-empty{padding:22px 12px;color:var(--muted);font-size:12px}.sidebar-footer{padding:13px 16px;border-top:1px solid var(--line);font-size:11px;color:var(--muted)}.sidebar-footer a{display:flex;align-items:center;gap:8px;text-decoration:none;margin-top:7px;font-size:12px}.sidebar-footer .icon{width:15px;height:15px}.workspace{min-width:0;min-height:0;overflow:auto}.screen-workspace{height:100%;display:grid;grid-template-rows:auto minmax(0,1fr) auto;min-width:0}.screen-heading{padding:20px 26px 16px;background:var(--canvas);border-bottom:1px solid var(--line)}.breadcrumb{display:flex;align-items:center;gap:7px;color:var(--muted);font-size:11px;margin-bottom:6px}.breadcrumb .icon{width:11px;height:11px}.title-row{display:flex;align-items:center;justify-content:space-between;gap:20px}.title-copy{min-width:0}h1{font-family:var(--display);font-size:25px;letter-spacing:-.5px;font-weight:600;line-height:1.25}.screen-subtitle{margin-top:6px;color:var(--muted);font-size:12px}.review-pill{font-size:11px;font-weight:600;color:var(--accent-ink);background:var(--accent-soft);padding:4px 9px;border-radius:4px;white-space:nowrap}.review-toolbar{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-top:17px;flex-wrap:wrap}.toolbar-start,.toolbar-end{display:flex;align-items:center;gap:9px;flex-wrap:wrap}.segmented{display:inline-flex;padding:3px;background:var(--hover);border:1px solid var(--line);border-radius:6px;gap:2px}.segmented button{padding:5px 12px;border-radius:4px;color:var(--muted);font-size:12px;font-weight:500;white-space:nowrap;min-height:30px}.segmented button[aria-pressed=true],.segmented button[aria-selected=true]{color:var(--ink);background:var(--surface);box-shadow:0 1px 3px #00000012}.segmented .icon{height:15px;width:15px;margin-right:5px}.theme-control button{padding-inline:10px}.toolbar-separator{height:21px;width:1px;background:var(--line-strong);margin:0 3px}.quiet-button,.quiet-link{display:inline-flex;align-items:center;justify-content:center;gap:7px;min-height:32px;border:1px solid transparent;border-radius:5px;padding:5px 9px;text-decoration:none;color:var(--ink);font-size:12px;white-space:nowrap}.quiet-button:hover,.quiet-link:hover{background:var(--hover)}.quiet-button[aria-pressed=true]{background:var(--accent-soft);color:var(--accent-ink)}.quiet-button .icon,.quiet-link .icon{width:16px;height:16px}.export-link{border-color:var(--line-strong);background:var(--surface);box-shadow:0 1px 1px #00000005}.export-link:hover{background:var(--soft)}.review-layout{display:grid;grid-template-columns:minmax(0,1fr) 260px;min-height:0;min-width:0}.review-layout.notes-hidden{grid-template-columns:minmax(0,1fr)}.review-layout.notes-hidden .note-rail{display:none}.canvas-area{display:flex;flex-direction:column;min-height:0;min-width:0;background:var(--stage);padding:18px 22px}.stage-header{display:flex;align-items:center;justify-content:space-between;gap:15px;padding:0 1px 13px;color:var(--muted);font-size:11px}.stage-header strong{color:var(--ink);font-weight:600}.stage-header>span{white-space:nowrap}.stage-header button{padding:0;color:var(--accent-ink);font-size:11px}.preview-stage{flex:1;min-height:0;min-width:0;display:flex;align-items:center;justify-content:center;position:relative}.frame-wrap{position:relative;flex:none;overflow:hidden;box-shadow:var(--shadow);border:1px solid var(--line-strong);border-radius:7px;background:var(--surface);max-width:100%;max-height:100%}.frame-wrap iframe{width:1280px;height:900px;position:absolute;inset:0;transform-origin:0 0;border:0;background:var(--surface);display:block}.preview-footnote{display:flex;justify-content:space-between;gap:16px;padding:13px 0 0;font-size:11px;color:var(--muted)}.preview-footnote a{white-space:nowrap}.comparison{width:100%;height:100%;min-height:0;display:grid;grid-template-columns:1fr 1fr;gap:15px;align-content:center}.comparison-item{min-width:0;display:flex;flex-direction:column;gap:10px}.comparison-label{font-size:12px;font-weight:600;display:flex;justify-content:space-between;gap:10px;align-items:center}.comparison-label small{font-size:10px;font-weight:400;color:var(--muted)}.compare-image-box{aspect-ratio:1280/900;display:grid;place-items:center;background:var(--soft);border:1px solid var(--line-strong);border-radius:6px;overflow:hidden;box-shadow:var(--shadow);min-width:0}.compare-image-box a{height:100%;width:100%;display:block;position:relative}.compare-image-box img{height:100%;width:100%;display:block;object-fit:contain}.compare-caption{font-size:11px;color:var(--muted);line-height:1.5;min-height:34px}.no-baseline{padding:24px;text-align:center;color:var(--muted);font-size:12px;max-width:310px}.no-baseline .icon{height:24px;width:24px;margin-bottom:10px}.no-baseline strong{font-weight:600;display:block;color:var(--ink);margin-bottom:4px}.note-rail{background:var(--surface);border-left:1px solid var(--line);padding:20px 20px 24px;overflow:auto;scrollbar-width:thin;min-width:0}.note-rail h2{font-size:14px;font-weight:600;margin-bottom:4px}.note-rail .note-caption{color:var(--muted);font-size:11px}.notes-section{margin-top:23px}.notes-section h3{font-size:11px;font-weight:600;color:var(--muted);margin-bottom:9px}.change-list{padding:0;margin:0;list-style:none;display:flex;flex-direction:column;gap:12px}.change-list li{position:relative;padding-left:14px;font-size:12px;line-height:1.6}.change-list li:before{content:'';position:absolute;left:0;top:7px;background:var(--accent);height:4px;width:4px;border-radius:50%}.scope-note{margin:14px 0 0;padding:11px 12px;background:var(--soft);border:1px solid var(--line);border-radius:5px;font-size:11px;color:var(--muted);line-height:1.6}.scope-note strong{font-weight:600;color:var(--ink)}.notes-section p{font-size:12px;color:var(--muted);margin-top:9px;line-height:1.65;overflow-wrap:anywhere}.source-link{display:block;font-size:10px;font-family:'Cascadia Code',Consolas,monospace;margin:7px 0;overflow-wrap:anywhere;line-height:1.6;text-decoration:none}.source-link:hover{text-decoration:underline}.source-detail{border-top:1px solid var(--line);margin-top:22px;padding-top:15px}.source-detail summary{font-size:12px;cursor:pointer}.source-detail .note-caption{margin:9px 0 12px;font-size:11px}.source-detail .text-button{margin-top:8px}.text-button{padding:0;text-align:left;color:var(--accent-ink);font-size:12px;text-decoration:none}.text-button:hover{text-decoration:underline}.text-button .icon{height:13px;width:13px;margin-left:2px}.review-status{background:var(--chrome);border-top:1px solid var(--line);display:flex;justify-content:space-between;gap:20px;align-items:center;padding:8px 24px;font-size:11px;color:var(--muted);min-height:36px}.review-status .icon{width:13px;height:13px;margin-right:4px;color:var(--positive)}.status-right{white-space:nowrap}.coverage-workspace{padding:28px 32px 40px;max-width:1600px;margin:0 auto}.coverage-heading{display:flex;justify-content:space-between;gap:25px;align-items:start}.coverage-heading h1{font-size:28px}.coverage-intro{max-width:760px;font-size:13px;color:var(--muted);margin-top:10px;line-height:1.65}.coverage-summary{display:flex;gap:20px;align-items:center;flex-wrap:wrap;margin-top:22px;padding:14px 0;border-top:1px solid var(--line);border-bottom:1px solid var(--line);font-size:12px}.coverage-summary strong{font-weight:600}.coverage-summary>span:first-child{margin-right:auto}.scope-tag{font-size:10px;line-height:1.5;padding:3px 7px;border-radius:4px;display:inline-block;text-transform:capitalize;white-space:nowrap;background:var(--hover);color:var(--muted)}.scope-tag.direct{background:var(--accent-soft);color:var(--accent-ink)}.scope-tag.family{background:var(--soft);border:1px solid var(--line-strong);padding:2px 6px;color:var(--ink)}.scope-tag.boundary{background:var(--caution-soft);color:var(--caution)}.scope-definitions{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:24px;margin-top:22px}.scope-definitions p{font-size:12px;color:var(--muted);line-height:1.65;margin-top:7px}.coverage-toolbar{display:flex;gap:12px;align-items:center;margin:26px 0 14px}.coverage-toolbar .search{flex:1;max-width:540px}.coverage-toolbar select{height:34px;border:1px solid var(--line-strong);border-radius:5px;background:var(--surface);padding:0 10px}.coverage-count{font-size:11px;color:var(--muted);margin-left:auto}.coverage-table-wrap{border:1px solid var(--line);border-radius:7px;overflow:auto;background:var(--surface)}.coverage-table{width:100%;border-collapse:collapse;font-size:12px}.coverage-table th{text-align:left;padding:12px 15px;font-size:11px;font-weight:600;background:var(--soft);color:var(--muted);border-bottom:1px solid var(--line);white-space:nowrap}.coverage-table td{vertical-align:top;border-bottom:1px solid var(--line);padding:14px 15px;line-height:1.65}.coverage-table tr:last-child td{border-bottom:0}.coverage-table td:first-child{width:28%;min-width:230px}.coverage-table td:nth-child(2){width:95px}.coverage-table td:nth-child(3){width:23%;min-width:190px}.coverage-table td:last-child{min-width:250px;color:var(--muted)}.coverage-table td strong{font-size:12px;font-weight:600;display:block}.coverage-table code{display:block;color:var(--muted);font-size:10px;overflow-wrap:anywhere;margin-top:3px}.source-path{display:block;color:var(--muted);font-size:10px;overflow-wrap:anywhere;margin-top:5px}.coverage-table p+p{margin-top:8px}.coverage-empty{text-align:center;padding:25px;color:var(--muted)}.coverage-footer{margin-top:20px;color:var(--muted);font-size:11px;display:flex;justify-content:space-between;gap:20px}.mobile-nav-backdrop{display:none}.image-error{padding:24px;color:var(--muted);font-size:12px;line-height:1.6;text-align:center}.image-error a{margin-top:8px;height:auto}.sr-only{clip:rect(0,0,0,0);clip-path:inset(50%);height:1px;overflow:hidden;position:absolute;white-space:nowrap;width:1px}
@media(min-width:1900px){.app-body{grid-template-columns:244px minmax(0,1fr)}.review-layout{grid-template-columns:minmax(0,1fr) 296px}.screen-heading{padding:24px 32px 18px}.canvas-area{padding:22px 30px}.note-rail{padding:24px}.screen-link{font-size:13px}.nav-group h2{font-size:12px}}
@media(max-width:1350px){.app-body{grid-template-columns:205px minmax(0,1fr)}.review-layout{grid-template-columns:minmax(0,1fr) 226px}.screen-heading{padding:18px 20px 14px}.canvas-area{padding:16px}.note-rail{padding:18px 16px}.header-links{gap:12px}.header-links .rationale-link{display:none}.review-label{display:none}.header-divider{display:none}.review-toolbar{gap:7px}.quiet-link{padding-inline:6px}.toolbar-start,.toolbar-end{gap:4px}.theme-control button{padding-inline:7px}.coverage-workspace{padding:24px}}
@media(max-width:1100px){.app-body{grid-template-columns:195px minmax(0,1fr)}.review-layout{grid-template-columns:minmax(0,1fr)}.note-rail{border-left:0;border-top:1px solid var(--line);max-height:310px}.review-layout:not(.notes-hidden){grid-template-rows:minmax(0,1fr) auto}.review-layout:not(.notes-hidden) .note-rail{display:grid;grid-template-columns:1fr 1fr;gap:0 24px}.note-rail>h2,.note-rail>.note-caption{grid-column:1/-1}.note-rail .source-detail{grid-column:1/-1}.notes-section{margin-top:16px}.review-layout:not(.notes-hidden) .note-rail>.scope-note{grid-column:1/-1}.title-row{gap:12px}.review-pill{font-size:10px}.header-tabs{gap:18px}.header-links .baseline-link{display:none}.review-toolbar{margin-top:14px}.toolbar-separator{display:none}.scope-definitions{gap:16px}.comparison{grid-template-columns:1fr}.comparison-item{display:grid;grid-template-columns:minmax(0,1fr);gap:7px}.comparison{overflow:auto;align-content:start}.compare-image-box{max-height:380px;aspect-ratio:auto;height:260px}.compare-caption{min-height:0}.comparison-item+.comparison-item{margin-top:12px}.screen-subtitle{max-width:560px}}
@media(max-width:760px){.app-header{height:56px;padding:0 14px;gap:13px}.brand{font-size:13px;gap:7px}.brand-mark{width:25px;height:25px}.brand-mark .icon{width:19px;height:19px}.header-tabs{gap:15px;margin-left:auto;font-size:12px}.header-links{display:none}.app-body{height:calc(100dvh - 56px);display:block}.menu-button{display:inline-flex;padding:5px;border-radius:4px;margin-left:-5px}.menu-button:hover{background:var(--hover)}.sidebar{position:fixed;top:56px;left:0;bottom:0;width:268px;z-index:40;transform:translateX(-100%);box-shadow:10px 0 30px #00000018}.sidebar.open{transform:translateX(0)}.mobile-nav-backdrop.open{display:block;position:fixed;inset:56px 0 0;z-index:35;background:#00000035;border:0}.workspace{height:100%}.screen-heading{padding:18px 17px 13px}h1{font-size:23px}.breadcrumb{font-size:10px}.title-row{align-items:start}.review-pill{padding:3px 6px;margin-top:4px}.screen-subtitle{font-size:11px;line-height:1.6}.review-toolbar{gap:9px}.toolbar-start{gap:8px}.toolbar-end{margin-left:auto}.quiet-button,.quiet-link{padding:5px 6px;gap:5px;font-size:11px}.segmented button{font-size:11px;padding:4px 9px;min-height:28px}.theme-control button{padding-inline:8px}.theme-control .icon{margin-right:3px;width:14px;height:14px}.canvas-area{padding:13px 12px}.review-layout{min-height:220px}.stage-header{font-size:10px;padding-bottom:10px;gap:10px}.stage-header>span:last-child{display:none}.preview-footnote{font-size:10px;padding-top:10px}.review-status{padding:8px 15px;font-size:10px;gap:10px}.status-right{display:none}.review-layout:not(.notes-hidden) .note-rail{display:block;max-height:260px}.note-rail{padding:17px}.note-rail .notes-section{margin-top:19px}.comparison{gap:12px}.compare-image-box{height:auto;aspect-ratio:1280/900;max-height:none}.coverage-workspace{padding:23px 17px 30px}.coverage-heading{display:block}.coverage-heading .quiet-link{margin-top:10px}.coverage-heading h1{font-size:25px}.coverage-intro{font-size:12px}.coverage-summary{gap:10px;font-size:11px}.coverage-summary>span:first-child{flex:0 0 100%}.scope-definitions{grid-template-columns:1fr;gap:15px}.scope-definitions p{margin-top:5px}.coverage-toolbar{flex-wrap:wrap;margin-top:23px;gap:8px}.coverage-toolbar .search{flex:0 0 100%;max-width:none}.coverage-toolbar select{font-size:12px;min-width:130px}.coverage-count{font-size:10px}.coverage-table td:first-child{min-width:205px}.coverage-table td:last-child{min-width:230px}.coverage-footer{display:block;line-height:1.8}.coverage-footer a{display:inline-block;margin-top:5px}}
@media(max-width:410px){.brand{font-size:12px}.app-header{gap:8px;padding-inline:12px}.brand-mark{width:23px;height:23px}.header-tabs{gap:11px;font-size:11px}.review-pill{display:none}.toolbar-end{width:100%;justify-content:flex-end}.review-toolbar{gap:7px}.screen-subtitle{max-width:320px}.preview-footnote span{max-width:230px}.preview-footnote{gap:6px}.title-row h1{font-size:22px}.review-layout{min-height:210px}}
@media(prefers-reduced-motion:reduce){*,*:before,*:after{animation:none!important;transition:none!important;scroll-behavior:auto!important}}
@media print{.app-header,.sidebar,.review-toolbar,.note-rail,.review-status,.coverage-toolbar,.coverage-heading>.quiet-link,.coverage-footer{display:none!important}.app-body{display:block;height:auto}.workspace{height:auto;overflow:visible}.screen-workspace{display:block;height:auto}.screen-heading{padding:0 0 14px;border:0}.review-layout{display:block}.canvas-area{padding:0;background:#fff;height:650px}.preview-footnote{padding-top:12px}.coverage-workspace{padding:0}.coverage-table-wrap{overflow:visible}.coverage-table td,.coverage-table th{padding:8px}.coverage-table td:first-child,.coverage-table td:nth-child(3),.coverage-table td:last-child{min-width:0}.coverage-table{font-size:9px}.coverage-table strong,.coverage-table .text-button{font-size:10px}tr{break-inside:avoid}thead{display:table-header-group}}
</style></head><body>
<a class="skip-link" href="#review-main">Skip to the selected screen</a>
<header class="app-header"><button type="button" class="menu-button" id="menu-toggle" aria-label="Open screen navigation" aria-expanded="false" aria-controls="screen-sidebar">${icon('menu')}</button><div class="brand"><span class="brand-mark">${icon('shield')}</span><span>WinVaultWarden</span></div><span class="header-divider" aria-hidden="true"></span><span class="review-label">Design review</span><div class="header-tabs" role="tablist" aria-label="Review workspace"><button type="button" class="header-tab" id="screens-tab" role="tab" aria-controls="screens-panel" aria-selected="true">Screens</button><button type="button" class="header-tab" id="coverage-tab" role="tab" aria-controls="coverage-panel" aria-selected="false" tabindex="-1">Coverage</button></div><div class="header-links"><a class="rationale-link" href="design-rationale.md">Design rationale ${icon('arrow')}</a><a class="baseline-link" href="../index.html" target="_blank" rel="noopener">Original atlas ${icon('arrow')}</a><a href="overview.png" target="_blank" rel="noopener">Overview ${icon('grid')}</a></div></header>
<div class="app-body"><button type="button" class="mobile-nav-backdrop" id="nav-backdrop" aria-label="Close screen navigation" tabindex="-1"></button><aside class="sidebar" id="screen-sidebar"><div class="sidebar-head"><div class="sidebar-title">Screen library <small id="screen-count">${records.length} proposals</small></div><label class="search">${icon('search')}<span class="sr-only">Search screens by title, group, or source ID</span><input type="search" id="screen-search" placeholder="Find a screen or state" autocomplete="off"></label></div><nav aria-label="Proposal screens" id="screen-navigation">${navigation}<p class="sidebar-empty" id="screen-empty" hidden>No matching screens. Try a page name or source ID.</p></nav><footer class="sidebar-footer"><span>Offline proposal · Illustrative data</span><a href="design-rationale.md">Design rationale ${icon('arrow')}</a><a href="../index.html" target="_blank" rel="noopener">Original UI atlas ${icon('arrow')}</a><a href="overview.png" target="_blank" rel="noopener">All pages at a glance ${icon('grid')}</a></footer></aside>
<main class="workspace" id="review-main" tabindex="-1"><section class="screen-workspace" id="screens-panel" role="tabpanel" aria-labelledby="screens-tab"><div class="screen-heading"><p class="breadcrumb"><span>Fluent design proposal</span>${icon('chevron')}<span id="selected-group">${escape(initial.group)}</span></p><div class="title-row"><div class="title-copy"><h1 id="selected-title">${escape(initial.title)}</h1><p class="screen-subtitle">Review the layout, hierarchy, and security cues before implementation.</p></div><span class="review-pill">Proposal · Not implemented</span></div><div class="review-toolbar" aria-label="Review controls"><div class="toolbar-start"><div class="segmented" role="group" aria-label="Preview mode"><button type="button" id="optimized-button" aria-pressed="true">Optimized</button><button type="button" id="compare-button" aria-pressed="false">Before / after</button></div><span class="toolbar-separator" aria-hidden="true"></span><div class="segmented theme-control" role="group" aria-label="Proposal color theme"><button type="button" data-theme="light" aria-pressed="true">${icon('sun')}Light</button><button type="button" data-theme="dark" aria-pressed="false">${icon('moon')}Dark</button></div></div><div class="toolbar-end"><button type="button" class="quiet-button" id="notes-toggle" aria-pressed="true" aria-controls="review-notes">${icon('panel')}Notes</button><a class="quiet-link export-link" id="download-current" href="renders/${escape(initial.id)}-light.png" download>${icon('download')}Save PNG</a></div></div></div>
<div class="review-layout" id="review-layout"><div class="canvas-area"><div class="stage-header"><strong id="stage-label">Optimized proposal · Light</strong><span id="frame-size">1280 × 900 · Fit to view</span></div><div class="preview-stage" id="preview-stage"><div class="frame-wrap" id="frame-wrap"><iframe title="${escape(initial.title)} — interactive design proposal" id="screen-frame" src="frames/${escape(initial.id)}.html?theme=light" width="1280" height="900"></iframe></div><div class="comparison" id="comparison" hidden><figure class="comparison-item" style="margin:0"><figcaption class="comparison-label">Original native UI <small>Capture evidence</small></figcaption><div class="compare-image-box" id="baseline-container"><a id="baseline-link" target="_blank" rel="noopener"><img id="baseline-image" alt="Original native UI capture"></a><div class="no-baseline" id="no-baseline" hidden>${icon('panel')}<strong>No matching native capture</strong>This state is represented by the original source inventory. The proposal is not a runtime observation.</div></div><p class="compare-caption" id="baseline-caption"></p></figure><figure class="comparison-item" style="margin:0"><figcaption class="comparison-label">Optimized proposal <small id="comparison-theme">Light theme</small></figcaption><div class="compare-image-box"><a id="proposal-image-link" href="renders/${escape(initial.id)}-light.png" target="_blank" rel="noopener"><img id="proposal-image" src="renders/${escape(initial.id)}-light.png" alt="Optimized design proposal"></a></div><p class="compare-caption">Rendered design study · Example data · Implementation pending</p></figure></div></div><div class="preview-footnote"><span id="preview-help">Preview controls demonstrate design intent only.</span><a id="open-full-size" href="renders/${escape(initial.id)}-light.png" target="_blank" rel="noopener">Open full-size image ${icon('arrow')}</a></div></div>
<aside class="note-rail" id="review-notes" aria-label="Selected screen review notes"><h2>Review notes</h2><p class="note-caption">What changed and how to audit it</p><section class="notes-section"><h3>Design changes</h3><ul class="change-list" id="change-list"></ul></section><section class="notes-section" id="scenario-notes-section"><h3>Scope and behavior</h3><div id="scenario-notes"></div></section><p class="scope-note"><strong>Design proposal.</strong> This is an HTML schematic of the intended WinUI experience. It does not demonstrate implemented features, native rendering, or passing tests.</p><details class="source-detail" open><summary id="source-summary">Source coverage</summary><p class="note-caption">Direct layouts and representative families remain distinct in the coverage matrix.</p><div id="source-links"></div><button type="button" class="text-button" id="show-related-coverage">Inspect related coverage ${icon('arrow')}</button></details><section class="notes-section"><h3>Review references</h3><p><a href="task-flow-refinement.md">Task-flow refinements</a></p><p><a href="review-summary.md">Page-by-page review and corrections</a></p><p><a href="design-rationale.md">Design principles and decisions</a></p><p><a href="../runtime-findings.md" target="_blank" rel="noopener">Original runtime observations</a></p></section></aside></div><footer class="review-status"><span>${icon('check')}Proposal coverage mapped to the original source inventory</span><span class="status-right" id="selected-position"></span></footer></section>
<section class="coverage-workspace" id="coverage-panel" role="tabpanel" aria-labelledby="coverage-tab" hidden><div class="coverage-heading"><div><p class="breadcrumb">Review traceability</p><h1>Every source surface, accounted for.</h1><p class="coverage-intro">The original inventory contains pages, dialogs, states, shared controls, and external boundaries. Each row links to a proposed screen or explains its review boundary. Mapping is design coverage, not implementation or runtime verification.</p></div><a class="quiet-link export-link" href="../page-inventory.json" target="_blank" rel="noopener">Original inventory ${icon('arrow')}</a></div><div class="coverage-summary"><span><strong>${rows.length}</strong> source surfaces · <strong>${records.length}</strong> proposal scenarios</span><span><strong>${counts.direct}</strong> direct</span><span><strong>${counts.family}</strong> family</span><span><strong>${counts.boundary}</strong> boundary</span></div><div class="scope-definitions"><div><span class="scope-tag direct">Direct</span><p>The linked proposal explicitly shows this surface or state.</p></div><div><span class="scope-tag family">Family</span><p>A representative screen defines the shared pattern. Read the mapping note for variants.</p></div><div><span class="scope-tag boundary">Boundary</span><p>The surface belongs to Windows, an external provider, developer tooling, or another documented limit.</p></div></div><div class="coverage-toolbar"><label class="search">${icon('search')}<span class="sr-only">Search source coverage</span><input type="search" id="coverage-search" placeholder="Search source ID, screen, path, or note" autocomplete="off"></label><label><span class="sr-only">Coverage scope</span><select id="coverage-scope"><option value="">All scopes</option><option value="direct">Direct layouts</option><option value="family">Representative families</option><option value="boundary">Review boundaries</option></select></label><button type="button" class="quiet-button" id="clear-coverage">Clear filters</button><span class="coverage-count" id="coverage-count" role="status">${rows.length} of ${rows.length} surfaces</span></div><div class="coverage-table-wrap"><table class="coverage-table"><caption class="sr-only">Complete mapping from original source surfaces to optimized design proposals</caption><thead><tr><th scope="col">Original source surface</th><th scope="col">Scope</th><th scope="col">Proposal to inspect</th><th scope="col">Coverage note</th></tr></thead><tbody id="coverage-rows">${coverageMarkup}</tbody></table><p id="coverage-empty" class="coverage-empty" hidden>No matching surfaces. Clear the filters to view the complete inventory.</p></div><footer class="coverage-footer"><span>English labels are illustrative. Source IDs preserve audit traceability.</span><span><a href="../index.html" target="_blank" rel="noopener">Original atlas</a> · <a href="design-rationale.md">Design rationale</a> · <a href="overview.png" target="_blank" rel="noopener">Page overview</a></span></footer></section></main></div><p class="sr-only" id="review-announcement" aria-live="polite"></p>
<script id="review-data" type="application/json">${data}</script><script>
(() => {
  'use strict';
  const data = JSON.parse(document.getElementById('review-data').textContent);
  const $ = (id) => document.getElementById(id);
  const all = (selector) => Array.from(document.querySelectorAll(selector));
  const byId = new Map(data.scenarios.map((screen) => [screen.id, screen]));
  const safeText = (value) => String(value == null ? '' : value).replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
  const slug = (value) => String(value).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  const initialHash = new URLSearchParams(location.hash.slice(1));
  const state = { screen: byId.has(initialHash.get('screen')) ? initialHash.get('screen') : data.initialId, theme: initialHash.get('theme') === 'dark' ? 'dark' : 'light', mode: initialHash.get('mode') === 'compare' ? 'compare' : 'optimized', tab: initialHash.get('tab') === 'coverage' ? 'coverage' : 'screens', notes: window.innerWidth > 1100, relatedIds: null };
  let frameUrl = '';
  ${galleryContextScript}
  function updateAddress() {
    const parameters = new URLSearchParams({ screen: state.screen, theme: state.theme });
    if (state.mode === 'compare') parameters.set('mode', 'compare');
    if (state.tab === 'coverage') parameters.set('tab', 'coverage');
    try { history.replaceState(null, '', '#' + parameters.toString()); } catch { /* Embedded file viewers may restrict history. */ }
  }
  function fitFrame() {
    if (state.mode !== 'optimized' || state.tab !== 'screens') return;
    const rect = $('preview-stage').getBoundingClientRect();
    const scale = Math.max(0.1, Math.min((rect.width - 2) / 1280, (rect.height - 2) / 900, 1));
    $('frame-wrap').style.width = (1280 * scale + 2) + 'px';
    $('frame-wrap').style.height = (900 * scale + 2) + 'px';
    $('screen-frame').style.transform = 'scale(' + scale + ')';
    $('frame-size').textContent = '1280 × 900 · Fit ' + Math.round(scale * 100) + '%';
  }
  function closeNavigation(returnFocus) {
    $('screen-sidebar').classList.remove('open');
    $('nav-backdrop').classList.remove('open');
    $('menu-toggle').setAttribute('aria-expanded', 'false');
    $('menu-toggle').setAttribute('aria-label', 'Open screen navigation');
    if (returnFocus) $('menu-toggle').focus();
  }
  function setTab(tab, focus) {
    state.tab = tab;
    ['screens', 'coverage'].forEach((name) => {
      $(name + '-tab').setAttribute('aria-selected', String(name === tab));
      $(name + '-tab').tabIndex = name === tab ? 0 : -1;
      $(name + '-panel').hidden = name !== tab;
    });
    if (focus) $(tab + '-tab').focus();
    updateAddress();
    requestAnimationFrame(fitFrame);
  }
  function renderScreen(announce) {
    const screen = byId.get(state.screen);
    const themeLabel = state.theme === 'dark' ? 'Dark' : 'Light';
    const png = 'renders/' + encodeURIComponent(screen.id) + '-' + state.theme + '.png';
    const nextFrame = 'frames/' + encodeURIComponent(screen.id) + '.html?theme=' + state.theme;
    document.documentElement.dataset.theme = state.theme;
    document.title = screen.title + ' · WinVaultWarden design review';
    $('selected-title').textContent = screen.title;
    $('selected-group').textContent = screen.group;
    $('stage-label').textContent = (state.mode === 'compare' ? 'Before / after' : 'Optimized proposal') + ' · ' + themeLabel;
    $('comparison-theme').textContent = themeLabel + ' theme';
    $('download-current').href = png;
    $('download-current').download = screen.id + '-' + state.theme + '.png';
    $('open-full-size').href = png;
    $('proposal-image-link').href = png;
    $('proposal-image').src = png;
    $('proposal-image').alt = screen.title + ' — optimized ' + state.theme + ' design proposal';
    if (frameUrl !== nextFrame) {
      $('screen-frame').src = nextFrame;
      $('screen-frame').title = screen.title + ' — interactive design proposal';
      frameUrl = nextFrame;
    }
    $('optimized-button').setAttribute('aria-pressed', String(state.mode === 'optimized'));
    $('compare-button').setAttribute('aria-pressed', String(state.mode === 'compare'));
    $('frame-wrap').hidden = state.mode !== 'optimized';
    $('comparison').hidden = state.mode !== 'compare';
    $('frame-size').hidden = state.mode !== 'optimized';
    $('preview-help').textContent = state.mode === 'compare' ? 'Original capture and proposal may show different data or representative states.' : 'Preview controls demonstrate design intent only.';
    all('.theme-control button').forEach((button) => button.setAttribute('aria-pressed', String(button.dataset.theme === state.theme)));
    all('.screen-link').forEach((button) => {
      const selected = button.dataset.scenario === screen.id;
      button.classList.toggle('selected', selected);
      if (selected) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    $('change-list').innerHTML = (screen.changes.length ? screen.changes : ['Apply the shared Fluent layout, typography, and interaction patterns.']).map((change) => '<li>' + safeText(change) + '</li>').join('');
    $('scenario-notes-section').hidden = !screen.notes.length;
    $('scenario-notes').innerHTML = screen.notes.map((note) => '<p>' + safeText(note) + '</p>').join('');
    $('source-summary').textContent = 'Source coverage · ' + screen.sourceIds.length;
    $('source-links').innerHTML = screen.sourceIds.map((id) => '<a class="source-link" href="../index.html#schematic-' + encodeURIComponent(slug(id)) + '" target="_blank" rel="noopener">' + safeText(id) + '</a>').join('');
    $('show-related-coverage').hidden = !screen.sourceIds.length;
    const hasBaseline = Boolean(screen.baselineFilename);
    $('baseline-link').hidden = !hasBaseline;
    $('no-baseline').hidden = hasBaseline;
    if (hasBaseline) {
      const baseline = '../screenshots/' + screen.baselineFilename.split('/').map(encodeURIComponent).join('/');
      $('baseline-link').href = baseline;
      $('baseline-image').src = baseline;
      $('baseline-image').alt = screen.title + ' — related original native capture';
      $('baseline-caption').textContent = 'Related baseline: ' + screen.baselineFilename + '. The original capture retains its recorded theme and state.';
    } else {
      $('baseline-image').removeAttribute('src');
      $('baseline-caption').textContent = 'No native screenshot is linked to this proposal. See source coverage for the original surface definition.';
    }
    $('selected-position').textContent = (data.scenarios.indexOf(screen) + 1) + ' of ' + data.scenarios.length + ' scenarios · ' + screen.id;
    $('review-layout').classList.toggle('notes-hidden', !state.notes);
    $('notes-toggle').setAttribute('aria-pressed', String(state.notes));
    updateAddress();
    requestAnimationFrame(fitFrame);
    if (announce) $('review-announcement').textContent = screen.title + ', ' + themeLabel.toLowerCase() + ' theme, ' + (state.mode === 'compare' ? 'before and after comparison' : 'optimized proposal');
  }
  function selectScreen(id, options = {}) {
    if (!byId.has(id)) return;
    if (!options.preserveVaultContext) reviewContextManualSelection();
    state.screen = id;
    setTab('screens', false);
    renderScreen(true);
    closeNavigation(false);
    $('review-main').scrollTop = 0;
  }
  function filterScreens() {
    const query = $('screen-search').value.trim().toLowerCase();
    let visible = 0;
    all('.screen-link').forEach((button) => {
      const screen = byId.get(button.dataset.scenario);
      const haystack = [screen.title, screen.id, screen.group, screen.page, screen.variant, ...screen.sourceIds].join(' ').toLowerCase();
      const matches = !query || haystack.includes(query);
      button.hidden = !matches;
      if (matches) visible += 1;
    });
    all('.nav-group').forEach((group) => { group.hidden = !Array.from(group.querySelectorAll('.screen-link')).some((button) => !button.hidden); });
    $('screen-empty').hidden = visible > 0;
    $('screen-count').textContent = query ? visible + ' matches' : data.scenarios.length + ' proposals';
  }
  function filterCoverage() {
    const query = $('coverage-search').value.trim().toLowerCase();
    const scope = $('coverage-scope').value;
    let visible = 0;
    all('#coverage-rows tr').forEach((row, index) => {
      const related = !state.relatedIds || state.relatedIds.has(data.coverage[index].inventoryId);
      const matches = related && (!query || row.dataset.coverageSearch.includes(query)) && (!scope || row.dataset.scope === scope);
      row.hidden = !matches;
      if (matches) visible += 1;
    });
    $('coverage-count').textContent = visible + ' of ' + data.coverage.length + ' surfaces' + (state.relatedIds ? ' · Selected screen' : '');
    $('coverage-empty').hidden = visible > 0;
  }
  all('[data-scenario]').forEach((button) => button.addEventListener('click', () => selectScreen(button.dataset.scenario)));
  all('[data-open-scenario]').forEach((button) => button.addEventListener('click', () => { selectScreen(button.dataset.openScenario); $('selected-title').setAttribute('tabindex', '-1'); $('selected-title').focus(); }));
  $('screen-search').addEventListener('input', filterScreens);
  $('coverage-search').addEventListener('input', filterCoverage);
  $('coverage-scope').addEventListener('change', filterCoverage);
  $('clear-coverage').addEventListener('click', () => { $('coverage-search').value = ''; $('coverage-scope').value = ''; state.relatedIds = null; filterCoverage(); });
  $('show-related-coverage').addEventListener('click', () => { state.relatedIds = new Set(byId.get(state.screen).sourceIds); $('coverage-search').value = ''; $('coverage-scope').value = ''; filterCoverage(); setTab('coverage', true); $('review-main').scrollTop = 0; });
  ['screens', 'coverage'].forEach((name) => $(name + '-tab').addEventListener('click', () => { if (name === 'coverage') { state.relatedIds = null; filterCoverage(); } setTab(name, false); }));
  all('.header-tab').forEach((tab) => tab.addEventListener('keydown', (event) => { if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return; event.preventDefault(); const focusedTab = tab.id === 'screens-tab' ? 'screens' : 'coverage'; const next = event.key === 'Home' ? 'screens' : event.key === 'End' ? 'coverage' : focusedTab === 'screens' ? 'coverage' : 'screens'; setTab(next, true); }));
  $('optimized-button').addEventListener('click', () => { state.mode = 'optimized'; renderScreen(true); });
  $('compare-button').addEventListener('click', () => { state.mode = 'compare'; renderScreen(true); });
  all('.theme-control button').forEach((button) => button.addEventListener('click', () => { if (reviewContextBeforeThemeChange(button.dataset.theme)) return; state.theme = button.dataset.theme; renderScreen(true); }));
  $('notes-toggle').addEventListener('click', () => { state.notes = !state.notes; renderScreen(false); });
  $('menu-toggle').addEventListener('click', () => { const open = !$('screen-sidebar').classList.contains('open'); $('screen-sidebar').classList.toggle('open', open); $('nav-backdrop').classList.toggle('open', open); $('menu-toggle').setAttribute('aria-expanded', String(open)); $('menu-toggle').setAttribute('aria-label', open ? 'Close screen navigation' : 'Open screen navigation'); if (open) $('screen-search').focus(); });
  $('nav-backdrop').addEventListener('click', () => closeNavigation(true));
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape' && $('screen-sidebar').classList.contains('open')) closeNavigation(true); });
  window.addEventListener('message', (event) => {
    if (reviewContextMessage(event)) return;
    if (event.source !== $('screen-frame').contentWindow) return;
    if (event.data?.type !== 'wvw-review-navigate' || typeof event.data.id !== 'string' || !byId.has(event.data.id)) return;
    reviewContextNavigate(event.data.id, event.data.context);
    const selected = document.querySelector('.screen-link.selected');
    if (selected) selected.scrollIntoView({ block: 'nearest' });
  });
  $('baseline-image').addEventListener('error', () => { $('baseline-link').hidden = true; $('no-baseline').hidden = false; $('baseline-caption').textContent = 'The linked native capture could not be loaded. Inspect the original atlas for its evidence record.'; });
  $('screen-frame').addEventListener('load', () => { fitFrame(); reviewContextFrameLoaded(); });
  if ('ResizeObserver' in window) new ResizeObserver(fitFrame).observe($('preview-stage'));
  window.addEventListener('resize', fitFrame);
  setTab(state.tab, false);
  renderScreen(false);
  requestAnimationFrame(() => { const selected = document.querySelector('.screen-link.selected'); if (selected) selected.scrollIntoView({ block: 'nearest' }); });
})();
</script></body></html>`;
}
