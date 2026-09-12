// Record synchronous event/state ordering separately from queued toggle events.
const fs = require('node:fs');
const path = require('node:path');
const {createRequire} = require('node:module');
const puppeteer = createRequire(path.resolve(__dirname, '../../../Tools/Layout/package.json'))('puppeteer');

(async () => {
  if (!process.argv[2]) throw new Error('Expected a new output JSON path');
  const browser = await puppeteer.launch({headless: true,
    executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const mode of ['auto', 'hint', 'manual']) {
      for (const action of ['open', 'cancel-open', 'remove-attribute', 'remove-element',
                            'close', 'cancel-close', 'repeat-open', 'repeat-close', 'close-remove', 'close-reopen']) {
        await page.setContent(`<button id="outside">Outside</button>
          <div id="other" popover="${mode}">Existing</div>
          <div id="target" popover="${mode}"><button id="inside" autofocus>Inside</button></div>`);
        const result = await page.evaluate(async ({action}) => {
          const target = document.getElementById('target');
          const other = document.getElementById('other');
          document.getElementById('outside').focus();
          other.showPopover();
          if (['close', 'cancel-close', 'repeat-open', 'repeat-close', 'close-remove', 'close-reopen'].includes(action)) target.showPopover();
          if (action === 'repeat-close') target.hidePopover();
          // Drain setup notifications before observing the operation under test.
          await new Promise(resolve => setTimeout(resolve, 0));
          const snapshot = () => ({open: target.matches(':popover-open'),
            otherOpen: other.matches(':popover-open'), connected: target.isConnected,
            focus: document.activeElement?.id ?? ''});
          const events = [];
          let mutated = false;
          const observe = event => {
            events.push({type: event.type, oldState: event.oldState, newState: event.newState,
              cancelable: event.cancelable, bubbles: event.bubbles, ...snapshot()});
            if (event.type !== 'beforetoggle' || mutated) return;
            mutated = true;
            if (action === 'close-remove') target.remove();
            if (action === 'close-reopen') { target.hidePopover(); target.showPopover(); }
            if (action === 'cancel-open' || action === 'cancel-close') event.preventDefault();
            if (action === 'remove-attribute') target.removeAttribute('popover');
            if (action === 'remove-element') target.remove();
          };
          target.addEventListener('beforetoggle', observe);
          target.addEventListener('toggle', observe);
          const before = snapshot();
          let error = null;
          try {
            if (['close', 'cancel-close', 'repeat-close', 'close-remove', 'close-reopen'].includes(action)) target.hidePopover();
            else target.showPopover();
          } catch (e) { error = e.name; }
          const immediate = snapshot();
          const synchronousEvents = events.length;
          await new Promise(resolve => setTimeout(resolve, 0));
          return {before, immediate, settled: snapshot(), synchronousEvents, events, error};
        }, {action});
        rows.push({mode, action, ...result});
      }
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), rows}, null, 2), {flag: 'wx'});
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
