import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const icons = JSON.parse(fs.readFileSync(path.join(root, 'docs/ui-review/fluent-redesign/assets/icons.json'), 'utf8'));
let fieldSequence = 0;
export const e = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
export const icon = (name, cls='') => `<span class="icon ${e(cls)}" aria-hidden="true">${icons[name] || icons.info}</span>`;
export const button = (text, style='', name='', action='', attrs='') => `<button ${/\btype\s*=/.test(attrs)?'':'type="button"'} class="button ${e(style)}" ${action?`data-go="${e(action)}"`:''} ${attrs}>${name?icon(name):''}${e(text)}</button>`;
export const ib = (name, text, attrs='') => `<button type="button" class="icon-button" aria-label="${e(text)}" title="${e(text)}" ${attrs}>${icon(name)}</button>`;
export const pill = (text, tone='') => `<span class="pill ${e(tone)}">${e(text)}</span>`;
export const note = (title, body, tone='info') => `<div class="notice ${e(tone)}">${icon(tone==='danger'||tone==='warning'?'warning':'info')}<div><strong>${e(title)}</strong><p>${e(body)}</p></div></div>`;
export function label(name, value='', type='text', help='', attrs='') {
  const id=`review-field-${++fieldSequence}`;
  return `<label class="form-field"><span>${e(name)}</span><input type="${e(type)}" value="${e(value)}" ${help?`aria-describedby="${id}-help"`:''} ${attrs}>${help?`<small id="${id}-help">${e(help)}</small>`:''}</label>`;
}
export function select(name, options, help='', attrs='') {
  const id=`review-field-${++fieldSequence}`;
  return `<label class="form-field"><span>${e(name)}</span><select ${help?`aria-describedby="${id}-help"`:''} ${attrs}>${options.map(option=>typeof option==='string'?`<option>${e(option)}</option>`:`<option value="${e(option.value)}" ${option.selected?'selected':''} ${option.disabled?'disabled':''}>${e(option.label)}</option>`).join('')}</select>${help?`<small id="${id}-help">${e(help)}</small>`:''}</label>`;
}
export const toggle = (title, desc, on=false, disabled=false) => `<div class="setting-row"><div><strong>${e(title)}</strong><p>${e(desc)}</p></div><label class="switch"><input aria-label="${e(title)}" type="checkbox" ${on?'checked':''} ${disabled?'disabled':''}><span></span></label></div>`;
export const setting = (title, desc, control) => `<div class="setting-row"><div><strong>${e(title)}</strong><p>${e(desc)}</p></div>${control}</div>`;
export const section = (title, body, aside='') => `<section class="section"><div class="section-title"><h3>${e(title)}</h3>${aside}</div>${body}</section>`;
export const header = (title, sub, action='') => `<header class="page-heading"><div><h1>${e(title)}</h1>${sub?`<p>${e(sub)}</p>`:''}</div><div class="page-actions">${action}</div></header>`;
export const titlebar = () => `<header class="titlebar"><span class="app-mark">${icon('shield')}</span><strong>WinVaultWarden</strong><span class="titlebar-spacer"></span><span class="window-control" aria-hidden="true">−</span><span class="window-control" aria-hidden="true">□</span><span class="window-control" aria-hidden="true">×</span></header>`;
export const ui = { e, icon, button, ib, pill, note, label, select, toggle, setting, section, header, titlebar };
export function resetFieldSequence(){fieldSequence=0;}
