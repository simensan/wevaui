import fs from 'node:fs';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
const require=createRequire(path.resolve('Tools/Layout/package.json'));
const puppeteer=require('puppeteer');
const dir=path.resolve(process.argv[2]);
if (!process.argv[2]) throw new Error('Pass the prepared Godot project directory');
const native=JSON.parse(fs.readFileSync(path.join(dir,'native.json')));
const initial=native.states[0].model;
let html=fs.readFileSync(path.join(dir,'ui/camp.html'),'utf8');
const css=fs.readFileSync(path.join(dir,'ui/camp.css'),'utf8');
const template=html.match(/<template[^>]*>([\s\S]*?)<\/template>/)[1];
const escape=v=>String(v).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;');
const bind=(text,model)=>text.replace(/\{\{\s*([\w.]+)\s*\}\}/g,(_,key)=>escape(key.split('.').reduce((o,k)=>o[k],model))).replace(/disabled="false"/g,'').replace(/disabled="true"/g,'disabled');
const rows=model=>model.Items.map(item=>bind(template,{...model,item})).join('');
html=bind(html.replace(/<template[^>]*>[\s\S]*?<\/template>/,rows(initial)),initial);
const font=fs.readFileSync(path.join(dir,'chrome-font.ttf')).toString('base64');
const fontCss=`@font-face{font-family:GameFont;src:url(data:font/ttf;base64,${font});font-weight:400}html,body,button,input{font-family:GameFont,sans-serif}`;
const pageHtml=`<!doctype html><html><head><meta charset="utf-8"><base href="${pathToFileURL(path.join(dir,'ui')).href}/"><style>${css}</style><style>${fontCss}</style></head><body>${html}</body></html>`;
const browser=await puppeteer.launch({headless:true,executablePath:process.env.PUPPETEER_EXECUTABLE_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe',args:['--allow-file-access-from-files']});
try {
 const page=await browser.newPage();await page.setViewport({width:1280,height:720});
 fs.writeFileSync(path.join(dir,'chrome-page.html'),pageHtml);await page.goto(pathToFileURL(path.join(dir,'chrome-page.html')).href,{waitUntil:'networkidle0'});await page.evaluate(()=>document.fonts.ready);
 await page.evaluate(()=>{
  const $=id=>document.getElementById(id);
  $('settings-button').onclick=()=>{$('settings').showModal();$('player-name').focus();};
  $('close-settings').onclick=()=>{$('settings').close();$('settings-button').focus();};
  $('player-name').oninput=()=>{$('player-label').textContent=$('player-name').value;};
 });
 const states=[];
 for(const expected of native.states){
  const name=expected.name;
  if(['opened','scrolled-open'].includes(name))await page.click('#settings-button');
  if(name==='tab')await page.keyboard.press('Tab');
  if(name==='typed'){await page.focus('#player-name');await page.keyboard.down('Control');await page.keyboard.press('a');await page.keyboard.up('Control');await page.keyboard.type('R');}
  if(['closed-again','resize-closed'].includes(name))await page.click('#close-settings');
  if(name==='scrolled')await page.evaluate(markup=>{document.querySelector('#inventory').innerHTML=markup;document.querySelector('#stack-count').textContent='16 stacks';document.querySelector('#inventory').scrollTop=120;},rows(expected.model));
  if(name.startsWith('resize-')&&name!=='resize-closed')await page.setViewport({width:expected.size[0],height:expected.size[1]});
  await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
  const state=await page.evaluate(selectors=>{
   const rects={};for(const selector of selectors){const r=document.querySelector(selector)?.getBoundingClientRect();rects[selector]=r?[r.x,r.y,r.width,r.height]:null;}
   const inv=document.querySelector('#inventory');return {rects,focus:document.activeElement.id,scroll:[inv.scrollLeft,inv.scrollTop],value:document.querySelector('#player-name').value,fontLoaded:document.fonts.check('14px GameFont')};
  },native.selectors);
  states.push({name,...state});await page.screenshot({path:path.join(dir,'chrome-'+name+'.png')});
 }
 fs.writeFileSync(path.join(dir,'chrome.json'),JSON.stringify({browser:await browser.version(),normalization:'same Godot font bytes; no UA overlay; native line heights',states},null,2));
 console.log(states.map(s=>[s.name,s.focus,s.value,s.scroll,s.rects['#settings']]));
} finally {await browser.close();}
