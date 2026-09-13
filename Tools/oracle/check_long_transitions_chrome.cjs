const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const count of [32, 33, 64]) for (const earlierMatch of [false, true]) {
      const names = Array(count).fill('opacity');
      if (earlierMatch) names[0] = 'width';
      names.push('width');
      await page.setContent(`<style>#a{width:100px;height:20px;transition-property:${names.join(',')};transition-duration:1s,2s;transition-timing-function:linear}</style><div id=a>x</div>`);
      const width = await page.evaluate(() => {
        const e = document.querySelector('#a');
        e.getBoundingClientRect();
        e.style.width = '300px';
        e.getBoundingClientRect();
        const animation = e.getAnimations()[0];
        if (!animation) throw new Error('Missing transition');
        animation.pause(); animation.currentTime = 500;
        return e.getBoundingClientRect().width;
      });
      const expected = count % 2 ? 150 : 200;
      rows.push({count, earlierMatch, width, expected, passed:Math.abs(width-expected)<0.02});
    }
    for (const count of [8, 16]) {
      const names = [...Array(count).fill('none'), 'pulse'].join(',');
      await page.setContent(`<style>#a{width:20px;animation-name:${names};animation-duration:2s;animation-timing-function:linear;animation-fill-mode:forwards}@keyframes pulse{from{width:100px}to{width:300px}}</style><div id=a>x</div>`);
      const values = await page.evaluate(() => {
        const e = document.querySelector('#a');
        const width = () => e.getBoundingClientRect().width;
        width();
        const animation = e.getAnimations()[0];
        animation.pause(); animation.currentTime = 500;
        const values = [width()];
        e.style.animationPlayState = 'paused'; values.push(width());
        e.style.animationName = 'pulse'; values.push(width());
        e.getAnimations()[0].currentTime = 1000; values.push(width());
        e.style.animationName = 'none'; values.push(width());
        return values;
      });
      const expected = [150,150,150,200,20];
      rows.push({animationPrefixCount:count, values, expected, passed:values.every((v,i)=>Math.abs(v-expected[i])<0.02)});
    }
    fs.writeFileSync(output, JSON.stringify({browser:await browser.version(), rows},null,2)+'\n');
    if (rows.some(r=>!r.passed)) process.exitCode=1;
  } finally { await browser.close(); }
})();
