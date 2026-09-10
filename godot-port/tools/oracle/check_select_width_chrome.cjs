const fs = require('node:fs'), path = require('node:path');
const { createRequire } = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');
(async () => {
  const browser = await puppeteer.launch({headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage(), rows = [];
    for (const multiple of [false, true]) {
      for (const css of ['width:90px', 'width:50%', 'width:90px;min-width:110px', 'width:90px;max-width:75px']) {
        await page.setContent(`<div style="width:320px"><select style="box-sizing:border-box;${css}" ${multiple ? 'multiple size=3' : ''}><option>Low</option><option>Medium</option></select></div>`);
        rows.push({multiple, css, width: await page.$eval('select', e => e.getBoundingClientRect().width)});
      }
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), scope: 'Authored select widths and constraints; does not establish native default appearance or intrinsic width parity.', rows}, null, 2));
  } finally { await browser.close(); }
})();
