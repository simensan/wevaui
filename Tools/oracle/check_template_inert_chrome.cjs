// A <template>'s content is inert in Chrome: it lives in template.content,
// not in the document tree, so querySelector never finds it, textContent of
// the parent does not include it, the template has no child nodes of its
// own and nothing of it gets a box. The core keeps a template's body in its
// tree (data-each clones rows from it), so its readers -- query, element
// text, children -- must hide that body the way Chrome's DOM does
// (test_abi_template_content_is_inert).
const fs = require('node:fs'), path = require('node:path');
const {launch} = require('./chrome_test_browser.cjs');
(async () => {
  const browser = await launch({headless: true, executablePath: process.argv[3] || undefined});
  try {
    const page = await browser.newPage();
    await page.setContent('<body style="margin:0"><ul id="quests" style="margin:0;padding:0"><template data-each="Quests as quest" data-key="Id"><li id="quest-{{ quest.Id }}" class="row">{{ quest.Title }}</li></template></ul><p id="after" style="margin:0">after</p></body>');
    const r = await page.evaluate(() => ({
      queryLi: document.querySelector('li') === null,
      queryAllRows: document.querySelectorAll('.row').length,
      queryTemplate: document.querySelector('template') !== null,
      templateChildren: document.querySelector('template').children.length,
      contentChildren: document.querySelector('template').content.children.length,
      ulText: document.querySelector('#quests').textContent,
      ulHeight: document.querySelector('#quests').getBoundingClientRect().height,
      afterTop: document.querySelector('#after').getBoundingClientRect().top,
      templateDisplay: getComputedStyle(document.querySelector('template')).display,
    }));
    const expected = {queryLi: true, queryAllRows: 0, queryTemplate: true, templateChildren: 0, contentChildren: 1, ulText: '', ulHeight: 0, afterTop: 0, templateDisplay: 'none'};
    const failures = Object.keys(expected).filter(k => JSON.stringify(r[k]) !== JSON.stringify(expected[k]));
    fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), result: r, expected, failures}, null, 2));
    if (failures.length) { console.error('template inertness differs from the expectation:', failures); process.exit(1); }
    console.log('template content inert in Chrome: query null, no children, no text, no box');
  } finally { await browser.close(); }
})();
