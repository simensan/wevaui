const fs = require('node:fs'), path = require('node:path');
const {launch} = require('./chrome_test_browser.cjs');
(async () => {
  const browser = await launch({headless:true, executablePath:undefined});
  try {
    const page = await browser.newPage();
    const rows = [];
    for (const modal of [false, true]) for (const attrs of ['tabindex=-1', 'tabindex=-1 autofocus', 'tabindex=0', 'tabindex=5', 'disabled autofocus', 'style="display:none" autofocus']) {
      const html = '<button id=outside>Open</button><dialog id=d><button id=first '+attrs+'>First</button><button id=second>Second</button></dialog>';
      await page.setContent(html);
      const focused = await page.evaluate(modal => {document.querySelector('#outside').focus(); document.querySelector('#d')[modal?'showModal':'show'](); return document.activeElement.id;}, modal);
      rows.push({modal, attrs, html, focused});
    }
    const editing = [];
    for (const modal of [false, true]) for (const tag of ['input', 'textarea']) {
      await page.setContent('<button id=outside>Open</button><dialog id=d>'+ (tag === 'input' ? '<input id=field value=abcdef>' : '<textarea id=field>abcdef</textarea>') + '</dialog>');
      editing.push(await page.evaluate(({modal, tag}) => {
        const field = document.querySelector('#field');
        document.querySelector('#outside').focus();
        document.querySelector('#d')[modal?'showModal':'show']();
        return {modal, tag, focused:document.activeElement.id, start:field.selectionStart, end:field.selectionEnd};
      }, {modal, tag}));
    }
    fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), rows, editing}, null, 2));
  } finally {await browser.close();}
})();
