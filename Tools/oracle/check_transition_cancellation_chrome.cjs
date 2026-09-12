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
    for (const [property,value] of [['transition-property','none'],['transition-property','opacity'],['transition-duration','0s']]) for (const retarget of [false,true]) {
      await page.setContent('<style>#a{width:100px;height:20px;transition:width 1s linear}</style><div id=a>x</div>');
      const values = await page.evaluate(({property,value,retarget}) => {
        const e = document.querySelector('#a');
        const width = () => e.getBoundingClientRect().width;
        width(); e.style.width = '300px'; width();
        const animation = e.getAnimations()[0];
        animation.pause(); animation.currentTime = 500; width();
        e.style.setProperty(property,value);
        if (retarget) e.style.width = '400px';
        const now = width(), count = e.getAnimations().length;
        if (count) e.getAnimations()[0].currentTime = 750;
        return [now,count,width()];
      },{property,value,retarget});
      const expected = property === 'transition-duration' && !retarget ? [200,1,250] : [retarget ? 400 : 300,0,retarget ? 400 : 300];
      rows.push({property,value,retarget,values,expected,passed:values.every((v,i)=>v===expected[i])});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
    if (rows.some(r=>!r.passed)) process.exitCode=1;
  } finally { await browser.close(); }
})();
