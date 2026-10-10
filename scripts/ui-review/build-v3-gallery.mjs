import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const output = path.join(root, 'docs/ui-review/v3');
const nativeDirectory = path.join(output, 'native');
const nativeManifest = JSON.parse(await fs.readFile(path.join(nativeDirectory, 'manifest.json'), 'utf8'));
const nativeRecords = Array.isArray(nativeManifest) ? nativeManifest : nativeManifest.finalRecords || nativeManifest.records || [];
const curated = await fs.readFile(path.join(output, 'gallery-manifest.json'), 'utf8').then(JSON.parse).catch(() => null);
const finalRound = process.argv.find(argument => argument.startsWith('--round='))?.slice('--round='.length);
const lookup = selection => selection.map(item => { const record = nativeRecords.find(record => record.name === item.name); if (!record) throw new Error(`Missing native evidence ${item.name}.`); return { ...record, ...item }; });
const selectedRecords = curated ? lookup(curated.final || []) : finalRound ? nativeRecords.filter(record => record.build === finalRound || record.name?.startsWith(`${finalRound}-`)) : nativeRecords.filter(record => record.final === true);
const finalRecords = selectedRecords.filter(record => record.final !== false && (record.clientWidth || record.referenceScene === null || record.binary?.configuration === 'Release'));
const supplementalRecords = curated ? lookup(curated.supplemental || []).map(record => ({ ...record, supplemental: true })) : [];
const references = JSON.parse(await fs.readFile(path.join(output, 'reference/manifest.json'), 'utf8')).records;
const finalFiles = new Set([...finalRecords, ...supplementalRecords].map(record => record.file));
const historical = (await fs.readdir(nativeDirectory)).filter(file => /^round\d+-.*\.png$/.test(file) && !finalFiles.has(file));
const escape = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
const title = value => value.split('-').map(word => word.charAt(0).toUpperCase() + word.slice(1)).join(' ');

function describe(record) {
  const name = record.name || path.basename(record.file, '.png');
  const theme = record.theme || (/dark/.test(name) ? 'dark' : 'light');
  const width = record.referenceWidth || record.clientWidth || record.logicalWidth || (/(?:^|-)(640|760|800|900|1000|1024|1200|1280|1680)(?:-|$)/.exec(name)?.[1]) || (record.width <= 820 ? 760 : record.width <= 1100 ? 1024 : 1280);
  let scene = record.referenceScene;
  if (scene === undefined) {
    scene = /register/.test(name) ? 'register' : /unlock|locked/.test(name) ? 'locked' : /login/.test(name) && !/vault/.test(name) ? 'login'
      : /generator/.test(name) ? 'generator' : /report/.test(name) ? 'report' : /settings-account/.test(name) ? 'settings-account' : /settings-security/.test(name) ? 'settings-security' : /settings/.test(name) ? null
      : /send.*(?:editor|new)/.test(name) ? 'send-file-editor' : /send.*detail/.test(name) ? 'send-file-detail' : /send/.test(name) ? 'send'
      : /vault.*(?:editor|new)/.test(name) ? 'vault-login-editor' : /vault.*(?:login|copy|detail)/.test(name) ? 'vault-login-detail' : /vault/.test(name) ? 'vault' : null;
  }
  const ref = references.find(reference => reference.scene === scene && reference.theme === theme && Number(reference.width) === Number(width));
  return { ...record, name, theme, width, captureWidth: record.captureWidth || record.width, captureHeight: record.captureHeight || record.height, scene, reference: ref };
}

const records = [...finalRecords, ...supplementalRecords].map(describe);
const groups = [...new Set(records.map(record => record.scene || record.name))];
const pageButtons = groups.map(group => `<option value="${escape(group)}">${escape(title(group))}</option>`).join('');
const cardsFor = subset => subset.map(record => `
<article data-page="${escape(record.scene || record.name)}" data-theme="${escape(record.theme)}" data-width="${escape(record.width)}">
 <header><h2>${escape(title(record.name.replace(/^round\d+-/, '')))}</h2><span>${record.supplemental ? 'Supplemental, not final release · ' : ''}${escape(record.binary?.configuration || '')} ${escape(record.build || finalRound || 'Final')} · ${escape(record.theme)} · capture ${escape(record.captureWidth || record.width)} × ${escape(record.captureHeight || record.height)} px</span></header>
 <div class="comparison">
  <figure><figcaption><b>Native application</b><a href="native/${escape(record.file)}" target="_blank">Open full size ↗</a></figcaption><a href="native/${escape(record.file)}" target="_blank"><img src="native/${escape(record.file)}" loading="lazy" alt="Native ${escape(record.name)}"></a></figure>
  ${record.reference ? `<figure><figcaption><b>Design reference</b><a href="reference/${escape(record.reference.file)}" target="_blank">Open full size ↗</a></figcaption><a href="reference/${escape(record.reference.file)}" target="_blank"><img src="reference/${escape(record.reference.file)}" loading="lazy" alt="Prototype ${escape(record.reference.scene)}"></a></figure>` : '<div class="reference-note">This state has no matching captured reference. Compare it against the handoff specification.</div>'}
 </div>
 ${record.note ? `<p class="note">${escape(record.note)}</p>` : ''}
</article>`).join('');

