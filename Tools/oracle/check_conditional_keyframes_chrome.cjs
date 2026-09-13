const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../Tools/Layout/package.json'))('puppeteer');

(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await puppeteer.launch({headless: true,
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const k = n => `@keyframes pulse {from {width:${n}px} to {width:${n}px}}`;
    const cases = [
      ['media', `@media (min-width:600px) {${k(100)}}`],
      ['supports', `@supports (display:block) {${k(100)}}`],
      ['unsupported-condition', `@supports (display:made-up) {${k(100)}}`],
      ['nested', `@supports (display:block) {@media (min-width:600px) {${k(100)}}}`],
      ['unknown', `@unknown {${k(100)}}`],
      ['layer', `@layer theme {${k(100)}}`],
      ['unlayered-wins', `${k(80)} @layer theme {${k(100)}}`],
      ['layer-order', `@layer first, last; @layer last {${k(100)}} @layer first {${k(80)}}`],
      ['source-order', `${k(80)} @media (min-width:600px) {${k(100)}}`],
      ['parent-before-child', `@layer outer {${k(100)} @layer inner {${k(80)}}}`],
      ['parent-after-child', `@layer outer {@layer inner {${k(80)}} ${k(100)}}`],
      ['nested-top-order', `@layer first,last; @layer last {${k(100)}} @layer first.inner {${k(80)}}`],
      ['nested-sibling-order', `@layer outer { @layer first,last; @layer last {${k(100)}} @layer first {${k(80)}} }`],
      ['implicit-parent-order', `@layer first.inner {${k(80)}} @layer last {${k(100)}} @layer first.other {${k(60)}}`],
      ['nested-source-order', `@layer outer.inner {${k(80)}} @layer outer.inner {${k(100)}}`],
    ];
    const rows = [];
    for (const [name, css] of cases) {
      await page.setViewport({width:400, height:300});
      await page.setContent(`<style>#a {width:20px;animation:pulse 1s linear infinite} ${css}</style><div id=a>HUD</div>`);
      const widths = [];
      for (const width of [400,800,400]) {
        await page.setViewport({width,height:300});
        widths.push(await page.$eval('#a', e => e.getBoundingClientRect().width));
      }
      rows.push({name,css,widths});
    }
    fs.writeFileSync(output, JSON.stringify({browser:await browser.version(),rows},null,2)+'\n');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
