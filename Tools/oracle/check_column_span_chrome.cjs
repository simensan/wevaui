const fs=require('node:fs'),path=require('node:path');
const {launch} = require('./chrome_test_browser.cjs');
(async()=>{
 const output=path.resolve(process.argv[2]);fs.mkdirSync(output,{recursive:true});
 const browser=await launch({headless:true,executablePath:undefined});
 const rows=[];
 try {
  const page=await browser.newPage();await page.setViewport({width:640,height:480});
  for(const headingHeight of [0,30]) for(const margin of [0,10,-5]) for(const consecutive of [false,true])
  for(const direction of ['ltr','rtl']) for(const before of [0,2,3]) for(const after of [0,2,3]) {
   const name=`${direction}-${before}-${after}-margin${margin}-pair${consecutive}-height${headingHeight}`;
   const item=id=>`<div id="${id}" class="item"></div>`;
   const html='<div id="columns">'+Array.from({length:before},(_,i)=>item('before'+i)).join('')+'<div id="heading"></div>'+(consecutive?'<div id="heading2"></div>':'')+Array.from({length:after},(_,i)=>item('after'+i)).join('')+'</div>';
   const css=`html,body{margin:0;padding:0}#columns{width:300px;column-count:2;column-gap:20px;direction:${direction}}.item{height:20px;break-inside:avoid}#heading,#heading2{height:${headingHeight}px;column-span:all;margin:${margin}px 0}`;
   await page.setContent('<style>'+css+'</style>'+html);
   const boxes=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}}));
   fs.writeFileSync(path.join(output,name+'.html'),html);fs.writeFileSync(path.join(output,name+'.css'),css);
   rows.push({name,html,css,boxes});
  }
  fs.writeFileSync(path.join(output,'chrome.json'),JSON.stringify({browser:await browser.version(),rows},null,2));
 } finally {await browser.close();}
})();
