const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');

(async () => {
  const output = process.argv[2];
  if (!output || fs.existsSync(output)) throw new Error('Expected new output JSON path');
  const browser = await launch({headless: true,
    executablePath: undefined});
  try {
    const page = await browser.newPage();
    const cases = [
      '@unknown { #a { color: red } }',
      '@unknown { @media all { #a { color: red } } }',
      '@media all { @unknown { #a { color: red } } }',
      '@supports (display: block) { @unknown { #a { color: red } } }',
      '@layer theme { @unknown { #a { color: red !important } } }',
      '@keyframes fade { from { color: red } to { color: blue } }',
    ];
    const rows = [];
    for (const css of cases) {
      await page.setContent(`<style>from { color: green } ${css}</style><from id=a>x</from>`);
      const before = await page.$eval('#a', e => getComputedStyle(e).color);
      await page.addStyleTag({content: '@media all { #a { color: blue } }'});
      const after = await page.$eval('#a', e => getComputedStyle(e).color);
      rows.push({css, before, after, passed: before === 'rgb(0, 128, 0)' && after === 'rgb(0, 0, 255)'});
    }
    const report = {browser: await browser.version(), rows, passed: rows.every(r => r.passed)};
    fs.writeFileSync(output, JSON.stringify(report, null, 2) + '\n');
    if (!report.passed) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
