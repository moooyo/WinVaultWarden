import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { scenarios, coverage } from './redesign-catalog.mjs';

const require = createRequire(import.meta.url);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const out = path.join(root, 'docs/ui-review/fluent-redesign');
const runtime = process.env.UI_REVIEW_NODE_MODULES || path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const {chromium} = require(path.join(runtime,'playwright'));
const sharp = require(path.join(runtime,'sharp'));
const browser = await chromium.launch({channel:'msedge',headless:true});
const context = await browser.newContext({viewport:{width:1280,height:900},deviceScaleFactor:1,reducedMotion:'reduce'});
const page = await context.newPage();
const errors=[];
page.on('pageerror',error=>errors.push(error.message));
const idSet=new Set(scenarios.map(s=>s.id));
const only = process.argv.find(arg=>arg.startsWith('--only='))?.split('=')[1]?.split(',');
const selected=only?scenarios.filter(s=>only.includes(s.id)):scenarios;
const results=[];
for (const theme of ['light','dark']) {
  for (const scene of selected) {
    const startErrors=errors.length;
    await page.goto(pathToFileURL(path.join(out,'frames',scene.id+'.html')).href+'?theme='+theme);
    await page.evaluate(()=>document.fonts.ready);
    const check=await page.evaluate(()=>{
      const bad=[];
      for(const element of document.querySelectorAll('input,select,textarea,.button,.form-field,.setting-row,.generated-value')){
        const rect=element.getBoundingClientRect();
        if(!rect.width||!rect.height)continue;
        const scroll=element.closest('.dialog-scroll,.editor-scroll,.detail-pane,.app-main,.vault-rows');
        const box=(scroll||document.body).getBoundingClientRect();
        if(rect.right>box.right+2||rect.left<box.left-2)bad.push({element:element.className||element.tagName,text:(element.textContent||element.getAttribute('aria-label')||'').trim().slice(0,90),left:Math.round(rect.left),right:Math.round(rect.right),containerRight:Math.round(box.right)});
      }
      return {width:document.documentElement.scrollWidth,height:document.documentElement.scrollHeight,clippedHorizontally:bad,actionTargets:[...document.querySelectorAll('[data-go]')].map(x=>x.dataset.go),inputs:document.querySelectorAll('input,select,textarea').length};
    });
    const invalidTargets=check.actionTargets.filter(id=>!idSet.has(id));
    delete check.actionTargets;
    await page.screenshot({path:path.join(out,'renders',scene.id+'-'+theme+'.png'),animations:'disabled'});
    results.push({id:scene.id,theme,...check,invalidTargets,errors:errors.slice(startErrors)});
  }
  console.log(`Rendered ${selected.length} ${theme} proposal frames.`);
}

if(!only){
  const overviewIds=['vault-no-selection','login-account','send-overview','generator-password','reports-overview','settings-security','devices-overview','emergency-overview','import-export-overview'];
  const escape=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const cellW=960,cellH=730,gap=28,margin=42,w=3*cellW+2*gap+2*margin,h=3*cellH+2*gap+226;
  let svg=`<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><rect width="${w}" height="${h}" fill="#edf2f8"/><text x="42" y="66" font-family="Segoe UI" font-size="38" font-weight="600" fill="#18212e">WinVaultWarden · Fluent redesign</text><text x="42" y="110" font-family="Segoe UI" font-size="21" fill="#5f6b7a">Optimized review proposal · ${scenarios.length} compositions · Light and dark · Not implemented</text>`;
  for(let i=0;i<overviewIds.length;i++){
    const scene=scenarios.find(s=>s.id===overviewIds[i]);
    const x=margin+(i%3)*(cellW+gap),y=150+Math.floor(i/3)*(cellH+gap);
    const data=await fs.readFile(path.join(out,'renders',scene.id+'-light.png'));
    svg+=`<rect x="${x}" y="${y}" width="${cellW}" height="${cellH}" rx="12" fill="#fff"/><text x="${x+20}" y="${y+34}" font-family="Segoe UI" font-size="22" font-weight="600" fill="#18212e">${escape(scene.title)}</text><image x="${x+16}" y="${y+51}" width="${cellW-32}" height="${(cellW-32)*900/1280}" href="data:image/png;base64,${data.toString('base64')}"/>`;
  }
  svg+=`<text x="42" y="${h-23}" font-family="Segoe UI" font-size="19" fill="#5f6b7a">Consistent account context · Stable sensitive fields · Progressive disclosure · Accessible controls</text></svg>`;
  await fs.writeFile(path.join(out,'overview.svg'),svg);
  await sharp(Buffer.from(svg)).png().toFile(path.join(out,'overview.png'));
}
await context.close();await browser.close();
const summary={generatedAt:new Date().toISOString(),scope:only?'selected-frame-check':'complete-frame-render',proposalOnly:true,frames:results.length,scenarios:scenarios.length,inventory:coverage.length,errors,results};
await fs.writeFile(path.join(out,only?'preview-validation.json':'render-validation.json'),JSON.stringify(summary,null,2));
const problems=results.filter(x=>x.clippedHorizontally.length||x.errors.length||x.invalidTargets.length);
console.log(JSON.stringify({frames:results.length,problems:problems.map(p=>({id:p.id,theme:p.theme,clipping:p.clippedHorizontally,invalidTargets:p.invalidTargets,errors:p.errors}))},null,2));
if(problems.length)process.exitCode=1;
