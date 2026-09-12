const fs = require('fs');
const path = require('path');
const { createRequire } = require('module');
const puppeteer = createRequire(path.resolve('Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:process.env.PUPPETEER_EXECUTABLE_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page=await browser.newPage();
    await page.setViewport({width:500,height:400});
    const rows=[];
    for(const mode of ['horizontal-tb','vertical-rl','vertical-lr']) for(const direction of ['ltr','rtl']) {
      await page.setContent(`<input id=r type=range min=0 max=100 value=50 style="position:absolute;left:40px;top:40px;width:${mode==='horizontal-tb'?200:30}px;height:${mode==='horizontal-tb'?30:200}px;writing-mode:${mode};direction:${direction}">`);
      for(const key of ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End','PageUp','PageDown']) {
        await page.$eval('#r',e=>{e.value='50';e.focus()});
        await page.keyboard.press(key);
        rows.push({mode,direction,key,value:await page.$eval('#r',e=>Number(e.value))});
      }
      for(const fraction of [0,0.5,1]) {
        const point=await page.$eval('#r',(e,{fraction,mode})=>{e.value='50';const r=e.getBoundingClientRect();return mode==='horizontal-tb'?{x:r.x+Math.max(1,Math.min(r.width-1,r.width*fraction)),y:r.y+r.height/2}:{x:r.x+r.width/2,y:r.y+Math.max(1,Math.min(r.height-1,r.height*fraction))}},{fraction,mode});
        await page.mouse.click(point.x,point.y);
        rows.push({mode,direction,fraction,value:await page.$eval('#r',e=>Number(e.value))});
      }
    }
    const result={browser:await browser.version(),rows};
    if(process.argv[2])fs.writeFileSync(process.argv[2],JSON.stringify(result,null,2));
    console.log(JSON.stringify(result));
  } finally {await browser.close();}
})();
