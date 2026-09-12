const fs = require('node:fs'), path = require('node:path');
const { createRequire } = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    await page.setViewport({width:240,height:100});
    const rows = [];
    for (const wrapper of [false,true]) for (const opacity of ['1','1.0','100%','1e0','+1','2','.999']) {
      const parentZ='auto', childZ='3', cellBackground='transparent', backWidth=80;
      const name = `wrapper-${wrapper}-opacity-${opacity.replace(/[^a-z0-9]/gi,'_')}`;
      const css = 'html,body{margin:0;padding:0}table{position:relative;width:200px;table-layout:fixed;border-collapse:collapse}' +
        'td{padding:0;border:8px solid red;height:40px;position:relative}#a{z-index:'+parentZ+';background:'+cellBackground+'}#b{background:yellow}' +
        '#front{position:absolute;left:0;top:10px;width:150px;height:20px;background:blue;z-index:'+childZ+'}' +
        '#back{position:absolute;left:0;top:10px;width:'+backWidth+'px;height:20px;background:lime;z-index:1}' +
        '#wrapper{position:relative;height:40px}#a{opacity:'+opacity+'}';
      const front='<div id=front></div>';
      const html='<table id=t><tr><td id=a>'+(wrapper?'<div id=wrapper>'+front+'</div>':front)+'</td><td id=b><div id=back></div></td></tr></table>';
      await page.setContent('<style>'+css+'</style>'+html);
      const points=[[98,16],[102,16],[120,16],[98,24],[102,24],[120,24]];
      const hits=await page.evaluate(points=>points.map(([x,y])=>document.elementFromPoint(x,y)?.id||'none'),points);
      const bounds=await page.evaluate(()=>Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return {id:e.id,x:r.x,y:r.y,width:r.width,height:r.height};}));
      await page.screenshot({path:process.argv[2]+'.'+name+'.png',omitBackground:true});
      rows.push({name,html,css,points,hits,bounds});
    }
    fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows},null,2));
  } finally {await browser.close();}
})();
