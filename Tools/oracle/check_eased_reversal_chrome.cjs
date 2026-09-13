const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const output=process.argv[2];
  if(!output||fs.existsSync(output))throw new Error('Expected new output JSON path');
  const browser=await puppeteer.launch({headless:true,executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page=await browser.newPage(),rows=[];
    for(const easing of ['ease','ease-in-out','cubic-bezier(.3,-.8,.7,1.8)','steps(4,end)']) for(const forwardTime of [200,700]) {
      await page.setContent(`<style>#a{width:100px;height:20px;transition:width 1s ${easing}}</style><div id=a>x</div>`);
      const values=await page.evaluate(forwardTime=>{
        const e=document.querySelector('#a'),width=()=>e.getBoundingClientRect().width;
        const seek=time=>{const a=e.getAnimations()[0];if(!a)return width();a.pause();a.currentTime=time;return width();};
        width();e.style.width='300px';width();const out=[seek(forwardTime)];
        e.style.width='100px';width();out.push(seek(50));
        e.style.width='300px';width();out.push(seek(100),seek(1100));return out;
      },forwardTime);
      rows.push({easing,forwardTime,values});
    }
    fs.writeFileSync(output,JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
  } finally {await browser.close();}
})();
