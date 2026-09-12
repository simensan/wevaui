const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const target of ['#a', '#panel']) {
      await page.setContent('<style>#a{width:100px;height:20px;transition:width 1s linear}</style><div id=panel><div id=a>Panel</div></div>');
      const values = await page.evaluate(target => {
        const e = document.querySelector('#a'), panel = document.querySelector(target);
        const width = () => e.getBoundingClientRect().width;
        const sample = () => {
          const animation = e.getAnimations()[0];
          if (!animation) throw new Error('Expected a visible transition');
          animation.pause(); animation.currentTime = 500;
          return width();
        };
        width(); e.style.width = '300px'; width();
        const out = [sample()];
        panel.style.display = 'none'; getComputedStyle(e).width;
        out.push(e.getAnimations().length);
        e.style.width = '400px'; getComputedStyle(e).width;
        out.push(e.getAnimations().length);
        panel.style.display = 'block'; e.style.width = '500px';
        out.push(width(), e.getAnimations().length);
        e.style.width = '600px'; width();
        out.push(sample());
        return out;
      }, target);
      const expected = [200, 0, 0, 500, 0, 550];
      rows.push({target, values, expected, passed:values.every((v,i)=>Math.abs(v-expected[i])<0.02)});
    }
    fs.writeFileSync(output, JSON.stringify({browser:await browser.version(), rows},null,2)+'\n');
    if (rows.some(r=>!r.passed)) process.exitCode=1;
  } finally { await browser.close(); }
})();
