const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await puppeteer.launch({headless:true,
    executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const shared of [false,true]) for (const delayed of [false,true]) {
      const property = shared ? 'width' : 'height';
      const css = `#a{width:20px;height:40px;animation:pulse 2s linear infinite,flash .25s linear ${delayed?'.5s':'0s'}}`+
        '@keyframes pulse{from{width:100px}to{width:200px}}'+
        `@keyframes flash{from{${property}:300px}to{${property}:400px}}`+
        '@keyframes steady{from{opacity:.5}to{opacity:.5}}';
      await page.setContent(`<style>${css}</style><div id=a>HUD</div>`);
      const widths = await page.$eval('#a', e => {
        const animations=e.getAnimations();
        for (const animation of animations) animation.pause();
        const values=[];
        for (const time of [250,600,1000]) {
          for (const animation of animations) animation.currentTime=time;
          values.push(e.getBoundingClientRect().width);
        }
        e.style.animationName='steady';
        values.push(e.getBoundingClientRect().width);
        return values;
      });
      const expected=[112.5,shared&&delayed?340:130,150,20];
      rows.push({shared,delayed,css,widths,expected,passed:widths.every((v,i)=>Math.abs(v-expected[i])<.02)});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
    console.log(`${rows.length} cases; ${rows.filter(r=>!r.passed).length} failures`);
    if (rows.some(r=>!r.passed)) process.exitCode=1;
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
