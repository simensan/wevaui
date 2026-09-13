const fs = require('node:fs'), path = require('node:path');
const { createRequire } = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');
const crypto = require('node:crypto');
(async () => {
  const browser = await puppeteer.launch({headless:true, executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage(), rows = [];
    const fontFile = process.argv[3] ? fs.readFileSync(process.argv[3]) : null;
    const fontCss = fontFile ? `@font-face{font-family:SelectProbe;src:url(data:font/ttf;base64,${fontFile.toString('base64')})}` : '';
    const family = fontFile ? 'SelectProbe' : 'monospace';
    const cases = {
      short:'<option>Low</option>',
      longest:'<option selected>Low</option><option>Very long quality</option>',
      label:'<option label="Very long quality">Low</option>',
      hidden:'<option>Low</option><option hidden>Very long quality</option>',
      disabled:'<option>Low</option><option disabled>Very long quality</option>',
      group:'<optgroup label="Very long quality"><option>Low</option></optgroup>',
      group_short_title:'<optgroup label="G"><option>Low</option></optgroup>',
      group_long_option:'<optgroup label="G"><option>Very long quality</option></optgroup>',
      styled_option:'<option style="font-size:30px">Very long quality</option>',
      empty:''
    };
    for (const variant of ['', 'display:block', 'letter-spacing:2px', 'word-spacing:3px', 'min-width:300px', 'max-width:80px'])
    for (const appearance of ['none','auto']) for (const [name,options] of Object.entries(cases)) {
      const css=`select{width:auto;min-width:0;max-width:none;font:20px ${family};letter-spacing:0;word-spacing:0;padding:0;border:0;appearance:${appearance};${variant}}`;
      const html=`<select id=s>${options}</select>`;
      await page.setContent(`<style>${fontCss}${css}</style>${html}`);
      await page.evaluate(() => document.fonts.ready);
      const measured=await page.$eval('#s', e=>{
        const span=document.createElement('span');span.style.font=getComputedStyle(e).font;span.style.whiteSpace='pre';span.textContent='Very long quality';document.body.append(span);
        const font = getComputedStyle(e).font;
        const long_label_width=span.getBoundingClientRect().width;
        span.textContent='    Low'; const indented_label_width=span.getBoundingClientRect().width;
        const option=e.querySelector('option');
        return {width:e.getBoundingClientRect().width, font, long_label_width,
          indented_label_width, option_font:option ? getComputedStyle(option).font : null};
      });
      rows.push({name,appearance,variant,html,css,...measured});
    }
    fs.writeFileSync(process.argv[2],JSON.stringify({browser:await browser.version(),font_sha256:fontFile ? crypto.createHash('sha256').update(fontFile).digest('hex') : null,rows},null,2));
  } finally {await browser.close();}
})();
