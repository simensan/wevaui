// CSS Cascade 6 scoped selectors, boundaries, proximity and stylesheet ownership.
// Kept in sync with test_abi_scopes() in libweva/tests/test_c_abi_forms.cpp.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');
const cases = [
  {
    "name": "default root match",
    "html": "<p class=target></p>",
    "css": "@scope (.target){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "outside ancestor without scope",
    "html": "<div class=outer><section class=card><p class=target></p></section></div>",
    "css": "@scope (.card){.outer .target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "relative child",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer){> .target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "ampersand root",
    "html": "<p class=target></p>",
    "css": "@scope (.target){&{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested explicit same root",
    "html": "<div class=\"target outer\"></div>",
    "css": "@scope (.outer){@scope (:scope){:scope{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "nested root instances",
    "html": "<div class=card><div class=card><p class=target></p></div></div>",
    "css": "@scope (.card){@scope (.card){.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "nearest root wins",
    "html": "<div class=outer><div class=inner><p class=target></p></div></div>",
    "css": "@scope (.inner){.target{background:red}} @scope (.outer){.target{background:blue}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "nested root cannot escape",
    "html": "<div class=outer><div class=inner><p class=target></p></div></div>",
    "css": "@scope (.inner){@scope (.outer){.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "unknown root pseudo",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (:bogus){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "empty explicit root",
    "html": "<p class=target></p>",
    "css": "@scope (){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "trailing garbage",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer) junk {.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "quoted parenthesis",
    "html": "<div data-key=\")\"><p class=target></p></div>",
    "css": "@scope ([data-key=\")\"]){.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "scope has dependency",
    "html": "<div class=card><i class=on></i><p class=target></p></div><div class=card><i></i><p class=target></p></div>",
    "css": "@scope (.card:has(.on)){.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope sibling dependency",
    "html": "<div class=card><p class=target></p></div><div class=card><p class=target></p></div>",
    "css": "@scope (.card:nth-child(2)){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "limit has dependency",
    "html": "<div class=outer><div class=card><i class=on></i><p class=target></p></div><div class=card><i></i><p class=target></p></div></div>",
    "css": "@scope (.outer) to (.card:has(.on)){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "outer scope fallback",
    "html": "<div class=\"outer card\"><div class=card><p class=target></p></div></div>",
    "css": "@scope (.card){@scope (.outer){.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "body scope fallback",
    "html": "<div class=\"outer card\"><div class=card><p class=target></p></div></div>",
    "css": "@scope (.card){.outer:scope .target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "nested implicit root",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer){@scope{:scope > .target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid root list",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer, :bogus){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid limit",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer) to (:bogus){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid limit list",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer) to (.cut, :bogus){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "empty limit",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer) to (){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "quoted parenthesis boundary",
    "html": "<div data-key=\")\"><p class=target></p></div><div><p class=target></p></div>",
    "css": "@scope ([data-key=\")\"]){.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope specificity before proximity",
    "html": "<div class=outer><div class=inner><p class=target></p></div></div>",
    "css": "@scope (.outer){.target.target{background:red}}@scope (.inner){.target{background:blue}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "scope before source order",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer){.target{background:red}}.target{background:blue}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "same distance source order",
    "html": "<div class=outer><p class=target></p></div>",
    "css": "@scope (.outer){.target{background:red}}@scope (.outer){.target{background:blue}}",
    "colors": [
      "rgb(0, 0, 255)"
    ]
  },
  {
    "name": "explicit root specificity",
    "html": "<p class=target></p>",
    "css": "@scope (.target){:scope{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "ampersand root matching",
    "html": "<p class=target></p>",
    "css": "@scope (.target){&.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "explicit ancestor outside boundary",
    "html": "<div class=outer><div class=card><p class=target></p></div></div>",
    "css": "@scope (.card){.outer :scope .target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "limit relative child",
    "html": "<div class=outer><div class=cut><p class=target></p></div><div><div class=cut><p class=target></p></div></div></div>",
    "css": "@scope (.outer) to (> .cut){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "implicit owned root",
    "html": "<div><style>@scope{:scope > .target{background:red}}</style><p class=target></p></div><div><p class=target></p></div>",
    "css": "",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "implicit head root",
    "html": "<p class=target></p>",
    "css": "@scope{.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested limit excludes inner",
    "html": "<div class=outer><div class=cut><div class=inner><p class=target></p></div></div><div class=inner><p class=target></p></div></div>",
    "css": "@scope (.outer) to (.cut){@scope (.inner){.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  }
];
(async () => {
    const browser = await launch({headless:true});
    try {
        const page = await browser.newPage(), rows = [];
        for (const test of cases) {
            await page.setContent('<style>.target{background:lime}' + test.css + '</style>' + test.html);
            const actual = await page.$$eval('.target', nodes => nodes.map(n => getComputedStyle(n).backgroundColor));
            rows.push({...test, actual});
        }
        await page.setContent('<style>@scope (.inner){p::before{content:"inner";color:red}}' +
            '@scope (.outer){p::before{content:"outer";color:blue}}</style>' +
            '<div class=outer><div class=inner><p></p></div></div>');
        rows.push({name:'pseudo proximity', colors:['rgb(255, 0, 0)', '"inner"'],
            actual:await page.$eval('p', n => {const s = getComputedStyle(n, '::before'); return [s.color, s.content];})});
        for (const limit of [false, true]) {
            await page.setContent('<style>.target{background:lime}' +
                (limit ? '@scope (.outer) to (.card:has(.on))' : '@scope (.card:has(.on))') +
                '{.target{background:red}}</style><div class=outer><div class=card>' +
                '<i id=flag></i><p class=target></p></div></div>');
            const actual = await page.evaluate(() => {
                const color = () => getComputedStyle(document.querySelector('.target')).backgroundColor;
                const before = color(); document.querySelector('#flag').className = 'on';
                const during = color(); document.querySelector('#flag').className = '';
                return [before, during, color()];
            });
            rows.push({name:limit ? 'limit has mutation' : 'root has mutation', actual,
                colors:limit ? ['rgb(255, 0, 0)', 'rgb(0, 255, 0)', 'rgb(255, 0, 0)'] :
                    ['rgb(0, 255, 0)', 'rgb(255, 0, 0)', 'rgb(0, 255, 0)']});
        }
        for (const state of ['hover', 'active']) for (const limit of [false, true]) {
            await page.mouse.move(390, 290);
            await page.setContent('<style>.target{background:lime;width:80px;height:30px}' +
                (limit ? '@scope (.outer) to (.card:' : '@scope (.card:') + state +
                '){.target{background:red}}</style><div class=outer><div class=card><p class=target></p></div></div>');
            const color = () => page.$eval('.target', n => getComputedStyle(n).backgroundColor);
            const actual = [await color()];
            const box = await page.$eval('.target', n => {const r = n.getBoundingClientRect(); return {x:r.x, y:r.y};});
            await page.mouse.move(box.x + 2, box.y + 2);
            if (state === 'active') await page.mouse.down();
            actual.push(await color());
            if (state === 'active') await page.mouse.up();
            await page.mouse.move(390, 290);
            actual.push(await color());
            rows.push({name:`${limit ? 'limit' : 'root'} ${state} mutation`, actual,
                colors:limit ? ['rgb(255, 0, 0)', 'rgb(0, 255, 0)', 'rgb(255, 0, 0)'] :
                    ['rgb(0, 255, 0)', 'rgb(255, 0, 0)', 'rgb(0, 255, 0)']});
        }
        await page.setContent('<style>.target{background:lime}</style><div id=first>' +
            '<style id=owned>@scope{.target{background:red}}</style><p class=target></p></div>' +
            '<div id=second><p class=target></p></div>');
        rows.push({name:'reloaded stylesheet owner', colors:['rgb(255, 0, 0)', 'rgb(0, 255, 0)',
            'rgb(0, 255, 0)', 'rgb(255, 0, 0)'], actual:await page.evaluate(() => {
                const colors = () => [...document.querySelectorAll('.target')].map(n => getComputedStyle(n).backgroundColor);
                const before = colors();
                document.body.innerHTML = '<div id=first><p class=target></p></div><div id=second>' +
                    '<style>@scope{.target{background:red}}</style><p class=target></p></div>';
                return [...before, ...colors()];
            })});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), rows}, null, 2));
        for (const row of rows) assert.deepEqual(row.actual, row.colors, row.name);
        console.log(`${rows.length} scope checks passed`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
