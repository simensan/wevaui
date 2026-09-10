const fs=require('node:fs'),path=require('node:path');
const {createRequire}=require('node:module');
const puppeteer=createRequire(path.resolve(__dirname,'../../../Tools/Layout/package.json'))('puppeteer');
(async()=>{
 const browser=await puppeteer.launch({headless:true,executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage(),rows=[];
  await page.setViewport({width:640,height:480});
  for(const nested of [false,true]) for(const manual of [false,true]) for(const action of ['outside','parent','keyboard','blank','outside-drag','leave-menu','enter-menu']) {
   const html='<button id=outside>Outside</button><div id=a popover>Menu'+(nested?'<div id=b popover>Child</div>':'')+'</div><div id=m popover=manual>Pinned</div>';
   const css='body{margin:0}#outside{position:absolute;left:10px;top:10px;width:100px;height:30px}[popover]{margin:0;position:fixed;width:100px;height:60px;padding:0;border:0;inset:auto}#a{left:200px;top:100px}#b{left:350px;top:100px}#m{left:500px;top:100px}';
   await page.setContent('<style>'+css+'</style>'+html);
   await page.evaluate(({nested,manual})=>{document.getElementById('a').showPopover();if(nested)document.getElementById('b').showPopover();if(manual)document.getElementById('m').showPopover();},{nested,manual});
   const down=action==='parent'||action==='leave-menu'?[220,120]:action==='blank'?[30,350]:[30,20];
   const up=action==='outside-drag'||action==='leave-menu'?[30,350]:action==='enter-menu'?[220,120]:down;
   if(action==='keyboard'){await page.focus('#outside');await page.keyboard.press('Enter');}
   else {await page.mouse.move(...down);await page.mouse.down();await page.mouse.move(...up);await page.mouse.up();}
   const open=await page.evaluate(()=>Array.from(document.querySelectorAll(':popover-open'),e=>e.id));
   rows.push({nested,manual,action,html,css,down,up,open});
  }
  fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows},null,2));
 }finally{await browser.close();}
})();
