const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');

(async () => {
  if (!process.argv[2]) throw new Error('Expected new output JSON path');
  const browser = await launch({headless: true,
    executablePath: undefined});
  try {
    const page = await browser.newPage();
    const rows = [];
    await page.setViewport({width: 640, height: 480});
    for (const mode of ['auto', 'hint', 'manual']) for (const cancel of [false, true]) {
      await page.setContent(`<style>body{margin:0}#outside{position:absolute;left:10px;top:10px;width:100px;height:30px}
        [popover]{position:fixed;margin:0;inset:auto;left:200px;top:100px;width:100px;height:60px}</style>
        <button id="outside" popovertarget="target">Open</button><div id="other" popover="${mode}">Other</div>
        <div id="target" popover="${mode}"><input id="inside" autofocus></div>`);
      await page.evaluate(({cancel}) => {
        const other = document.getElementById('other');
        const target = document.getElementById('target');
        document.getElementById('outside').focus();
        other.showPopover();
        window.probe = {events: []};
        window.probeSnapshot = () => ({open: Array.from(document.querySelectorAll(':popover-open'), e => e.id),
          focus: document.activeElement?.id ?? ''});
        for (const element of [other, target]) element.addEventListener('beforetoggle', event => {
          window.probe.events.push({id: element.id, newState: event.newState, ...window.probeSnapshot()});
          if (element === target && event.newState === 'open' && cancel) event.preventDefault();
        });
      }, {cancel});
      await page.mouse.click(30, 20);
      rows.push({mode, cancel, ...await page.evaluate(() => ({events: window.probe.events, final: window.probeSnapshot()}))});
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), rows}, null, 2), {flag: 'wx'});
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