const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>WinVaultWarden v3 · Native delivery review</title>
<style>
:root{font-family:"Segoe UI",system-ui,sans-serif;color:#1b1b1b;background:#f3f3f3;line-height:1.5;color-scheme:light dark;--surface:#fff;--line:#d8d8d8;--secondary:#555;--accent:#005fb8}*{box-sizing:border-box}body{margin:0}main{max-width:1840px;margin:auto;padding:40px 32px 64px}h1{font-size:28px;line-height:1.2;margin:0 0 12px;font-weight:600}p{max-width:1080px;margin:8px 0;color:var(--secondary)}a{color:var(--accent)}.intro{padding-bottom:24px;border-bottom:1px solid var(--line)}.links{display:flex;flex-wrap:wrap;gap:18px;margin-top:18px}.status{display:inline-block;border:1px solid var(--line);background:var(--surface);padding:6px 12px;border-radius:4px;margin:0 0 20px;font-size:13px}nav{display:flex;flex-wrap:wrap;align-items:end;gap:16px;padding:22px 0}label{display:flex;flex-direction:column;gap:5px;font-size:12px;color:var(--secondary)}select{font:inherit;font-size:14px;min-width:180px;height:34px;padding:0 10px;background:var(--surface);color:inherit;border:1px solid var(--line);border-radius:4px}article{background:var(--surface);border:1px solid var(--line);border-radius:8px;margin-bottom:28px;overflow:hidden}article header{display:flex;flex-wrap:wrap;align-items:baseline;justify-content:space-between;padding:16px 20px;border-bottom:1px solid var(--line);gap:8px}h2{font-size:18px;margin:0;font-weight:600}article header span{color:var(--secondary);font-size:12px}.comparison{display:grid;grid-template-columns:1fr 1fr;gap:1px;background:var(--line)}figure{margin:0;background:var(--surface);padding:14px}figcaption{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:12px;font-size:13px}figcaption a{font-size:12px}img{display:block;width:100%;height:auto;border:1px solid var(--line)}.note{padding:0 20px 14px}.reference-note{padding:28px;background:var(--surface);color:var(--secondary)}details{margin-top:32px;border-top:1px solid var(--line);padding-top:20px}summary{cursor:pointer;font-weight:600}ul{columns:3;padding-left:20px}li{padding:4px;break-inside:avoid;font-size:13px}.empty{padding:40px;background:var(--surface);border:1px solid var(--line);border-radius:8px}article[hidden]{display:none}@media(max-width:900px){main{padding:24px 16px}.comparison{grid-template-columns:1fr}ul{columns:1}}@media(prefers-color-scheme:dark){:root{color:#fff;background:#202020;--surface:#282828;--line:#444;--secondary:#c8c8c8;--accent:#60cdff}}
</style></head><body><main>
<section class="intro"><span class="status">${finalRecords.length ? `${finalRecords.length} accepted native captures · ${supplementalRecords.length} supplemental earlier captures` : 'Awaiting the verifier’s final native captures'}</span><h1>WinVaultWarden v3 · Native delivery review</h1>
<p>Compare the implemented WinUI application with the supplied v3 design. The native application uses an isolated synthetic review account; names, counts, timestamps, favicons and available server capabilities intentionally differ from the prototype’s sample data.</p>
<p>Native captures include the Windows capture boundary; reference images crop the simulated frame. Their exact pixel dimensions can differ from the requested effective-pixel window size. Compare layout and behavior, not fixture content or decorative system shadows.</p>
<div class="links"><a href="README.md">Delivery evidence</a><a href="design-differences.md">Capability decisions and differences</a><a href="review-checklist.md">Review ledger</a><a href="native/manifest.json">Native manifest</a><a href="reference/manifest.json">Reference manifest</a></div></section>
<nav aria-label="Screenshot filters"><label>Page<select id="page"><option value="all">All pages</option>${pageButtons}</select></label><label>Theme<select id="theme"><option value="all">Both themes</option><option value="light">Light</option><option value="dark">Dark</option></select></label><label>Window size<select id="width"><option value="all">All sizes</option>${[...new Set(records.map(record => record.width))].sort((a,b) => a-b).map(width => `<option value="${width}">${width} epx reference</option>`).join('')}</select></label><span id="count" aria-live="polite"></span></nav>
<section id="captures">${cardsFor(records.filter(record => !record.supplemental)) || '<div class="empty">The final gallery will be populated only after the verification owner identifies the final build and captures. Earlier diagnostic images remain available below.</div>'}</section>
${supplementalRecords.length ? `<section><h2>Supplemental earlier page evidence</h2><p>These reviewed Debug round-12 pages provide additional layout/theme coverage. They do not verify the final Release binary, subsequent shell fixes, or later service changes.</p>${cardsFor(records.filter(record => record.supplemental))}</section>` : ''}
<details><summary>Historical diagnostic captures (${historical.length})</summary><p>These images document earlier defects and are not the final delivered appearance.</p><ul>${historical.map(file => `<li><a href="native/${escape(file)}" target="_blank">${escape(file)}</a></li>`).join('')}</ul></details>
</main><script>
const controls=['page','theme','width'].map(id=>document.getElementById(id));function filter(){let visible=0;for(const card of document.querySelectorAll('article')){const match=controls.every(control=>control.value==='all'||card.dataset[control.id]===control.value);card.hidden=!match;if(match)visible++}document.getElementById('count').textContent=visible+' captures'}controls.forEach(control=>control.addEventListener('change',filter));filter();
</script></body></html>`;
await fs.writeFile(path.join(output, 'index.html'), html);
console.log(JSON.stringify({ finalRound: finalRound || null, finalCaptures: finalRecords.length, supplementalCaptures: supplementalRecords.length, historicalCaptures: historical.length, output: path.join(output, 'index.html') }));
