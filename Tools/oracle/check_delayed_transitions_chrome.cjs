const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');
(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await launch({headless:true, executablePath:undefined});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const [duration, delay] of [[0,.5],[1,.5],[1,-1],[1,-2]]) {
      await page.setContent(`<style>#a{width:100px;height:20px;transition:width ${duration}s linear ${delay}s}</style><div id=a>x</div>`);
      const values = await page.evaluate(() => {
        const e = document.querySelector('#a');
        const width = () => e.getBoundingClientRect().width;
        width(); e.style.width = '300px'; width();
        const animation = e.getAnimations()[0];
        if (!animation) return [width(), width(), width(), false];
        animation.pause();
        const values = [0,250,500].map(time => { animation.currentTime = time; return width(); });
        return [...values, true];
      });
      const expected = delay < 0 ? [300,300,300,false] : [100,100,duration === 0 ? 300 : 100,true];
      rows.push({duration,delay,values,expected,passed:values.every((v,i)=>v===expected[i])});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
    if (rows.some(r=>!r.passed)) process.exitCode=1;
  } finally { await browser.close(); }
})();
