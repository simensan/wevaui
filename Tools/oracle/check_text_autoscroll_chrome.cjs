// Native browser contract for continuous input/textarea selection scrolling.
const {launch} = require('./chrome_test_browser.cjs');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await launch({headless:true,executablePath:process.argv[2]});
 let checks=0;
 const check=(got,want)=>{assert.deepEqual(got,want);++checks;};
 const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
 try{
  const page=await browser.newPage();await page.setViewport({width:640,height:480});
  async function setup(kind,readonly=false,wrap=false){
   await page.setContent(`<style>body{margin:0}input,textarea{position:absolute;left:100px;top:100px;width:160px;height:${kind==='textarea'?96:32}px;padding:0;border:0;font:16px/24px monospace;resize:none}textarea{white-space:${wrap?'pre-wrap':'pre'};overflow:auto}</style>${kind==='textarea'?'<textarea id=f></textarea>':`<input id=f type=${kind}>`}`);
   await page.evaluate((kind,readonly)=>{const row='á😀b '.repeat(12);f.value=kind==='textarea'?Array.from({length:20},(_,i)=>`${i} ${row}`).join('\n'):'á😀b'.repeat(100);f.readOnly=readonly;window.events=[];f.oninput=()=>events.push('input');f.onchange=()=>events.push('change')},kind,readonly);
  }
  const state=()=>page.evaluate(()=>({a:f.selectionStart,b:f.selectionEnd,d:f.selectionDirection,x:f.scrollLeft,y:f.scrollTop,events:events.splice(0)}));
  async function start(x,y){await page.mouse.move(120,116);await page.mouse.down();await page.mouse.move(140,116);await page.mouse.move(x,y);}
  for(const kind of ['text','password','textarea']){
   await setup(kind);const area=kind==='textarea';
   await start(300,area?240:116);const initial=await state();check(initial.events,[]);
   await page.waitForFunction(()=>f.selectionEnd===f.value.length,{timeout:5000});
   const end=await state();check(end.a,initial.a);check(end.x>0,true);check(end.events,[]);if(area)check(end.y>0,true);
   await page.mouse.move(60,area?60:116);
   await page.waitForFunction(()=>f.selectionStart===0,{timeout:5000});
   const back=await state();check(back.b,initial.a);check(back.d,'backward');check(back.events,[]);
   await page.mouse.up();const released=await state();await wait(200);check(await state(),released);
  }
  for(const [readonly,wrap] of [[true,false],[false,true]]){
   await setup('textarea',readonly,wrap);await start(120,240);const initial=await state();
   await page.waitForFunction(()=>f.scrollTop>100,{timeout:3000});const next=await state();
   check(next.a,initial.a);check(next.b>initial.b,true);check(next.events,[]);await page.mouse.up();
  }
  await setup('text');await page.mouse.move(120,116);await page.mouse.down();await page.mouse.move(300,116);
  const unarmed=await state();await wait(250);check(await state(),unarmed);check(unarmed.x,0);await page.mouse.up();
  await setup('text');await start(140,116);await wait(200);const center=await state();check(center.x,0);
  await page.mouse.move(255,116);await page.waitForFunction(()=>f.scrollLeft>0,{timeout:3000});
  check((await state()).b>center.b,true);await page.mouse.up();
  console.log(`Chrome text autoscroll: ${checks} checks, 0 failures (${await browser.version()})`);
 }finally{await browser.close()}
})().catch(error=>{console.error(error);process.exitCode=1});
