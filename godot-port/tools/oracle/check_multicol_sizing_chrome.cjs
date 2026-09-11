const fs = require('node:fs');
const puppeteer = require('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:process.argv[3] || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    await page.setViewport({width:1280,height:720});
    const cases = [
      ['fractional-three', 'width:632px;column-count:3;column-gap:0'],
      ['fractional-five', 'width:632px;column-count:5;column-gap:0'],
      ['fractional-gap', 'width:632px;column-count:3;column-gap:10.5px'],
      ['normal-gap', 'width:632px;column-count:3'],
      ['normal-font', 'width:648px;font-size:24px;column-count:3'],
      ['zero-gap', 'width:600px;column-count:3;column-gap:0'],
      ['width-limits-count', 'width:632px;column-count:4;column-width:200px'],
      ['count-limits-width', 'width:632px;column-count:2;column-width:100px'],
      ['narrow-container', 'width:180px;column-count:4;column-width:200px'],
      ['auto-count', 'width:632px;column-width:200px'],
      ['zero-column-width', 'width:8px;column-width:0;column-gap:0'],
      ['subpixel-column-width', 'width:8px;column-width:.25px;column-gap:0'],
      ['subpixel-count-cap', 'width:8px;column-width:.5px;column-count:4;column-gap:0'],
      ['percentage-gap', 'width:600px;column-count:3;column-gap:10%'],
      ['relative-width', 'width:640px;font-size:20px;column-count:4;column-width:10em'],
      ['large-gap', 'width:200px;column-count:3;column-width:40px;column-gap:400px'],
      ['rtl-fixed-card', 'width:632px;column-count:3;direction:rtl', '#m>div{width:100px}'],
      ['rtl-card-margins', 'width:632px;column-count:3;direction:rtl', '#m>div{width:100px;margin-left:7px;margin-right:11px}'],
      ['rtl-card-auto-margins', 'width:632px;column-count:3;direction:rtl', '#m>div{width:100px;margin-left:auto;margin-right:auto}'],
      ['rtl-inherited', 'width:632px;column-count:3', 'body{direction:rtl}#m{margin-left:0;margin-right:auto}'],
      ['rtl-three', 'width:632px;column-count:3;direction:rtl'],
      ['rtl-fractional-three', 'width:632px;column-count:3;column-gap:0;direction:rtl'],
      ['rtl-fractional-five', 'width:632px;column-count:5;column-gap:0;direction:rtl'],
      ['rtl-gap', 'width:632px;column-count:3;column-gap:10.5px;direction:rtl'],
      ['rtl-padding', 'width:632px;column-count:3;padding:13px 17px;border:3px solid;direction:rtl'],
      ['rtl-width-cap', 'width:632px;column-count:4;column-width:200px;direction:rtl'],
      ['rtl-single', 'width:180px;column-count:4;column-width:200px;direction:rtl'],
      ['shorthand', 'width:632px;columns:4 200px'],
    ];
    const html = '<div id=m>' + Array.from({length:12},(_,i)=>`<div id=c${i}></div>`).join('') + '</div>';
    const rows=[];
    for (const [name, declarations, extra = ""] of cases) {
      const css='html,body{margin:0;padding:0;font-size:16px}#m{'+declarations+'}#m>div{height:20px;margin:0;padding:0;border:0;break-inside:avoid}'+extra;
      await page.setContent('<style>'+css+'</style>'+html);
      const bounds=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{
        const r=e.getBoundingClientRect();return {id:e.id,x:r.x,y:r.y,width:r.width,height:r.height};
      }));
      rows.push({name,html,css,bounds});
    }
    const live=[];
    const base=rows.find(row=>row.name==='width-limits-count');
    await page.setContent('<style>'+base.css+'</style>'+html);
    for (const style of ['width:1000px','width:632px','font-size:24px','font-size:16px','column-count:2','column-count:4','column-width:100px','column-width:200px','column-gap:0','direction:rtl','direction:ltr','direction:rtl;column-gap:0','direction:rtl;column-count:5;column-gap:0',null,'width:8px;column-width:0;column-count:auto;column-gap:0',null]) {
      const bounds=await page.evaluate(style=>{
        const m=document.getElementById('m');
        if(style===null)m.removeAttribute('style');else m.setAttribute('style',style);
        return Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height};});
      },style);
      live.push({style,bounds});
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(),viewport:[1280,720],rows,live},null,2));
    console.log(`${rows.length} multicol sizing cases captured`);
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
