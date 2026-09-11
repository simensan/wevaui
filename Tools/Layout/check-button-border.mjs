// Independent Chrome paint/UA controls; no engine stylesheet is injected.
// Usage: node Tools/Layout/check-button-border.mjs [chrome.exe] [output-dir] [--no-sandbox]
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import puppeteer from 'puppeteer';

const args=process.argv.slice(2).filter(a=>a!=='--no-sandbox');
const outputRoot=path.resolve(args[1] || os.tmpdir());
fs.mkdirSync(outputRoot,{recursive:true});
const work=fs.mkdtempSync(path.join(outputRoot,'button-border-'));
const samples=[
 ['#000',[168,168,168],[84,84,84]], ['#101010',[184,184,184],[100,100,100]],
 ['#202020',[200,200,200],[116,116,116]], ['#505050',[164,164,164],[0,0,0]],
 ['#767676',[202,202,202],[33,33,33]], ['#808080',[212,212,212],[44,44,44]],
 ['#ebebeb',[255,255,255],[151,151,151]], ['#fff',[255,255,255],[171,171,171]],
 ['#f00',[255,0,0],[171,0,0]], ['#00f',[0,0,255],[0,0,171]],
 ['#008000',[0,212,0],[0,44,0]],
];
const markup=`<!doctype html><style>html{color-scheme:light}body{margin:0;background:white;font:italic bold 30px/60px serif}
 #default,#styled,#author,#disabled{font:14px/16px sans-serif}#styled{padding:8px 16px;background:#4f46e5;color:white;border-radius:8px}
 #author{border:3px solid blue}.samples{display:grid;grid-template-columns:repeat(4,120px);gap:10px;padding:10px}
 .sample{box-sizing:border-box;width:100px;height:60px;border:8px outset}
 .sides{border-style:solid;border-color:red lime blue yellow;border-width:4px 7px 10px 13px}
 </style><button id=default>Default</button><button id=styled>Start</button>
 <button id=author>Author</button><button id=disabled disabled>Disabled</button>
 <button id=small>Small</button><button id=inherit style="font:inherit">Inherit</button><div class=samples>
 ${samples.flatMap(([color],i)=>['outset','inset'].map(style=>
 `<div class=sample id=c${i}-${style} style="border-color:${color};border-style:${style}"></div>`)).join('')}
 <div class="sample sides" id=square></div><div class="sample sides" id=rounded style="border-radius:20px"></div></div>`;
fs.writeFileSync(path.join(work,'fixture.html'),markup);
const browser=await puppeteer.launch({headless:true,pipe:true,userDataDir:path.join(work,'profile'),
 executablePath:args[0] || process.env.CHROME_PATH || undefined,
 args:process.argv.includes('--no-sandbox') ? ['--no-sandbox'] : []});
const rows=[];
const check=(label,actual,expected)=>rows.push({label,actual,expected,pass:JSON.stringify(actual)===JSON.stringify(expected)});
try {
 const page=await browser.newPage();await page.setViewport({width:600,height:800});await page.setContent(markup);
 const rects=await page.evaluate(()=>Object.fromEntries(Array.from(document.querySelectorAll('[id]'),e=>{
  const r=e.getBoundingClientRect();return [e.id,[r.x,r.y,r.width,r.height]];
 })));
 const png=await page.screenshot({path:path.join(work,'chrome.png')});
 const pixels=await page.evaluate(async({encoded,rects})=>{
  const image=new Image();image.src='data:image/png;base64,'+encoded;await image.decode();
  const canvas=document.createElement('canvas');canvas.width=image.width;canvas.height=image.height;
  const context=canvas.getContext('2d');context.drawImage(image,0,0);
  const sample=(x,y)=>Array.from(context.getImageData(Math.floor(x),Math.floor(y),1,1).data).slice(0,3);
  return Object.fromEntries(Object.entries(rects).map(([id,[x,y,w,h]])=>
   [id,[sample(x+w/2,y+2),sample(x+w-4,y+h/2),sample(x+w/2,y+h-4),sample(x+3,y+h/2)]]));
 },{encoded:Buffer.from(png).toString('base64'),rects});
 for(let i=0;i<samples.length;++i)for(const style of ['outset','inset']){
  const [,light,dark]=samples[i],leading=style==='outset'?light:dark,trailing=style==='outset'?dark:light;
  for(let side=0;side<4;++side)check(`color ${i}/${style}/${side}`,pixels[`c${i}-${style}`][side],
   side===0 || side===3 ? leading : trailing);
 }
 for(const id of ['square','rounded'])for(let side=0;side<4;++side)
  check(`${id}/${side}`,pixels[id][side],[[255,0,0],[0,255,0],[0,0,255],[255,255,0]][side]);
 const read=async id=>page.$eval('#'+id,e=>{
  const s=getComputedStyle(e);return {height:e.getBoundingClientRect().height,
   width:s.borderTopWidth,style:s.borderTopStyle,color:s.borderTopColor,padding:s.padding};
 });
 const normal=await read('default');
 check('default height with 16px line',normal.height,22);
 check('default border width',normal.width,'2px');check('default style',normal.style,'outset');
 check('default border color',normal.color,'rgb(0, 0, 0)');check('default padding',normal.padding,'1px 6px');
 check('author padding height',(await read('styled')).height,36);
 const fonts=await page.evaluate(()=>['small','inherit'].map(id=>{
  const s=getComputedStyle(document.getElementById(id));return [s.fontSize,s.fontWeight,s.fontStyle,s.lineHeight];
 }));
 check('small control font ignores inherited author font',fonts[0],['13.3333px','400','normal','normal']);
 check('explicit font inheritance wins',fonts[1],['30px','700','italic','60px']);
 for(const id of ['default','author','disabled']){
  const [x,y,w,h]=rects[id];await page.mouse.move(x+w/2,y+h/2);await page.mouse.down();
  check(id+' pressed style',(await read(id)).style,id==='default'?'inset':id==='author'?'solid':'outset');
  await page.mouse.up();
 }
 const failures=rows.filter(r=>!r.pass);
 fs.writeFileSync(path.join(work,'result.json'),JSON.stringify({browser:await browser.version(),rows},null,2));
 console.log(`Chrome button borders: ${rows.length} checks, ${failures.length} failures; ${work}`);
 for(const row of failures)console.error(row);
 process.exitCode=failures.length?1:0;
}finally{await browser.close();}
