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
    const rows=[];
    for (const reverseAgain of [false,true]) {
      await page.setContent('<style>#a{width:100px;height:20px;transition:width 1s linear}</style><div id=a>x</div>');
      const values = await page.evaluate(reverseAgain => {
        const e=document.querySelector('#a'), width=()=>e.getBoundingClientRect().width;
        const seek=time=>{const a=e.getAnimations()[0];a.pause();a.currentTime=time;return width();};
        width();e.style.width='300px';width();const out=[seek(500)];
        e.style.width='100px';width();out.push(seek(250));
        e.style.width=reverseAgain?'300px':'400px';width();out.push(seek(250),seek(reverseAgain?750:1000));
        return out;
      },reverseAgain);
      const expected=[200,150,reverseAgain?200:212.5,reverseAgain?300:400];
      rows.push({reverseAgain,values,expected,passed:values.every((v,i)=>Math.abs(v-expected[i])<.02)});
    }
    for (const delay of [-.2,.2]) {
      await page.setContent('<style>#a{width:100px;height:20px;transition:width 1s linear}</style><div id=a>x</div>');
      const values=await page.evaluate(delay=>{
        const e=document.querySelector('#a'),width=()=>e.getBoundingClientRect().width;
        const seek=time=>{const a=e.getAnimations()[0];a.pause();a.currentTime=time;return width();};
        width();e.style.width='300px';width();seek(500);
        e.style.transitionDelay=delay+'s';e.style.width='100px';width();
        return [seek(0),seek(delay<0?150:450),seek(delay<0?450:750)];
      },delay);
      const expected=[delay<0?180:200,150,100];
      rows.push({delay,values,expected,passed:values.every((v,i)=>Math.abs(v-expected[i])<.02)});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
    if(rows.some(r=>!r.passed))process.exitCode=1;
  } finally {await browser.close();}
})();
