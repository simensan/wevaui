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
    await page.setContent(`<style>#a{width:20px;animation:pulse 2s linear infinite,steady 2s linear infinite}
      @keyframes pulse{from{width:100px}to{width:200px}}
      @keyframes replacement{from{width:100px}to{width:200px}}
      @keyframes steady{from{opacity:.5}to{opacity:.5}}</style><div id=a>HUD</div>`);
    await page.$eval('#a', e => {
      window.seen = new Set(e.getAnimations());
      for (const a of window.seen) {a.pause(); a.currentTime = 500;}
    });
    const rows = [];
    for (const [names, expected] of [['steady, pulse',125], ['pulse, pulse',100],
      ['pulse',125], ['replacement',100], ['none, pulse',100]]) {
      const row = await page.$eval('#a', (e, names) => {
        e.style.animationName = names;
        const animations = e.getAnimations();
        for (const a of animations) if (!window.seen.has(a)) {
          a.pause(); a.currentTime = 0; window.seen.add(a);
        }
        return {names, width:e.getBoundingClientRect().width,
          animations:animations.map(a => ({name:a.animationName,time:a.currentTime}))};
      }, names);
      rows.push({...row,expected,passed:row.width === expected});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),
      scope:'Existing animation objects sampled at 500ms; newly created objects sampled at zero.',rows},null,2)+'\n');
    if (rows.some(r => !r.passed)) process.exitCode = 1;
  } finally {await browser.close();}
})().catch(e => {console.error(e);process.exitCode = 1;});
