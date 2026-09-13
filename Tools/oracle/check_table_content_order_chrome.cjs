const fs = require('node:fs'), path = require('node:path');
const { createRequire } = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    await page.setViewport({width: 240, height: 100});
    const rows = [];
    for (const cellPosition of ['static', 'relative']) for (const position of ['static', 'relative', 'absolute']) {
      for (const firstBackground of ['transparent', 'cyan']) for (const secondBackground of ['transparent', 'yellow']) for (const effect of ['plain', 'clip', 'translate', 'clip-translate']) {
        const css = 'html,body{margin:0;padding:0}table{width:200px;table-layout:fixed;border-collapse:collapse}' +
          'td{padding:0;border:8px solid red;height:40px}#a{position:' + cellPosition + ';background:' + firstBackground + '}' +
          '#b{background:' + secondBackground + '}#overlay{position:' + position + ';width:140px;height:20px;background:blue;' +
          (position === 'absolute' ? 'left:0;top:10px;' : '') + '}' +
          '#clip{width:110px;height:70px;' + (effect.includes('clip') ? 'overflow:hidden;' : '') +
          (effect.includes('translate') ? 'transform:translate(10px,0);' : '') + '}';
        const html = '<div id=clip><table id=t><tr><td id=a><div id=overlay></div></td><td id=b></td></tr></table></div>';
        await page.setContent('<style>' + css + '</style>' + html);
        const bounds = await page.evaluate(() => Array.from(document.querySelectorAll('[id]'), e => {
          const r = e.getBoundingClientRect(); return {id:e.id,x:r.x,y:r.y,width:r.width,height:r.height};
        }));
        const name = cellPosition + '-' + position + '-' + firstBackground + '-' + secondBackground + '-' + effect;
        const points = [[98,6],[102,6],[120,6],[98,24],[102,24],[120,24]];
        const hits = await page.evaluate(points => points.map(([x,y]) => document.elementFromPoint(x,y)?.id || 'none'), points);
        await page.screenshot({path: process.argv[2] + '.' + name + '.png', omitBackground:true});
        rows.push({name,html,css,bounds,points,hits});
      }
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(),rows},null,2));
  } finally { await browser.close(); }
})();
