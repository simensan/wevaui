const fs=require('node:fs'),path=require('node:path');
const {createRequire}=require('node:module');
const puppeteer=createRequire(path.resolve(__dirname,'../../Tools/Layout/package.json'))('puppeteer');
(async()=>{
 const browser=await puppeteer.launch({headless:true,executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try {
  const page=await browser.newPage();await page.setViewport({width:400,height:300});const rows=[];
  for(const collapse of ['collapse','separate'])for(const side of ['top','bottom'])for(const padding of [0,20]) {
   const html='<table id=t><caption id=cap><i></i><i></i><i></i></caption><tbody id=g><tr id=r><td id=a><div></div></td><td id=b><div></div></td></tr><tr id=r2><td id=c><div></div></td><td><div></div></td></tr></tbody><tfoot id=f><tr id=r3><td id=d><div></div></td><td><div></div></td></tr></tfoot></table><div id=after></div>';
   const css='html,body{margin:0;padding:0}table{width:200px;table-layout:fixed;border-collapse:'+collapse+';border-spacing:2px;padding:'+padding+'px;border:8px solid blue}caption{caption-side:'+side+';font-size:0;line-height:0;text-align:left}caption i{display:inline-block;width:65px;height:10px}td{padding:0;border:4px solid red}td>div{height:20px}#after{height:10px}';
   await page.setContent('<style>'+css+'</style>'+html);
   const bounds=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}}));
   rows.push({name:[collapse,side,padding].join('-'),html,css,bounds});
  }
  fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows},null,2));
 }finally{await browser.close()}
})();
