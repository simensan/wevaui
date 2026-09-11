const fs = require('node:fs'), path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname,'../../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage(); await page.setViewport({width:240,height:160});
    const rows = [];
    for (const position of ['absolute','fixed']) for (const owner of ['static','relative','transform','displaced'])
    for (const rounded of [false,true]) for (const scrolled of [false,true]) for (const outerClip of [false,true]) for (const overflow of ['hidden','clip']) {
      const name = [position,owner,rounded?'round':'square',scrolled?'scroll':'still',outerClip?'nested':'single',overflow].join('-');
      const css = 'html,body{margin:0;padding:0}#outer{position:relative;width:130px;height:120px;' + (outerClip?'overflow:hidden;':'') + '}' +
        '#clip{width:100px;height:100px;overflow:'+overflow+';' + (owner==='relative'?'position:relative;':owner==='transform'?'transform:translate(0,0);':owner==='displaced'?'margin-left:300px;':'') +
        (rounded?'border-radius:20px;':'') + '}#overlay{position:' + position + ';left:80px;top:0;width:80px;height:20px;background:blue}' +
        '#ordinary{position:relative;top:40px;width:200px;height:20px;background:yellow}';
      const html = '<div id=outer><div id=clip><div id=overlay></div><div id=ordinary></div></div></div>';
      await page.setContent('<style>'+css+'</style>'+html);
      if (scrolled) await page.evaluate(() => { document.querySelector('#clip').scrollLeft=20; });
      const points = [[95,5],[120,10],[140,10],[120,50],[80,50],[10,10]];
      const capture = await page.evaluate(points => ({
        bounds:Array.from(document.querySelectorAll('[id]'),e=>{const r=e.getBoundingClientRect();return{id:e.id,x:r.x,y:r.y,width:r.width,height:r.height}}),
        hits:points.map(([x,y])=>document.elementFromPoint(x,y)?.id||'none'),
        scroll:document.querySelector('#clip').scrollLeft
      }), points);
      await page.screenshot({path:process.argv[2]+'.'+name+'.png',omitBackground:true});
      rows.push({name,css,html,points,...capture});
    }
    fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),rows},null,2));
  } finally { await browser.close(); }
})();
