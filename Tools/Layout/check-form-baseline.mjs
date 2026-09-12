// Compare form baselines with independent CSS controls, using Chrome's own UA.
// Usage: node Tools/Layout/check-form-baseline.mjs [chrome.exe] [output-dir] [--no-sandbox]
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath, pathToFileURL} from 'node:url';
import puppeteer from 'puppeteer';

const args = process.argv.slice(2).filter(a => a !== '--no-sandbox');
const outputRoot = path.resolve(args[1] || os.tmpdir());
fs.mkdirSync(outputRoot, {recursive:true});
const work = fs.mkdtempSync(path.join(outputRoot, 'form-baseline-'));
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const font = pathToFileURL(path.join(repo, 'Tools/oracle/fonts/WevaMonoSans.ttf')).href;
const html = path.join(work, 'fixture.html');
fs.writeFileSync(html, `<!doctype html><style>
    @font-face{font-family:Fixture;src:url('${font}')}
    body{margin:0;font:16px Fixture}.row{white-space:nowrap}
    .field,.model{display:inline-block;box-sizing:border-box;width:120px;margin:0;
        font:16px Fixture;border:2px solid;padding:3px 4px}
    .model{display:inline-flex;align-items:center}.model>span{line-height:normal}
    .bottom{display:inline-block;overflow:hidden}
    </style><div id=font-load>Font</div>`);
const browser = await puppeteer.launch({headless:true, pipe:true,
    executablePath:args[0] || process.env.CHROME_PATH || undefined,
    userDataDir:path.join(work,'profile'),
    args:process.argv.includes('--no-sandbox') ? ['--no-sandbox'] : []});
try {
    const page = await browser.newPage();
    await page.goto(pathToFileURL(html).href);
    await page.evaluate(()=>document.fonts.ready);
    const result = await page.evaluate(()=>{
        const rows=[], failures=[];
        const textTypes=['text','search','tel','url','email','password','number',
            'date','datetime-local','month','time','week','TEXT','unexpected'];
        const bottomTypes=['checkbox','radio','range','image'];
        for (const type of [...textTypes,...bottomTypes]) {
            const bottom=bottomTypes.includes(type);
            for (const fs of [10,16,28]) for (const height of [12,34,60]) {
                for (const overflow of ['visible','hidden','auto','clip']) {
                    for (const mt of [-3,0,7]) {
                        const row=document.createElement('div'); row.className='row';
                        row.innerHTML=`<input class=field type="${type}"><span class="model ${bottom?'bottom':''}">${bottom?'':'<span>X</span>'}</span>`;
                        const field=row.firstChild, model=row.lastChild;
                        for (const el of [field,model]) Object.assign(el.style, {
                            fontSize:fs+'px',height:height+'px',paddingTop:'3px',paddingBottom:'5px',
                            marginTop:mt+'px',marginBottom:'2px'});
                        // Marks/range export their border-bottom baseline;
                        // an image input instead synthesizes at the margin edge.
                        if (bottom && type !== 'image') model.style.marginBottom='0';
                        field.style.overflow=overflow;
                        document.body.append(row);
                        const a=field.getBoundingClientRect(), b=model.getBoundingClientRect();
                        const delta=a.y-b.y;
                        const entry={type,fs,height,overflow,mt,delta}; rows.push(entry);
                        // Both use native normal font metrics; only LayoutNG's
                        // 1/64px geometry resolution is allowed here.
                        if (Math.abs(delta)>1/64) failures.push(entry);
                        row.remove();
                    }
                }
            }
        }
        return {checks:rows.length,rows,failures};
    });
    fs.writeFileSync(path.join(work,'result.json'),JSON.stringify({browser:await browser.version(),...result},null,2));
    console.log(`Form baselines: ${result.checks} checks, ${result.failures.length} failures; ${work}`);
    if(result.failures.length){console.error(JSON.stringify(result.failures.slice(0,12)));process.exitCode=1;}
} finally {await browser.close();}
