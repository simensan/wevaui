const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');

(async () => {
  if (!process.argv[2]) throw new Error('Expected new output JSON path');
  const browser = await puppeteer.launch({headless: true,
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const mode of ['auto', 'hint', 'manual']) {
      for (const action of ['replace', 'close-parent', 'remove-attribute', 'change-mode', 'remove-parent', 'replace-remove-target', 'replace-change-target-mode']) {
        await page.setContent(`<button id="outside">Outside</button>
          <div id="parent" popover="${mode}"><button id="pfield" autofocus>Parent</button>
            <div id="child" popover="${mode}"><button id="cfield" autofocus>Child</button></div></div>
          <div id="replacement" popover="${mode}"><button id="rfield" autofocus>Replacement</button></div>`);
        const result = await page.evaluate(async ({action}) => {
          const parent = document.getElementById('parent');
          const child = document.getElementById('child');
          const replacement = document.getElementById('replacement');
          document.getElementById('outside').focus();
          parent.showPopover();
          child.showPopover();
          await new Promise(resolve => setTimeout(resolve, 0));
          const snapshot = () => ({open: Array.from(document.querySelectorAll(':popover-open'), e => e.id),
            focus: document.activeElement?.id ?? '', parentAttribute: parent.getAttribute('popover')});
          const events = [];
          for (const element of [parent, child, replacement]) {
            element.addEventListener('beforetoggle', event => events.push({id: element.id,
              oldState: event.oldState, newState: event.newState, cancelable: event.cancelable,
              ...snapshot()}));
            element.addEventListener('beforetoggle', event => {
              if (element !== child || event.newState !== 'closed') return;
              if (action === 'replace-remove-target') replacement.remove();
              if (action === 'replace-change-target-mode') replacement.setAttribute('popover', 'manual');
            });
          }
          let error = null;
          if (action.startsWith('replace')) {
            try { replacement.showPopover(); } catch (e) { error = e.name; }
          }
          if (action === 'close-parent') parent.hidePopover();
          if (action === 'remove-attribute') parent.removeAttribute('popover');
          if (action === 'change-mode') parent.setAttribute('popover', 'manual');
          if (action === 'remove-parent') parent.remove();
          const immediate = snapshot();
          await new Promise(resolve => setTimeout(resolve, 0));
          return {events, immediate, settled: snapshot(), error};
        }, {action});
        rows.push({mode, action, ...result});
      }
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), rows}, null, 2), {flag: 'wx'});
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
