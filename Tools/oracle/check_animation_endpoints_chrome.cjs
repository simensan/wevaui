const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');

(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await launch({headless:true,
    executablePath:undefined});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const direction of ['normal','reverse','alternate','alternate-reverse']) {
      for (const duration of [0,1]) {
        for (const count of ['0','0.25','1','1.25','2','2.25','infinite']) {
          const css = `#a{width:20px;animation:pulse ${duration}s linear 1s ${count} ${direction} both paused}`+
            '@keyframes pulse{from{width:100px}to{width:200px}}';
          await page.setContent(`<style>${css}</style><div id=a>HUD</div>`);
          const widths = await page.$eval('#a', e => {
            const animation = e.getAnimations()[0];
            const widths = [];
            for (const time of [0,10000]) {
              if (animation) animation.currentTime = time;
              widths.push(e.getBoundingClientRect().width);
            }
            return widths;
          });
          rows.push({direction,duration,count,css,times:[0,10],widths});
        }
      }
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
    console.log(`${rows.length} cases captured`);
  } finally {await browser.close();}
})().catch(e => {console.error(e);process.exitCode = 1;});
