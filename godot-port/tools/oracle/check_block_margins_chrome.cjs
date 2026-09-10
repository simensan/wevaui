const fs = require('node:fs');
const puppeteer = require('puppeteer');
(async()=>{
 const browser=await puppeteer.launch({headless:true,executablePath:process.argv[3]||'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage();await page.setViewport({width:1280,height:720});
  const rows=[];const html='<div id=p><div id=c></div></div>';
  for(const context of ['block','flow-root','columns'])
  for(const direction of ['ltr','rtl'])
  for(const width of ['width:100px','width:400px','max-width:100px','min-width:400px'])
  for(const margin of ['margin-left:auto;margin-right:11px','margin-left:7px;margin-right:auto','margin-left:auto;margin-right:auto','margin-left:7px;margin-right:11px']) {
   const css='html,body{margin:0;padding:0}#p{width:300px;direction:'+direction+';'+(context==='columns'?'columns:2;column-gap:0':'')+'}#c{break-inside:avoid;height:20px;'+width+';'+margin+';'+(context==='flow-root'?'display:flow-root':'')+'}';
   await page.setContent('<style>'+css+'</style>'+html);
   const bounds=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}}));
   rows.push({name:[context,direction,width,margin].join('/'),html,css,bounds});
  }
  const live=[];
  for (const context of ['block','flow-root','columns']) {
   const base=rows.find(r=>r.name===context+'/ltr/width:100px/margin-left:auto;margin-right:11px');
   await page.setContent('<style>'+base.css+'</style>'+html);
   const steps=[];
   for(const [target,style] of [['c','margin-left:0;margin-right:auto'],['c','margin-left:auto;margin-right:auto'],['p','direction:rtl'],['c','margin-left:auto;margin-right:11px'],['c','width:400px'],['p','width:80px;direction:rtl'],['c','width:auto;max-width:50px;margin-left:auto;margin-right:auto'],['p','direction:ltr'],['c',null],['p',null]]) {
    const bounds=await page.evaluate(([target,style])=>{const el=document.getElementById(target);if(style===null)el.removeAttribute('style');else el.setAttribute('style',style);return Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}})},[target,style]);
    steps.push({target,style,bounds});
   }
   live.push({base:base.name,steps});
  }
  fs.writeFileSync(process.argv[2],JSON.stringify({live,browser:await browser.version(),viewport:[1280,720],rows},null,2));console.log(rows.length+' block margin cases');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
