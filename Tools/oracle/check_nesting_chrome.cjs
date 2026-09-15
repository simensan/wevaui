// CSS Nesting 1 parent selectors, specificity and declaration order.
// Kept in sync with test_abi_nesting() in libweva/tests/test_c_abi_forms.cpp.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');
const cases = [
  {
    "name": "implicit descendant",
    "html": "<div class=parent><p class=target></p></div><p class=target></p>",
    "css": ".parent{.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "child combinator",
    "html": "<div class=parent><p class=target></p><div><p class=target></p></div></div><p class=target></p>",
    "css": ".parent{> .target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "explicit self",
    "html": "<p class=\"parent target\"></p><p class=target></p>",
    "css": ".parent{&.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "ancestor ampersand",
    "html": "<div class=outer><p class=\"parent target\"></p></div><p class=\"parent target\"></p>",
    "css": ".parent{.outer &{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "sibling ampersand",
    "html": "<div class=parent></div><p class=target></p><p class=target></p>",
    "css": ".parent{+ .target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "multiple levels",
    "html": "<div class=parent><div class=middle><p class=target></p></div><p class=target></p></div><div class=middle><p class=target></p></div>",
    "css": ".parent{.middle{.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "list specificity",
    "html": "<div class=parent><p class=target></p></div>",
    "css": "#absent,.parent{.target{background:red}}.parent.parent .target{background:blue}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "where ampersand",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{background:blue;:where(&){background:red}}",
    "colors": [
      "rgb(0, 0, 255)"
    ]
  },
  {
    "name": "declarations after nested rule",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{background:blue;&{background:red}background:lime}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested media declarations",
    "html": "<p class=\"parent target\"></p><p class=target></p>",
    "css": ".parent{@media all{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested media child",
    "html": "<div class=parent><p class=target></p></div><p class=target></p>",
    "css": ".parent{@media all{.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested media order",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{@media all{background:blue;&{background:red}background:lime}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested scope explicit root",
    "html": "<div class=parent><p class=target></p></div><p class=target></p>",
    "css": ".parent{@scope (&){.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested scope implicit root",
    "html": "<div class=parent><p class=target></p></div><p class=target></p>",
    "css": ".parent{@scope{.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested scope unbound root",
    "html": "<div class=parent><div class=sub><p class=target></p></div></div><div class=sub><p class=target></p></div>",
    "css": ".parent{@scope (.sub){.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested scope declarations",
    "html": "<div class=\"parent target\"></div><p class=target></p>",
    "css": ".parent{@scope (&){background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope and style nesting",
    "html": "<div class=parent><div class=middle><p class=target></p></div><p class=target></p></div>",
    "css": "@scope (.parent){.middle{.target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope nested not ampersand",
    "html": "<div class=\"parent target\"><p class=\"middle target\"></p><p class=target></p></div>",
    "css": "@scope (.parent){.middle{.target:not(&){background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "quoted ampersand",
    "html": "<div class=parent><p class=target data-key=\"&\"></p></div><p class=target data-key=\"&\"></p>",
    "css": ".parent{[data-key=\"&\"]{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid parent",
    "html": "<div class=parent><p class=target></p></div>",
    "css": ".parent:bogus{.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid nested rule",
    "html": "<div class=parent><p class=target></p></div>",
    "css": ".parent{.bad:bogus{.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "relative contains ampersand",
    "html": "<p class=\"parent target\"></p><i></i><p class=\"parent target\"></p>",
    "css": ".parent{+ i + &{background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "parent positional dependency",
    "html": "<div class=parent><p class=target></p></div><div class=parent><p class=target></p></div>",
    "css": ".parent:nth-child(2){.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "parent has dependency",
    "html": "<div class=parent><i class=on></i><p class=target></p></div><div class=parent><i></i><p class=target></p></div>",
    "css": ".parent:has(.on){.target{background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested supports declarations",
    "html": "<p class=\"parent target\"></p><p class=target></p>",
    "css": ".parent{@supports (display:block){background:red}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested supports false",
    "html": "<div class=parent><p class=target></p></div><p class=target></p>",
    "css": ".parent{@supports (display:bogus){.target{background:red}}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested layer declarations",
    "html": "<p class=\"parent target\"></p><p class=target></p>",
    "css": ".parent{@layer theme{background:red!important}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "repeated ampersand specificity",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{&&{background:red}}.target{background:blue}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "declaration run specificity",
    "html": "<p class=\"parent target\"></p>",
    "css": "#absent,.parent{&{background:red}background:blue}",
    "colors": [
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "declarations among group rules",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{background:red;@media all{background:blue}background:lime;@supports (display:block){background:red}background:blue}",
    "colors": [
      "rgb(0, 0, 255)"
    ]
  },
  {
    "name": "invalid parent list",
    "html": "<div class=parent><p class=target></p></div>",
    "css": ".parent,:bogus{.target{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid nested list",
    "html": "<div class=parent><p class=target></p></div>",
    "css": ".parent{.target,:bogus{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "invalid suffix concatenation",
    "html": "<p class=\"parent target\"></p>",
    "css": ".parent{&p{background:red}}",
    "colors": [
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "nested scope root and limit",
    "html": "<div class=parent><div class=sub><p class=target></p><div class=cut><p class=target></p></div></div></div><div class=sub><p class=target></p></div>",
    "css": ".parent{@scope (& > .sub) to (& .cut){& .target{background:red}}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope declarations root",
    "html": "<div class=\"parent target\"><p class=target></p></div><p class=target></p>",
    "css": "@scope (.target){background:red!important}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(255, 0, 0)",
      "rgb(255, 0, 0)"
    ]
  },
  {
    "name": "nested scope declarations root",
    "html": "<div class=\"parent target\"><p class=target></p></div><p class=target></p>",
    "css": ".parent{@scope (&){background:red!important}}",
    "colors": [
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope group has no style parent",
    "html": "<div class=\"parent target\"><p class=target></p></div><p class=target></p>",
    "css": ".parent{@scope (&){@media all{background:red!important}}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope group declarations without parent",
    "html": "<div class=\"parent target\"><p class=target></p></div><p class=target></p>",
    "css": "@scope (.target){@media all{background:red!important}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)",
      "rgb(0, 255, 0)"
    ]
  },
  {
    "name": "scope unbound declarations root",
    "html": "<div class=\"parent target\"><p class=target></p></div><p class=target></p>",
    "css": ".parent{@scope (.target){background:red!important}}",
    "colors": [
      "rgb(0, 255, 0)",
      "rgb(255, 0, 0)",
      "rgb(0, 255, 0)"
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
        const pseudoCases = [
            ['.target,.target::before{content:"x";color:blue;@media all{color:red}}', ['rgb(255, 0, 0)', 'rgb(255, 0, 0)']],
            ['.target,.target::before{content:"x";color:blue;&{color:red}}', ['rgb(255, 0, 0)', 'rgb(0, 0, 255)']],
            ['.target,.target::before{content:"x";color:blue;@media all{&{color:red}}color:lime}', ['rgb(0, 255, 0)', 'rgb(0, 255, 0)']],
            ['.target::before{content:"x";color:blue;@media all{color:red}color:lime}', ['rgb(0, 0, 0)', 'rgb(0, 255, 0)']],
            ['#absent::before,.target{&{color:red}}.target.target{color:blue}', ['rgb(0, 0, 255)', 'rgb(0, 0, 255)']],
        ];
        for (const [css, colors] of pseudoCases) {
            await page.setContent('<style>' + css + '</style><p class=target></p>');
            rows.push({name:'pseudo nesting: ' + css, css, colors,
                actual:await page.$eval('p', n => [getComputedStyle(n).color, getComputedStyle(n, '::before').color])});
        }
        await page.setContent('<style>p::before{content:"x";color:blue;@media all{color:red}}</style>' +
            '<style>p::before{color:lime}</style><p></p>');
        rows.push({name:'pseudo sheet source order', colors:['rgb(0, 255, 0)'],
            actual:await page.$eval('p', n => [getComputedStyle(n, '::before').color])});
        // The component contract uses marker attributes, not a ShadowRoot.
        // Check that translated DOM/CSS against Chrome's ordinary selectors.
        await page.setContent('<style>.target{background:lime}[data-uui-host=card]{&.hot{background:blue}}' +
            '.frame[data-uui-scope=card]{.target[data-uui-scope=card]{background:red}}</style>' +
            '<div id=host class=hot data-uui-host=card><div class=frame data-uui-scope=card>' +
            '<p class=target data-uui-scope=card></p><p id=light class=target></p></div>' +
            '<p class="target spare" data-uui-scope=card></p></div><p id=outside class=target></p>');
        rows.push({name:'nested component marker boundary', colors:['rgb(0, 0, 255)', 'rgb(255, 0, 0)',
            'rgb(0, 255, 0)', 'rgb(0, 255, 0)', 'rgb(0, 255, 0)'], actual:await page.evaluate(() => {
                return [document.querySelector('#host'), document.querySelector('.frame .target'), document.querySelector('.spare'),
                    document.querySelector('#light'), document.querySelector('#outside')].map(n => getComputedStyle(n).backgroundColor);
            })});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), rows}, null, 2));
        for (const row of rows) assert.deepEqual(row.actual, row.colors, row.name);
        console.log(`${rows.length} nesting checks passed`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
