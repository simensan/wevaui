const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
 const browser = await puppeteer.launch({headless:true, executablePath:process.argv[3] || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage();await page.setViewport({width:1280,height:720});const rows=[];
  for (const mode of ['separate','collapse']) for (const border of [1,4]) for (const shape of ['bottom','all','conflict']) {
   const html='<table id=t><tbody id=g><tr id=r1><td id=a><div></div></td><td id=b><div></div></td></tr><tr id=r2><td id=c><div></div></td><td id=d><div></div></td></tr></tbody></table>';
   const css='html,body{margin:0;padding:0}table{width:200px;table-layout:fixed;border-spacing:0;border-collapse:'+mode+'}td{padding:0}td>div{height:20px}'+(shape==='bottom'?'td{border-bottom:'+border+'px solid red}':shape==='all'?'td{border:'+border+'px solid red}':'#a{border-right:'+border+'px solid red}#b{border-left:'+(border*2)+'px solid blue}');
   await page.setContent('<style>'+css+'</style>'+html);
   const bounds=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}}));
   rows.push({name:mode+'-'+shape+'-'+border,html,css,bounds});
  }
  fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),viewport:[1280,720],rows},null,2));console.log(rows.length+' text-free table cases captured');
 }finally{await browser.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
