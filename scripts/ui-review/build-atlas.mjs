import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

// HTML and SVG generation is dependency-free. Sharp optionally refreshes the PNG contact sheet.
// The output works directly from file://.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'docs/ui-review');
const diagrams = path.join(output, 'diagrams');
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const slug = value => String(value ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
const list = value => Array.isArray(value) ? value : value ? [value] : [];
const sentence = value => typeof value === 'string' ? value : Array.isArray(value) ? value.map(sentence).join(' / ') : value ? Object.entries(value).map(([k, v]) => `${k}: ${sentence(v)}`).join('; ') : '';

async function readJson(relative, fallback) {
  try { return JSON.parse((await fs.readFile(path.join(output, relative), 'utf8')).replace(/^\uFEFF/, '')); }
  catch (error) { if (error.code === 'ENOENT') return fallback; throw error; }
}

function flattenInventory(raw) {
  if (Array.isArray(raw)) return raw.flatMap(value => value.entries || value.items ? flattenInventory(value.entries || value.items).map(item => ({ group: value.title || value.name || value.group, ...item })) : [value]);
  if (!raw || typeof raw !== 'object') return [];
  for (const key of ['entries', 'surfaces', 'pages', 'items', 'inventory']) if (Array.isArray(raw[key])) return flattenInventory(raw[key]);
  if (raw.groups) return flattenInventory(raw.groups);
  return [];
}

function sourceText(value) {
  if (Array.isArray(value)) return value.map(sourceText).join(' | ');
  if (value && typeof value === 'object') return `${value.path || value.file || value.source || ''}${value.line || value.startLine ? `:${value.line || value.startLine}` : ''}${value.symbol ? ` (${value.symbol})` : ''}`;
  return String(value || '');
}

function normalizeInventory(entry, index) {
  const title = entry.title || entry.name || entry.page || `Surface ${index + 1}`;
  const id = String(entry.id || entry.key || slug(title));
  const area = /^(auth\.|page\.(login|register)|dialog\.login)/.test(id) ? 'Authentication' :
    /^(settings\.|page\.settings)/.test(id) ? 'Settings and account security' :
    /^(vault\.|page\.vault)/.test(id) ? 'Vault and item management' :
    /^(send\.|page\.send)/.test(id) ? 'Send' :
    /^generator\./.test(id) ? 'Generator' :
    /^(emergency\.|page\.emergency)/.test(id) ? 'Emergency access' :
    /^(devices\.|page\.devices)/.test(id) ? 'Devices' :
    /^(import\.|page\.import)/.test(id) ? 'Import and export' :
    /^page\.reports/.test(id) ? 'Security reports' :
    /^(dialog\.passkey|dialog\.duo|native\.)/.test(id) ? 'External and system flows' :
    /^browser\./.test(id) ? 'Browser development harnesses' :
    /^control\./.test(id) ? 'Shared controls' : 'Shell and navigation';
  return {
    ...entry,
    id,
    title,
    group: entry.group || entry.category || entry.area || area,
    kind: entry.kind || entry.type || 'surface',
    source: sourceText(entry.source || entry.sources || entry.sourceRefs || entry.sourceFile),
    trigger: sentence(entry.trigger || entry.entry || entry.entryPoint || entry.navigation || entry.route || entry.access),
    notes: sentence(entry.notes || entry.description || entry.purpose || entry.reviewNotes || entry.prerequisites),
  };
}

function sourceLink(value) {
  const match = String(value || '').match(/(?:[A-Z]:[\\/][^|]*?[\\/])?((?:src|browser-extension)[\\/][^\s|:(]+)(?::(\d+))?/i);
  if (!match) return `<span>${escape(value || 'See page inventory')}</span>`;
  return `<a href="../../${escape(match[1].replaceAll('\\', '/'))}" target="_blank" rel="noopener">${escape(value)}</a>`;
}

const palette = { ink: '#172c45', blue: '#245ea8', pale: '#eaf1f9', line: '#c8d4e2', paper: '#ffffff', canvas: '#f3f6fa', gray: '#62748a', amber: '#9a5909', amberPale: '#fff4df' };

function textSvg(text, x, y, size = 18, color = palette.ink, weight = 400, extra = '') {
  return `<text x="${x}" y="${y}" font-family="Segoe UI,Arial,sans-serif" font-size="${size}" fill="${color}" font-weight="${weight}" ${extra}>${escape(text)}</text>`;
}
function wrap(text, width = 65) {
  const words = String(text || '').split(/\s+/); const lines = []; let current = '';
  for (const word of words) { if (current && `${current} ${word}`.length > width) { lines.push(current); current = word; } else current += `${current ? ' ' : ''}${word}`; }
  if (current) lines.push(current);
  return lines;
}
function paragraphSvg(text, x, y, width = 65, size = 16, color = palette.gray, lineHeight = 24) {
  return wrap(text, width).map((line, i) => textSvg(line, x, y + i * lineHeight, size, color)).join('');
}
function rect(x, y, width, height, fill, stroke = 'none', radius = 8, extra = '') {
  return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="${radius}" fill="${fill}" stroke="${stroke}" ${extra}/>`;
}
function fieldSvg(label, x, y, width) {
  const readOnly = /status|notice|warning|error|info|message|fingerprint|recovery code|progress|result|permission|explanation|\bcounts?\b|history|preview|no .*field|no .*selector/i.test(label);
  const choice = /selector|selection|checkbox|radio|toggle|options|picker|combo|choice/i.test(label);
  const secret = /password|secret|pin|private key|security code|token/i.test(label);
  const lines = wrap(label, Math.floor((width - 45) / 8.4));
  let body = rect(x, y, width, 68, readOnly ? palette.pale : '#fff', palette.line, 6);
  if (/^no /i.test(label)) body += textSvg('i', x + 14, y + 32, 16, palette.gray, 700);
  else if (choice) body += rect(x + 13, y + 20, 14, 14, '#fff', '#8ca8ca', 3);
  else if (secret) body += textSvg('●', x + 13, y + 33, 11, '#8ca8ca');
  else body += `<path d="M${x + 14} ${y + 21}h12m-12 6h12m-12 6h8" stroke="#8ca8ca" stroke-width="2"/>`;
  body += lines.slice(0, 3).map((line, i) => textSvg(line, x + 37, y + 26 + i * 18, 14, readOnly ? '#385572' : palette.ink, 400)).join('');
  return body;
}

// Diagram content comes directly from the source-inspected inventory controls.
function getProfile(entry) {
  const controls = list(entry.controls || entry.fields || entry.elements || entry.layout?.fields).flatMap(value => typeof value === 'string' ? value.split(';').map(part => part.trim()).filter(Boolean) : [sentence(value)]);
  const explicitActions = list(entry.actions || entry.layout?.actions).map(sentence);
  const actionPattern = /^(save|cancel|apply|continue|next|enable|disable|delete|remove|confirm|close|copy|open|set|change|done|retry|back|verify|fetch|clear|create|log\s?out|permanently|add|send code|switch|finish|use passkey|regenerate)(?:$|[ /])/i;
  const controlActions = controls.filter(control => actionPattern.test(control) && control.length < 62 && !/save destination|copy diagnostics|copy controls|close.to.tray|open.link|create.account form|delete account action|add.*choices|confirmation field/i.test(control));
  const fields = controls.filter(control => !controlActions.includes(control));
  const actions = controlActions.flatMap(value => value.split('/').map(part => part.trim())).filter(Boolean);
  return { heading: entry.title, fields, actions: explicitActions.length ? explicitActions : actions };
}

function schematicSvg(entry) {
  const profile = getProfile(entry);
  const isDialog = /dialog|modal|prompt|confirmation|flyout/i.test(entry.kind) || /dialog/i.test(entry.source);
  const isAuth = /auth|login|registration|register/i.test(entry.group);
  const isExternal = entry.kind === 'native';
  const isComponent = ['control', 'test-harness', 'unreachable', 'transition'].includes(entry.kind);
  const paneX = isDialog || isAuth ? 295 : 288;
  const paneWidth = isDialog || isAuth ? 634 : 836;
  const fieldRows = profile.fields.length ? Math.ceil(profile.fields.length / (isDialog || isAuth ? 1 : 2)) : 0;
  const formHeight = Math.max(330, fieldRows * 82 + 138);
  const height = Math.max(820, formHeight + 260);
  let body = rect(0, 0, 1200, height, palette.canvas, 'none', 0);
  body += rect(0, 0, 1200, 53, palette.amberPale, 'none', 0);
  body += textSvg('SOURCE-DERIVED SCHEMATIC', 28, 33, 17, palette.amber, 700);
  body += textSvg('Logical layout · English labels · Not a native screenshot', 1168, 33, 15, palette.amber, 400, 'text-anchor="end"');
  body += rect(24, 76, 1152, height - 184, '#fff', palette.line, 10);
  body += rect(25, 77, 1150, 49, palette.pale, 'none', 9);
  body += textSvg('V', 46, 109, 23, palette.blue, 700) + textSvg('WinVaultWarden', 82, 108, 17, palette.ink, 600);
  body += textSvg('—    □    ×', 1060, 108, 16, palette.gray);
  if (!isAuth && !isExternal && !isComponent) {
    body += rect(25, 126, 235, height - 235, '#f2f5f9', 'none', 0);
    const nav = ['My vault', 'All items', 'Favorites', 'Item types', 'Folders', 'Organizations', 'Collections', 'Archive / trash', 'Send', 'Generator', 'Security report', 'Import / export', 'Settings', 'Devices'];
    nav.forEach((label, i) => { if (i * 36 + 165 < height - 135) body += textSvg(label, 47, i * 36 + 165, i === 0 ? 17 : 15, i === 0 ? palette.ink : palette.gray, i === 0 ? 600 : 400); });
  }
  if (isExternal || isComponent) {
    const label = isExternal ? 'System-owned surface' : entry.kind === 'test-harness' ? 'Developer test harness' : entry.kind === 'unreachable' ? 'Unreachable placeholder' : entry.kind === 'transition' ? 'Navigation transition' : 'Reusable UI control';
    body += rect(44, 151, 214, 138, palette.amberPale, '#dab77b', 7, 'stroke-dasharray="5 5"');
    body += paragraphSvg(label, 58, 181, 22, 16, palette.amber, 22);
    body += paragraphSvg(isExternal ? 'The OS owns this UI. Its appearance is not reconstructed.' : entry.kind === 'transition' ? 'Returns to an existing page; no separate success page.' : 'Shown independently from application navigation.', 58, 226, 25, 13, palette.gray, 19);
  }
  if (isDialog) body += rect(25, 126, 1150, height - 235, '#172c45', 'none', 0, 'opacity="0.11"');
  const top = 154;
  body += rect(paneX, top, paneWidth, formHeight, '#fff', isDialog ? '#aabbd0' : 'none', 10);
  body += paragraphSvg(profile.heading, paneX + 24, top + 43, isDialog || isAuth ? 42 : 57, 25, palette.ink, 29);
  const columnCount = isDialog || isAuth ? 1 : 2;
  const fieldWidth = (paneWidth - 48 - (columnCount - 1) * 24) / columnCount;
  profile.fields.forEach((label, i) => { body += fieldSvg(label, paneX + 24 + (i % columnCount) * (fieldWidth + 24), top + 78 + Math.floor(i / columnCount) * 82, fieldWidth); });
  if (!profile.fields.length) body += paragraphSvg(entry.notes || 'Source-defined surface. Inspect the linked source for precise state behavior.', paneX + 24, top + 94, 60);
  let buttonX = paneX + 24;
  profile.actions.slice(0, 4).forEach((label, i) => {
    const w = Math.min(225, Math.max(86, label.length * 7 + 26));
    if (buttonX + w > paneX + paneWidth - 18) return;
    body += rect(buttonX, top + formHeight - 57, w, 35, i === 0 ? palette.blue : '#f2f5f9', i === 0 ? 'none' : palette.line, 5);
    body += textSvg(label, buttonX + w / 2, top + formHeight - 34, 14, i === 0 ? '#fff' : palette.ink, 600, 'text-anchor="middle"');
    buttonX += w + 10;
  });
  body += textSvg(`${entry.id} · ${entry.title}`, 28, height - 75, 17, palette.ink, 600);
  body += paragraphSvg(entry.source || 'Source reference: page-inventory.json', 28, height - 49, 145, 13, palette.gray, 18);
  body += textSvg('Diagram coverage does not assert runtime or integration verification.', 28, height - 14, 13, palette.amber);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="${height}" viewBox="0 0 1200 ${height}" role="img" aria-label="${escape(entry.title)} — source-derived schematic"><title>${escape(entry.title)}</title><desc>Logical review diagram based on ${escape(entry.source)}. Not a screenshot. Controls are summarized; external system screens are not reconstructed.</desc>${body}</svg>`;
}

function routeMapSvg() {
  const w = 1260, h = 734;
  let body = rect(0, 0, w, h, '#f8faff', palette.line, 10);
  body += textSvg('Application routes and ownership', 30, 45, 26, palette.ink, 700);
  body += textSvg('FrameNavigationService + MainWindow · navigation map, not runtime trace', 30, 74, 15, palette.gray);
  const node = (x, y, width, title, detail, tone = 'normal') => rect(x, y, width, 82, tone === 'auth' ? '#eaf1f9' : '#fff', palette.line, 8) + textSvg(title, x + 16, y + 30, 18, palette.ink, 600) + textSvg(detail, x + 16, y + 55, 13, palette.gray);
  const edge = (x1, y1, x2, y2, dashed = false) => `<path d="M ${x1} ${y1} H ${(x1 + x2) / 2} V ${y2} H ${x2}" fill="none" stroke="${dashed ? '#b17a27' : '#8ca8ca'}" stroke-width="2" ${dashed ? 'stroke-dasharray="6 5"' : ''} marker-end="url(#arrow)"/>`;
  body += edge(325, 173, 468, 173) + edge(325, 173, 468, 285, true);
  body += edge(620, 326, 620, 383);
  body += edge(468, 426, 306, 542) + edge(660, 426, 660, 542) + edge(850, 426, 1015, 542);
  body += node(30, 132, 295, 'Login host', 'LoginPage · password / device / SSO', 'auth');
  body += node(468, 132, 345, 'Registration', 'RegisterPage · editable / verify / uncertain', 'auth');
  body += node(468, 244, 345, 'Unlock or authenticate', 'Password · PIN · Hello · 2FA challenge', 'auth');
  body += node(468, 383, 382, 'Main window navigation', 'ContentFrame · navigation tags');
  body += node(30, 520, 335, 'Vault / Send', 'VaultPage · SendPage');
  body += node(428, 520, 340, 'Reports / import and export', 'SecurityReportPage · ImportExportPage');
  body += node(830, 520, 400, 'Account and devices', 'SettingsPage · DevicesPage');
  body += paragraphSvg('Vault filters: all, favorites, 8 item types, folders, organizations, collections, archive, trash. Send filters: all, text, file.', 46, 635, 42, 14, palette.gray, 21);
  body += paragraphSvg('Generator and history open as dialogs. Reports expand finding lists. Import/export launches native file pickers.', 444, 635, 42, 14, palette.gray, 21);
  body += paragraphSvg('Settings opens EmergencyAccessPage and security dialogs. Lock and logout reset both navigation frames.', 846, 635, 48, 14, palette.gray, 21);
  body += textSvg('External / conditional', 921, 141, 16, palette.amber, 700);
  body += paragraphSvg('SSO browser · Duo WebView · Windows Hello · security keys · passkey consent · browser extension', 921, 172, 34, 15, palette.gray, 23);
  body += rect(900, 109, 330, 178, 'none', '#dab77b', 8, 'stroke-dasharray="5 5"');
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" role="img"><title>WinVaultWarden application routes</title><defs><marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8" fill="#8ca8ca"/></marker></defs>${body}</svg>`;
}

await fs.mkdir(diagrams, { recursive: true });
const rawInventory = await readJson('page-inventory.json', []);
const inventory = flattenInventory(rawInventory).map(normalizeInventory);
if (!inventory.length) throw new Error('page-inventory.json is missing or contains no entries. Generate the source inventory first.');
const seenIds = new Set();
for (const entry of inventory) { if (seenIds.has(entry.id)) throw new Error(`Duplicate inventory ID: ${entry.id}`); seenIds.add(entry.id); }
const rawManifest = await readJson('screenshots/manifest.json', []);
const captureCoverage = await readJson('capture-coverage.json', {});
const captures = (Array.isArray(rawManifest) ? rawManifest : rawManifest.screenshots || rawManifest.captures || []).map(capture => ({ ...capture, ...(captureCoverage[capture.id] || {}) })).map((capture, i) => ({
  ...capture, id: String(capture.id || `capture-${i + 1}`), title: capture.title || capture.id || `Capture ${i + 1}`,
  group: capture.group || 'Captured surfaces', kind: 'capture', captureKind: capture.kind || 'native', source: sourceText(capture.source || capture.sources), notes: sentence(capture.notes),
  captureLabel: capture.kind === 'demo' ? 'Demo session' : 'Observed UI', coverageNote: sentence(capture.coverageNote), relatedInventoryIds: list(capture.relatedInventoryIds),
  filename: String(capture.filename || capture.file || '').replaceAll('\\', '/'),
}));
for (const capture of captures) {
  const clean = capture.filename.replace(/^screenshots\//, '');
  const full = path.resolve(output, 'screenshots', clean);
  if (!full.startsWith(path.join(output, 'screenshots') + path.sep)) throw new Error(`Screenshot path escapes the capture directory: ${capture.filename}`);
  await fs.access(full);
  capture.image = `screenshots/${clean}`;
  capture.filetype = path.extname(clean).slice(1).toUpperCase();
  const signature = (await fs.readFile(full)).subarray(0, 8);
  const isPng = signature.equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
  capture.imageEncoding = isPng ? 'Native capture losslessly encoded as PNG. Source pixels and dimensions are preserved; existing source compression is not reversed.' : 'Original native capture encoding. Check image-format-verification.json before assuming a lossless PNG source.';
  if (capture.filetype === 'PNG' && !isPng) console.warn(`Capture has a .png extension but a different image encoding: ${capture.filename}`);
}
function captureCovers(capture, entry) {
  const explicit = [capture.id, ...list(capture.inventoryId), ...list(capture.inventoryIds), ...list(capture.covers), ...list(capture.coverage)].map(String);
  const hasMapping = ['inventoryId', 'inventoryIds', 'covers', 'coverage'].some(key => Object.hasOwn(capture, key));
  return explicit.includes(entry.id) || (!hasMapping && slug(capture.title) === slug(entry.title));
}
const groupAliases = { Vault: 'Vault and item management', Settings: 'Settings and account security', Reports: 'Security reports', 'Import / Export': 'Import and export', 'Emergency Access': 'Emergency access', 'Passkeys / External': 'External and system flows' };
const gallery = captures.map(capture => {
  const matched = inventory.filter(entry => captureCovers(capture, entry));
  return { ...capture, group: matched.find(entry => entry.kind === 'page')?.group || matched[0]?.group || groupAliases[capture.group] || capture.group, key: `capture-${slug(capture.id)}`, evidence: 'Native screenshot', inventoryIds: matched.map(entry => entry.id) };
});
const coverage = [];
for (const entry of inventory) {
  const matches = gallery.filter(capture => capture.inventoryIds.includes(entry.id));
  // Keep one diagram per inventory surface, even when a representative capture exists.
  // This makes the source-defined state scope independently reviewable.
  const filename = `${slug(entry.id)}.svg`;
  await fs.writeFile(path.join(diagrams, filename), schematicSvg(entry));
  const card = { ...entry, key: `schematic-${slug(entry.id)}`, image: `diagrams/${filename}`, filetype: 'SVG', kind: 'schematic', surfaceKind: entry.kind, evidence: 'Source-derived schematic', inventoryIds: [entry.id], liveCaptureKeys: matches.map(capture => capture.key) };
  gallery.push(card);
  coverage.push({ ...entry, captureKeys: matches.map(capture => capture.key), relatedCaptureKeys: gallery.filter(capture => capture.kind === 'capture' && capture.relatedInventoryIds.includes(entry.id)).map(capture => capture.key), schematicKey: card.key });
}
await fs.writeFile(path.join(diagrams, 'navigation-map.svg'), routeMapSvg());

const overviewSpec = [
  ['01-login', 'Login and unlock', 'page.login'], ['02-register', 'Registration', 'page.register'], ['08-vault-live', 'Vault', 'page.vault'],
  ['14-send-list', 'Send', 'page.send'], ['22-security-report', 'Security reports', 'page.reports'], ['24-settings-account', 'Settings', 'page.settings'],
  ['30-emergency-access', 'Emergency access', 'page.emergency'], ['34-devices', 'Devices', 'page.devices'], ['41-import-export', 'Import and export', 'page.import-export'],
];
const overviewItems = overviewSpec.map(([id, title, pageId]) => ({ capture: captures.find(capture => capture.id === id), title, pageId })).filter(item => item.capture);
async function buildOverview() {
  if (!overviewItems.length) return null;
  const width = 3000, margin = 54, gap = 28, columns = 3;
  const tileWidth = (width - margin * 2 - gap * 2) / columns, tileHeight = 680, top = 208;
  const height = top + Math.ceil(overviewItems.length / columns) * (tileHeight + gap) + 66;
  let body = rect(0, 0, width, height, '#eef3f9', 'none', 0);
  body += textSvg('WinVaultWarden · Native page overview', margin, 81, 48, palette.ink, 700);
  body += textSvg(`${overviewItems.length} navigable pages · Original application captures from the isolated UI review session`, margin, 132, 25, palette.gray);
  body += textSvg('Visible state only. Captures retain defects and do not establish complete flow or integration verification.', margin, 174, 21, palette.gray);
  for (let i = 0; i < overviewItems.length; i++) {
    const item = overviewItems[i], x = margin + (i % columns) * (tileWidth + gap), y = top + Math.floor(i / columns) * (tileHeight + gap);
    const imageBytes = await fs.readFile(path.join(output, item.capture.image));
    const imageMime = imageBytes[0] === 0xff && imageBytes[1] === 0xd8 ? 'image/jpeg' : 'image/png';
    const data = imageBytes.toString('base64');
    body += rect(x, y, tileWidth, tileHeight, '#fff', palette.line, 10);
    body += textSvg(item.title, x + 22, y + 40, 27, palette.ink, 650);
    body += textSvg(item.pageId, x + tileWidth - 22, y + 40, 17, palette.gray, 400, 'text-anchor="end"');
    body += rect(x + 15, y + 64, tileWidth - 30, tileHeight - 118, '#e5ebf3', 'none', 5);
    body += `<image x="${x + 23}" y="${y + 72}" width="${tileWidth - 46}" height="${tileHeight - 134}" preserveAspectRatio="xMidYMid meet" xlink:href="data:${imageMime};base64,${data}"/>`;
    body += textSvg(`${item.capture.filename} · Native capture`, x + 22, y + tileHeight - 23, 18, palette.gray);
  }
  body += textSvg('Full images, source diagrams, coverage notes, and runtime findings: docs/ui-review/index.html', margin, height - 30, 21, palette.gray);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}"><title>WinVaultWarden native page contact sheet</title>${body}</svg>`;
  await fs.writeFile(path.join(output, 'overview.svg'), svg);
  const require = createRequire(import.meta.url);
  let sharp;
  try { sharp = require('sharp'); }
  catch {
    const runtimeSharp = process.env.UI_REVIEW_SHARP || (process.env.USERPROFILE ? path.join(process.env.USERPROFILE, '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/sharp') : null);
    if (runtimeSharp) { try { sharp = require(runtimeSharp); } catch {} }
  }
  if (sharp) await sharp(Buffer.from(svg)).png().toFile(path.join(output, 'overview.png'));
  else {
    try { await fs.access(path.join(output, 'overview.png')); }
    catch { console.warn('Sharp was unavailable; use overview.svg or set NODE_PATH / UI_REVIEW_SHARP to generate overview.png.'); return 'overview.svg'; }
    console.warn('Sharp was unavailable; overview.svg was refreshed and existing overview.png was retained.');
  }
  return 'overview.png';
}
const overviewImage = await buildOverview();

const preferredGroups = ['Authentication', 'Shell and navigation', 'Vault and item management', 'Send', 'Generator', 'Security reports', 'Import and export', 'Devices', 'Emergency access', 'Settings and account security', 'External and system flows', 'Shared controls', 'Browser development harnesses'];
const groups = [...new Set([...inventory.map(entry => entry.group), ...gallery.map(card => card.group)])].sort((a, b) => {
  const ai = preferredGroups.indexOf(a), bi = preferredGroups.indexOf(b);
  return (ai < 0 ? 100 : ai) - (bi < 0 ? 100 : bi) || a.localeCompare(b);
});
const groupId = group => `group-${slug(group)}`;
const covered = coverage.filter(entry => entry.captureKeys.length).length;
const pagesCaptured = coverage.filter(entry => entry.kind === 'page' && entry.captureKeys.length).length;
const demoCount = captures.filter(capture => capture.captureKind === 'demo').length;
const buildTime = new Date().toISOString();
const cardMarkup = card => `<article class="evidence-card ${card.kind}" id="${escape(card.key)}" data-group="${escape(card.group)}" data-kind="${card.kind}" data-search="${escape([card.id, card.title, card.group, card.notes, card.coverageNote, card.source, card.trigger, ...card.inventoryIds, ...list(card.relatedInventoryIds)].join(' ').toLowerCase())}">
  <button class="preview" data-open="${escape(card.key)}" aria-label="Inspect ${escape(card.title)}"><img src="${escape(card.image)}" alt="${escape(card.evidence)}: ${escape(card.title)}" loading="lazy" decoding="async"/><span class="expand-hint">Inspect image ↗</span></button>
  <div class="card-body"><div class="eyebrow"><span class="evidence-label ${card.kind}">${card.kind === 'capture' ? `<span class="status-dot"></span> Native capture · ${escape(card.captureLabel)}` : '<span class="status-diamond"></span> Source schematic'}</span><span class="surface-id">${escape(card.id)}</span></div>
  <h3><button data-open="${escape(card.key)}">${escape(card.title)}</button></h3>
  <p class="card-note">${escape(card.coverageNote || card.notes || (card.kind === 'capture' ? 'Captured from the native application during the review session.' : 'Logical source-derived diagram. This state is not asserted to have been exercised.'))}</p>
  ${card.kind === 'schematic' ? '<p class="schematic-note">Diagram only · summarized layout · not a screenshot</p>' : ''}
  <div class="card-links"><button data-open="${escape(card.key)}">Details</button><a href="${escape(card.image)}" target="_blank" rel="noopener">Open ${card.filetype}</a><a href="${escape(card.image)}" download>Download</a></div></div></article>`;

const css = `
:root{--ink:#172c45;--muted:#60718a;--blue:#245ea8;--line:#d6e0eb;--paper:#fff;--canvas:#f3f6fa;--pale:#eaf1f9;--amber:#925609;--amber-pale:#fff4df;font:15px/1.55 'Segoe UI',Arial,sans-serif;color:var(--ink);background:var(--canvas);font-synthesis:none}
*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:100px}body{margin:0}button,input,select{font:inherit}button,a,input,select{touch-action:manipulation}button,a{color:var(--blue)}button{cursor:pointer}a{text-underline-offset:3px}button{border:0;background:none}button:focus-visible,a:focus-visible,input:focus-visible,select:focus-visible{outline:3px solid #3e84d4;outline-offset:3px}h1,h2,h3,p{margin:0}h1,h2{font-family:Tahoma,'Segoe UI',sans-serif}h1{font-size:clamp(32px,3.4vw,49px);line-height:1.13;letter-spacing:-1.9px;font-weight:700;max-width:820px}h2{font-size:25px;line-height:1.3;letter-spacing:-.5px}h3{font-size:18px;line-height:1.4;font-weight:650}small{font-size:12px}.mono,.surface-id{font-family:Consolas,'Courier New',monospace}a,button{ -webkit-tap-highlight-color:transparent }.skip{position:fixed;top:-60px;left:20px;z-index:50;background:white;padding:10px 16px}.skip:focus{top:12px}
.rail{position:fixed;inset:0 auto 0 0;width:238px;background:#eef3f9;border-right:1px solid var(--line);display:flex;flex-direction:column;padding:29px 21px;z-index:20}.brand{display:flex;gap:10px;align-items:center;font-weight:700;font-size:16px}.brand-mark{height:31px;width:31px;display:grid;place-items:center;background:var(--blue);color:#fff;border-radius:7px;font-size:20px}.rail-label{margin:26px 8px 8px;color:var(--muted);font-size:11px;font-weight:700;letter-spacing:1.3px;text-transform:uppercase}.rail nav{overflow:auto;flex:1}.rail nav a{display:flex;align-items:center;justify-content:space-between;gap:5px;padding:8px 10px;margin:3px 0;color:var(--ink);text-decoration:none;border-radius:6px;font-size:13px}.rail nav a:hover{background:#dde9f6}.rail nav a:focus{background:#dde9f6}.rail nav span{color:var(--muted);font-family:Consolas,monospace;font-size:11px}.rail-foot{border-top:1px solid var(--line);padding-top:18px;font-size:12px;color:var(--muted)}.rail-foot a{display:inline-block;margin-top:8px}.main{margin-left:238px;max-width:2100px;padding:45px 44px 64px}.kicker{display:flex;gap:12px;align-items:center;text-transform:uppercase;letter-spacing:1.4px;font-size:11px;font-weight:700;color:var(--blue);margin-bottom:18px}.kicker::before{content:'';width:27px;height:3px;background:var(--blue)}.lead{margin-top:22px;max-width:860px;font-size:17px;color:var(--muted)}.top-actions{display:flex;flex-wrap:wrap;gap:9px;margin-top:24px}.button{border:1px solid var(--line);background:#fff;border-radius:6px;padding:8px 13px;display:inline-flex;align-items:center;text-decoration:none;font-size:13px;color:var(--ink)}.button.primary{background:var(--blue);color:white;border-color:var(--blue)}.stats{margin:32px 0;display:grid;grid-template-columns:repeat(4,1fr);border:1px solid var(--line);border-radius:9px;overflow:hidden;background:#fff}.stat{padding:20px 22px;border-right:1px solid var(--line)}.stat:last-child{border:0}.stat strong{font-size:33px;font-weight:650;display:block;line-height:1.1;letter-spacing:-1px}.stat span{display:block;color:var(--muted);font-size:12px;margin-top:9px}.stat small{display:block;color:var(--muted);font-size:11px;margin-top:3px}.scope-note{padding:17px 20px;background:#eaf1f9;border-left:3px solid var(--blue);font-size:13px;margin:0 0 28px;border-radius:0 5px 5px 0}.scope-note strong{font-weight:650}.section-head{display:flex;align-items:end;justify-content:space-between;gap:20px;margin:37px 0 18px}.section-head p{color:var(--muted);font-size:13px;margin-top:7px}.map-frame{background:#fff;border:1px solid var(--line);border-radius:9px;padding:12px}.map-frame img{width:100%;height:auto;display:block}.map-caption{padding:12px 4px 2px;color:var(--muted);font-size:12px;display:flex;gap:14px;justify-content:space-between}.toolbar{position:sticky;top:0;z-index:10;display:grid;grid-template-columns:minmax(160px,1fr) 190px 190px auto;gap:10px;align-items:end;margin:32px -1px 25px;padding:16px 0;background:var(--canvas);border-bottom:1px solid var(--line)}.filter-label{font-size:11px;color:var(--muted);font-weight:650;display:block;margin-bottom:5px}.toolbar input,.toolbar select{width:100%;height:40px;border:1px solid #b8c9dc;border-radius:6px;color:var(--ink);background:#fff;padding:0 12px;min-width:0}.toolbar input::placeholder{color:#728299}.toolbar button{height:40px}.result-count{font-size:12px;color:var(--muted);margin:0 0 20px}.gallery-group{margin-bottom:43px;scroll-margin-top:111px}.group-head{border-bottom:1px solid var(--line);padding:0 0 12px;margin:0 0 19px;display:flex;align-items:center;gap:13px}.group-head h2{font-family:'Segoe UI',Arial,sans-serif;font-size:21px;letter-spacing:-.3px}.group-head span{font-family:Consolas,monospace;font-size:12px;color:var(--muted);background:#e4ecf5;border-radius:4px;padding:1px 7px}.card-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:22px}.evidence-card{background:var(--paper);border:1px solid var(--line);border-radius:9px;overflow:hidden;min-width:0;scroll-margin-top:120px;box-shadow:0 2px 4px #172c4504}.evidence-card:target{outline:3px solid #5e91d4;outline-offset:4px}.preview{padding:12px;width:100%;display:block;position:relative;background:#e7edf4;aspect-ratio:1.6;border-bottom:1px solid var(--line);overflow:hidden}.preview img{display:block;width:100%;height:100%;object-fit:contain;filter:drop-shadow(0 2px 5px #172c4519)}.schematic .preview{background:#f5f0e6}.expand-hint{position:absolute;bottom:10px;right:10px;font-size:11px;padding:4px 8px;background:#fff;color:var(--blue);border:1px solid #d5dfec;border-radius:4px;opacity:0;transition:opacity .12s}.preview:hover .expand-hint,.preview:focus-visible .expand-hint{opacity:1}.card-body{padding:17px 20px 16px;border-left:3px solid var(--blue)}.schematic .card-body{border-left:3px dashed #b67c2b}.eyebrow{display:flex;justify-content:space-between;gap:12px;align-items:start;margin-bottom:10px}.evidence-label{font-size:10px;text-transform:uppercase;font-weight:700;letter-spacing:.6px;display:flex;gap:6px;align-items:center;white-space:nowrap;color:var(--blue)}.evidence-label.schematic{color:var(--amber)}.surface-id{font-size:10px;color:var(--muted);overflow-wrap:anywhere;text-align:right}.status-dot{width:7px;height:7px;border-radius:50%;display:inline-block;background:var(--blue)}.status-diamond{width:7px;height:7px;display:inline-block;border:1.5px solid #b47c2b;transform:rotate(45deg)}h3 button{text-align:left;color:var(--ink);font-weight:inherit;padding:0}.card-note{font-size:12px;line-height:1.7;color:var(--muted);margin-top:8px;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}.schematic-note{font-size:11px;color:var(--amber);margin-top:7px}.card-links{display:flex;gap:15px;border-top:1px solid #e8edf4;margin-top:15px;padding-top:11px;font-size:11px}.card-links button{padding:0;font-size:11px}.card-links a{text-decoration:none}.empty-message{padding:35px;text-align:center;border:1px dashed #b8c9dc;color:var(--muted);border-radius:8px}[hidden]{display:none!important}.coverage-section{margin-top:52px}.table-wrap{overflow:auto;border:1px solid var(--line);border-radius:8px;background:white}table{width:100%;border-collapse:collapse;font-size:12px}th{text-align:left;background:#eaf1f9;padding:12px 14px;color:var(--muted);font-size:11px;white-space:nowrap}td{padding:12px 14px;border-top:1px solid #e5ebf2;vertical-align:top}td:first-child{font-family:Consolas,monospace;font-size:11px}td:nth-child(2){min-width:220px}td:nth-child(3){min-width:180px}td:nth-child(4){min-width:200px}td p{margin-top:4px;color:var(--muted);font-size:11px}.pill{font-size:10px;padding:3px 6px;border-radius:4px;background:var(--pale);color:var(--blue);white-space:nowrap}.pill.derived{background:var(--amber-pale);color:var(--amber)}.coverage-links{margin-top:7px;display:flex;gap:12px;flex-wrap:wrap}.footer{margin-top:43px;padding-top:20px;border-top:1px solid var(--line);font-size:12px;color:var(--muted)}.footer p+p{margin-top:8px}
.viewer{position:fixed;inset:16px;width:calc(100% - 32px);height:calc(100% - 32px);max-width:none;max-height:none;margin:0;padding:0;border:1px solid #bacadc;border-radius:10px;background:#f4f7fb;color:var(--ink);box-shadow:0 24px 100px #07192e66}.viewer::backdrop{background:#0a1828b8;backdrop-filter:blur(4px)}.viewer-shell{display:grid;grid-template-rows:auto 1fr;height:100%}.viewer-header{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:16px 22px;border-bottom:1px solid var(--line);background:#fff}.viewer-header h2{font-family:'Segoe UI',Arial,sans-serif;font-size:19px;letter-spacing:-.3px}.viewer-actions{display:flex;gap:7px;flex-wrap:wrap;align-items:center}.viewer-actions .button{padding:6px 10px}.viewer-actions label{font-size:12px;color:var(--muted);display:flex;gap:5px;align-items:center}.viewer-actions input{accent-color:var(--blue);width:90px}.viewer-content{display:grid;grid-template-columns:minmax(0,1fr) 325px;min-height:0}.image-stage{overflow:auto;display:flex;align-items:flex-start;justify-content:flex-start;padding:22px;background:#e5ecf4;min-height:0;min-width:0}.image-stage img{display:block;max-width:100%;max-height:100%;object-fit:contain;margin:auto;box-shadow:0 4px 24px #172c4526}.image-stage.zoomed{display:block}.image-stage.zoomed img{max-width:none;max-height:none;margin:0}.viewer-info{padding:22px;background:#fff;overflow:auto;border-left:1px solid var(--line)}.viewer-info .evidence-label{margin-bottom:18px}.viewer-info dt{font-size:10px;letter-spacing:.8px;text-transform:uppercase;font-weight:700;color:var(--muted);margin:20px 0 5px}.viewer-info dd{font-size:13px;margin:0;overflow-wrap:anywhere}.viewer-info .caution{background:var(--amber-pale);color:var(--amber);padding:12px 14px;border-radius:5px;margin:17px 0;font-size:12px}.viewer-info .button{margin-top:10px}.viewer-help{font-size:11px;color:var(--muted);margin-top:20px}
@media(min-width:1850px){.card-grid{grid-template-columns:repeat(3,minmax(0,1fr))}}
@media(max-width:1150px){.rail{width:205px;padding:25px 13px}.main{margin-left:205px;padding:35px 25px}.toolbar{grid-template-columns:minmax(120px,1fr) 150px 150px auto}.viewer-content{grid-template-columns:minmax(0,1fr) 270px}.stats .stat{padding:17px 15px}.eyebrow{display:block}.surface-id{display:block;text-align:left;margin-top:5px}}
@media(max-width:880px){.rail{position:relative;width:auto;padding:15px 20px;display:block;border-right:0;border-bottom:1px solid var(--line)}.rail nav,.rail-label,.rail-foot{display:none}.main{margin-left:0;padding:28px 20px}.card-grid{gap:16px}.toolbar{grid-template-columns:1fr 1fr}.toolbar .search-control{grid-column:1/-1}.toolbar .button{display:none}.stats{margin:25px 0}.stat strong{font-size:27px}.viewer-content{grid-template-columns:1fr;grid-template-rows:minmax(240px,1fr) 200px}.viewer-info{border-left:0;border-top:1px solid var(--line)}.viewer-header{padding:10px 14px;align-items:flex-start}.viewer-header h2{font-size:15px}.viewer-actions input{width:70px}}
@media(max-width:580px){.card-grid{grid-template-columns:1fr}.stats{grid-template-columns:1fr 1fr}.stat:nth-child(2){border-right:0}.stat:nth-child(-n+2){border-bottom:1px solid var(--line)}h1{letter-spacing:-1.2px;font-size:35px}.lead{font-size:15px}.main{padding:26px 15px}.section-head{display:block}.section-head .button{margin-top:12px}.map-caption{display:block}.toolbar{gap:7px}.toolbar select{font-size:12px;padding:0 7px}.viewer{inset:5px;width:calc(100% - 10px);height:calc(100% - 10px)}.viewer-header{display:block}.viewer-actions{margin-top:10px}.eyebrow{display:flex}.surface-id{margin:0;text-align:right}}
@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}*{transition:none!important}}
@media print{@page{size:A4 landscape;margin:12mm}:root{font-size:11px;background:white}.rail,.toolbar,.top-actions,.card-links,.expand-hint,.skip,.viewer,.section-head>.button{display:none!important}.main{margin:0;padding:0;max-width:none}.stats{margin:18px 0}.stat{padding:12px}.stat strong{font-size:25px}.kicker{margin-bottom:9px}h1{font-size:30px;max-width:none}.lead{font-size:13px;max-width:none;margin-top:12px}.scope-note{font-size:11px;margin-bottom:16px}.map-frame{break-inside:avoid;max-width:850px}.map-frame img{max-height:390px}.map-caption{font-size:10px}.section-head{margin:24px 0 12px}.gallery-group{break-before:page;margin-bottom:15px}.card-grid{grid-template-columns:repeat(2,minmax(0,1fr));gap:13px}.evidence-card{break-inside:avoid;box-shadow:none}.preview{aspect-ratio:1.65;padding:6px}.card-body{padding:10px 12px}.card-note{-webkit-line-clamp:3;font-size:10px}.eyebrow{margin-bottom:5px}h3{font-size:13px}.group-head{margin-bottom:12px}.coverage-section{break-before:page}.table-wrap{overflow:visible}thead{display:table-header-group}table{font-size:9px}th,td{padding:7px 8px}td p,td:first-child{font-size:8px}tr{break-inside:avoid}td:nth-child(n){min-width:0}.pill{font-size:8px}a{color:var(--ink)}.footer{font-size:9px}.result-count{font-size:10px}}
`;

const data = JSON.stringify(gallery).replaceAll('<', '\\u003c');
const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light"><meta name="description" content="Offline WinVaultWarden review atlas: native UI captures, source-derived schematics, navigation map, and complete inventory coverage."><title>WinVaultWarden — UI review atlas</title><style>${css}</style></head>
<body><a href="#main" class="skip">Skip to review content</a>
<aside class="rail"><div class="brand"><span class="brand-mark">V</span><span>WinVaultWarden</span></div><p class="rail-label">UI review atlas</p><nav aria-label="Review sections"><a href="#overview">Overview <span>↗</span></a><a href="#page-overview">Native page sheet <span>PNG</span></a><a href="#routes">Route map <span>SVG</span></a>${groups.map(group => `<a href="#${groupId(group)}">${escape(group)} <span>${gallery.filter(card => card.group === group).length}</span></a>`).join('')}<a href="#coverage">Coverage matrix <span>${inventory.length}</span></a></nav><div class="rail-foot">Offline review artifact<br><a href="runtime-findings.md">Runtime findings</a> · <a href="session.md">Session</a><br><a href="../../tools/UiReviewSeed/README.md">Backend and fixture setup</a><br><a href="page-inventory.md">Source inventory</a> · <a href="README.md">Read me</a></div></aside>
<main class="main" id="main"><header id="overview"><p class="kicker">Evidence and interface inventory</p><h1>Review the whole<br>application surface.</h1><p class="lead">Native Windows captures sit alongside source-derived diagrams, so reviewers can inspect the visible application and track every documented page, dialog, and conditional state.</p><div class="top-actions"><a class="button primary" href="#gallery">Browse the atlas ↓</a><a class="button" href="overview.png" target="_blank" rel="noopener">Open 9-page overview</a><a class="button" href="runtime-findings.md">Read observed defects</a><a class="button" href="#coverage">Audit coverage</a><button class="button" id="print-button">Print / save as PDF</button><a class="button" href="page-inventory.json" download>Download inventory</a></div></header>
<div class="stats" aria-label="Evidence summary"><div class="stat"><strong>${pagesCaptured} / ${rawInventory.summary?.reachablePages || 9}</strong><span>Navigable pages captured</span><small>Representative visible page states</small></div><div class="stat"><strong>${captures.length}</strong><span>Native screenshots</span><small>Includes ${demoCount} demo-session captures</small></div><div class="stat"><strong>${inventory.length}</strong><span>Logical source diagrams</span><small>Every inventory entry diagrammed</small></div><div class="stat"><strong>${covered} / ${inventory.length}</strong><span>Entries linked to captures</span><small>Read each capture's scope note</small></div></div>
<p class="scope-note"><strong>${rawInventory.summary?.reachablePages || 9} navigable pages, expanded into ${inventory.length} review surfaces.</strong> The inventory also includes dialog variants, embedded editors, state families, reusable controls, and developer harnesses; it is not a count of independent pages. Blue marks identify native screenshots. Amber marks identify logical source diagrams with English labels and summarized layouts. A diagram does not establish that a state was opened, rendered correctly, or passed integration testing.</p>
<p class="scope-note"><strong>Capture evidence is not a test pass.</strong> “Observed UI” identifies a real native capture; manifest kind “live” does not mean an end-to-end flow was validated. Scope notes retain clipped controls, empty panels, missing field rendering, and other defects. Related inventory links are context only and do not increase capture coverage. See <a href="runtime-findings.md">runtime findings</a>, <a href="session.md">session / reproduction notes</a>, and <a href="../../tools/UiReviewSeed/README.md">backend / fixture setup</a>.</p>
${overviewImage ? `<section id="page-overview"><div class="section-head"><div><h2>See the nine native pages.</h2><p>One original capture per navigable page. The atlas below retains the full session evidence and state diagrams.</p></div><a class="button" href="${overviewImage}" download>Download page overview</a></div><div class="map-frame"><a href="${overviewImage}" target="_blank" rel="noopener" aria-label="Open native page overview at full resolution"><img src="${overviewImage}" alt="Nine-page contact sheet of actual WinVaultWarden native screenshots"></a><div class="map-caption"><span>Login · Registration · Vault · Send · Reports · Settings · Emergency access · Devices · Import and export</span><span>Original captures · no simulated screens</span></div></div></section>` : ''}
<section id="routes"><div class="section-head"><div><h2>Find the route.</h2><p>The two navigation frames separate authentication from the unlocked application.</p></div><a class="button" href="diagrams/navigation-map.svg" download>Download route map</a></div><div class="map-frame"><a href="diagrams/navigation-map.svg" target="_blank" rel="noopener" aria-label="Open full navigation map"><img src="diagrams/navigation-map.svg" alt="Source-derived navigation map showing authentication, vault, Send, reports, settings, devices, and external flows"></a><div class="map-caption"><span>Source: MainWindow.xaml and FrameNavigationService.cs</span><span>Solid lines: navigation ownership · dashed: conditional flow</span></div></div></section>
<section id="gallery" aria-label="Screenshot and schematic gallery"><div class="section-head"><div><h2>Inspect the interface.</h2><p>Open a card for the full image, source reference, entry point, and evidence details.</p></div></div><div class="toolbar"><label class="search-control"><span class="filter-label">Search atlas</span><input id="search" type="search" placeholder="Page, state, ID, source file…" autocomplete="off"></label><label><span class="filter-label">Application area</span><select id="group-filter"><option value="">All areas</option>${groups.map(group => `<option>${escape(group)}</option>`).join('')}</select></label><label><span class="filter-label">Evidence</span><select id="kind-filter"><option value="">All evidence</option><option value="capture">Native captures</option><option value="schematic">Source schematics</option></select></label><button class="button" id="reset-filters">Reset</button></div><p id="result-count" class="result-count" aria-live="polite">Showing ${gallery.length} visual records in ${groups.length} areas.</p><p class="empty-message" id="empty-message" hidden>No matching records. Clear a filter or search by source filename.</p>${groups.map(group => `<section class="gallery-group" id="${groupId(group)}" data-gallery-group="${escape(group)}"><div class="group-head"><h2>${escape(group)}</h2><span>${gallery.filter(card => card.group === group).length} records</span></div><div class="card-grid">${gallery.filter(card => card.group === group).map(cardMarkup).join('')}</div></section>`).join('')}</section>
<section class="coverage-section" id="coverage"><div class="section-head"><div><h2>Account for every surface.</h2><p>One row per inventory entry. Capture links indicate representative visible evidence, subject to each scope note. Related links are contextual, not capture coverage. All entries have logical diagrams.</p></div><a class="button" href="page-inventory.md">Read source inventory ↗</a></div><div class="table-wrap"><table><thead><tr><th scope="col">ID</th><th scope="col">Surface</th><th scope="col">Entry / state</th><th scope="col">Evidence available</th></tr></thead><tbody>${coverage.map(entry => `<tr data-coverage-search="${escape([entry.id, entry.title, entry.group, entry.trigger, entry.notes, entry.source].join(' ').toLowerCase())}"><td>${escape(entry.id)}</td><td><strong>${escape(entry.title)}</strong><p>${escape(entry.group)} · ${escape(entry.kind)}</p></td><td>${escape(entry.trigger || entry.notes || 'See linked source inventory')}<p>${sourceLink(entry.source)}</p></td><td><span class="pill ${entry.captureKeys.length ? '' : 'derived'}">${entry.captureKeys.length ? 'Capture + diagram' : 'Diagram only'}</span><div class="coverage-links">${entry.captureKeys.map((key, i) => `<a href="#${key}" data-show-record="${key}">Capture${entry.captureKeys.length > 1 ? ` ${i + 1}` : ''}</a>`).join('')}<a href="#${entry.schematicKey}" data-show-record="${entry.schematicKey}">Diagram</a></div>${entry.relatedCaptureKeys.length ? `<p>Context only: ${entry.relatedCaptureKeys.map((key, i) => `<a href="#${key}" data-show-record="${key}">Related capture ${i + 1}</a>`).join(' · ')}</p>` : ''}</td></tr>`).join('')}</tbody></table></div></section>
<footer class="footer"><p>Built ${escape(buildTime)} · Fully offline · ${pagesCaptured} navigable pages captured · ${captures.length} native captures (${demoCount} demo) · ${inventory.length} logical source diagrams.</p><p>Source inventory: <a href="page-inventory.json">JSON</a> / <a href="page-inventory.md">Markdown</a>. Capture evidence: <a href="screenshots/manifest.json">manifest</a> / <a href="capture-coverage.json">scope mappings</a>. Generated by <a href="../../scripts/ui-review/build-atlas.mjs">build-atlas.mjs</a>.</p><p><a href="runtime-findings.md">Runtime findings</a> · <a href="session.md">Session and reproduction</a> · <a href="../../tools/UiReviewSeed/README.md">Backend and fixture setup</a> · <a href="overview.png">Native page contact sheet</a></p><p>Verification records: <a href="backend-verification.json">Backend data</a> · <a href="atlas-validation.json">Offline atlas</a></p><p>Native capture losslessly encoded as PNG. <a href="image-format-verification.json">Decoded-pixel verification</a> confirms the format conversion preserves pixels and dimensions. Compression already present in capture sources is not reversed.</p><p>The atlas supports visual auditing. It is not a claim that all data states, backend protocols, accessibility paths, or external provider flows have been runtime-tested.</p></footer></main>
<dialog class="viewer" id="viewer" aria-labelledby="viewer-title"><div class="viewer-shell"><header class="viewer-header"><h2 id="viewer-title"></h2><div class="viewer-actions"><button class="button" id="previous-record" aria-label="Previous visual record">← Previous</button><button class="button" id="next-record" aria-label="Next visual record">Next →</button><button class="button" id="fit-image">Fit</button><label>Zoom <input id="zoom" type="range" min="25" max="200" step="5" value="100"><output id="zoom-label">Fit</output></label><button class="button" id="close-viewer" aria-label="Close image viewer">Close ×</button></div></header><div class="viewer-content"><div class="image-stage" id="image-stage"><img id="viewer-image" alt=""></div><aside class="viewer-info" id="viewer-info"></aside></div></div></dialog>
<script type="application/json" id="atlas-data">${data}</script>
<script>
'use strict';
const records = JSON.parse(document.getElementById('atlas-data').textContent);
const recordMap = new Map(records.map(record => [record.key, record]));
const cards = [...document.querySelectorAll('.evidence-card')];
const search = document.getElementById('search');
const groupFilter = document.getElementById('group-filter');
const kindFilter = document.getElementById('kind-filter');
const viewer = document.getElementById('viewer');
const viewerImage = document.getElementById('viewer-image');
const stage = document.getElementById('image-stage');
const zoom = document.getElementById('zoom');
let activeKey = null;
let lastFocus = null;
const encode = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
function applyFilters() {
  const query = search.value.trim().toLowerCase();
  let shown = 0;
  for (const card of cards) {
    card.hidden = !(card.dataset.search.includes(query) && (!groupFilter.value || card.dataset.group === groupFilter.value) && (!kindFilter.value || card.dataset.kind === kindFilter.value));
    if (!card.hidden) shown++;
  }
  for (const group of document.querySelectorAll('[data-gallery-group]')) group.hidden = ![...group.querySelectorAll('.evidence-card')].some(card => !card.hidden);
  document.getElementById('result-count').textContent = 'Showing ' + shown + ' of ' + records.length + ' visual records. The coverage matrix always retains the full inventory.';
  document.getElementById('empty-message').hidden = shown !== 0;
}
function resetFilters() { search.value=''; groupFilter.value=''; kindFilter.value=''; applyFilters(); }
function fitImage() { stage.classList.remove('zoomed'); viewerImage.style.width=''; viewerImage.style.height=''; zoom.value='100'; document.getElementById('zoom-label').textContent='Fit'; }
function openRecord(key) {
  const record = recordMap.get(key); if (!record) return;
  if (!viewer.open) lastFocus = document.activeElement;
  activeKey = key;
  document.getElementById('viewer-title').textContent = record.title;
  viewerImage.src=record.image;
  viewerImage.alt=record.evidence + ': ' + record.title;
  fitImage();
  const info = document.getElementById('viewer-info');
  info.innerHTML='<span class="evidence-label ' + record.kind + '">' + (record.kind==='capture'?'● Native screenshot · '+encode(record.captureLabel):'◇ Source-derived schematic') + '</span>' +
    (record.kind==='schematic'?'<p class="caution">This is a source-derived logical layout with summarized English labels. It is not a native screenshot and does not establish runtime verification.</p>':'') +
    '<dl><dt>Record ID</dt><dd class="mono">' + encode(record.id) + '</dd><dt>Area</dt><dd>' + encode(record.group) + '</dd><dt>Entry point / state</dt><dd>' + encode(record.trigger || record.route || 'See source inventory and capture notes.') + '</dd>' +
    (record.coverageNote?'<dt>Capture scope and limitations</dt><dd>'+encode(record.coverageNote)+'</dd>':'') +
    '<dt>Review notes</dt><dd>' + encode(record.notes || 'No additional notes.') + '</dd>' +
    (record.controls?'<dt>Source-defined controls</dt><dd>'+encode(record.controls)+'</dd>':'') +
    (record.states?.length?'<dt>State family</dt><dd>'+encode(record.states.join(' / '))+'</dd>':'') +
    (record.feasibility?'<dt>Prerequisite category</dt><dd>'+encode(record.feasibility)+'</dd>':'') +
    '<dt>Source</dt><dd class="mono">' + encode(record.source || 'See source inventory.') + '</dd><dt>Representative inventory evidence</dt><dd>' + encode(record.inventoryIds.length ? record.inventoryIds.join(', ') : 'Standalone capture; no exact inventory ID mapping.') + '</dd>' +
    (record.relatedInventoryIds?.length?'<dt>Related inventory — context only</dt><dd>'+encode(record.relatedInventoryIds.join(', '))+'</dd>':'') +
    (record.kind==='capture'?'<dt>Capture classification</dt><dd>'+encode(record.captureKind)+' — visible native UI, not a complete-flow test result.</dd>':'') + '</dl>' +
    (record.imageEncoding?'<p class="viewer-help">'+encode(record.imageEncoding)+'</p>':'') +
    '<a class="button" href="' + encode(record.image) + '" target="_blank" rel="noopener">Open original ' + encode(record.filetype) + ' ↗</a> <a class="button" href="' + encode(record.image) + '" download>Download image</a>' +
    '<p class="viewer-help">Esc closes the viewer. Left / right arrows navigate visible records. Fit shows the complete image; zoom inspects details.</p>';
  if (!viewer.open) viewer.showModal();
}
function moveRecord(direction) {
  const visible = cards.filter(card => !card.hidden).map(card => card.id);
  if (!visible.length) return;
  const index = visible.indexOf(activeKey);
  openRecord(visible[(index + direction + visible.length) % visible.length]);
}
document.addEventListener('click', event => {
  const opener = event.target.closest('[data-open]'); if (opener) openRecord(opener.dataset.open);
  const jump = event.target.closest('[data-show-record]'); if (jump) { event.preventDefault(); resetFilters(); const card=document.getElementById(jump.dataset.showRecord); card?.scrollIntoView({block:'start'}); openRecord(jump.dataset.showRecord); }
});
search.addEventListener('input',applyFilters); groupFilter.addEventListener('change',applyFilters); kindFilter.addEventListener('change',applyFilters);
document.getElementById('reset-filters').addEventListener('click',resetFilters);
document.getElementById('print-button').addEventListener('click',()=>window.print());
document.getElementById('close-viewer').addEventListener('click',()=>viewer.close());
viewer.addEventListener('close',()=>lastFocus?.focus());
document.getElementById('previous-record').addEventListener('click',()=>moveRecord(-1));
document.getElementById('next-record').addEventListener('click',()=>moveRecord(1));
document.getElementById('fit-image').addEventListener('click',fitImage);
zoom.addEventListener('input',()=>{ stage.classList.add('zoomed'); const factor=Number(zoom.value)/100; viewerImage.style.width=viewerImage.naturalWidth*factor+'px'; viewerImage.style.height=viewerImage.naturalHeight*factor+'px'; document.getElementById('zoom-label').textContent=zoom.value+'%'; });
viewer.addEventListener('keydown',event=>{ if (['INPUT','SELECT','TEXTAREA'].includes(event.target.tagName)) return; if(event.key==='ArrowLeft'){event.preventDefault();moveRecord(-1);} if(event.key==='ArrowRight'){event.preventDefault();moveRecord(1);} });
viewer.addEventListener('click',event=>{ if(event.target===viewer) viewer.close(); });
window.addEventListener('beforeprint',()=>{ if(viewer.open)viewer.close(); });
applyFilters();
</script></body></html>`;
await fs.writeFile(path.join(output, 'index.html'), html);
console.log(JSON.stringify({ output: path.join(output, 'index.html'), inventory: inventory.length, pagesCaptured, captures: captures.length, demoCaptures: demoCount, schematics: inventory.length, overview: overviewImage, entriesWithCapture: covered, unmappedCaptures: gallery.filter(card => card.kind === 'capture' && !card.inventoryIds.length).map(card => card.id) }, null, 2));
