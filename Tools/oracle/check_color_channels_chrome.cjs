// CSS Color 4 channel scales and the modern/legacy grammar.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');
const valid = [
    ['rgb(255 50% 0)', [255,127,0,255]],
    ['rgb(50% 64 20%)', [127,64,51,255]],
    ['rgba(none 50% 255 / 50%)', [0,128,255,128]],
    ['rgb(100%,50%,0%)', [255,128,0,255]],
    ['rgb(127.5 128.5 .5)', [128,129,1,255]],
    ['rgb(-10 110% 0)', [0,255,0,255]],
    ['hsl(.5turn 100 50)', [0,255,255,255]],
    ['hsl(120,100%,50%)', [0,255,0,255]],
    ['hwb(120 0% 0%)', [0,255,0,255]],
    ['hwb(120 0 0)', [0,255,0,255]],
    ['lab(50% 0 0)', [119,119,119,255]],
    ['oklch(1 0 0)', [255,255,255,255]],
    ['color(srgb 50% .25 0)', [128,64,0,255]],
];
const invalid = [
    'rgb(255,50%,0)', 'rgb(1 2 3 .5)', 'rgb(1 2 3 /)',
    'rgb(1,2,3 / .5)', 'rgb(,1,2,3)', 'rgb(1,2,3,)', 'rgb(1,,2,3)',
    'rgb(none,0,0)', 'rgb(10deg 0 0)', 'rgb(1 2 3 / 1deg)',
    'hsl(20% 100% 50%)', 'hsl(120,100,50)', 'hsl(120 30deg 50%)',
    'hwb(120,0%,0%)', 'lab(50,0,0)',
    'lab(50 10deg 0)', 'lch(50 10 50%)', 'oklch(.5 .1 50%)',
    'color(srgb,1,0,0)', 'color(srgb 1deg 0 0)',
];
(async () => {
    const browser = await launch({headless:true});
    try {
        const page = await browser.newPage();
        const rows = await page.evaluate(({valid, invalid}) => {
            const canvas = document.createElement('canvas');
            canvas.width = canvas.height = 1;
            const ctx = canvas.getContext('2d');
            return [...valid.map(([css, expected]) => ({css, expected})), ...invalid.map(css => ({css}))].map(test => {
                const supported = CSS.supports('color', test.css);
                ctx.clearRect(0,0,1,1);
                ctx.fillStyle = test.css;
                ctx.fillRect(0,0,1,1);
                return {...test, supported, actual:[...ctx.getImageData(0,0,1,1).data]};
            });
        }, {valid, invalid});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), rows}, null, 2));
        for (const row of rows) {
            assert.equal(row.supported, !!row.expected, row.css);
            if (row.expected) assert.deepEqual(row.actual, row.expected, row.css);
        }
        // The same swatches can be rendered through weva_render: authored
        // functions on the left, Chrome's byte values on the right.
        const html = '<style>html,body{margin:0;background:white}.row{display:flex;gap:12px;margin:8px}' +
            'i{display:block;width:180px;height:32px}</style>' + valid.map(([css, rgba]) =>
                `<div class=row><i style="background-color:${css}"></i>` +
                `<i style="background-color:rgba(${rgba.slice(0,3).join(',')},${rgba[3]/255})"></i></div>`).join('');
        fs.writeFileSync(process.argv[2] + '.html', html);
        await page.setViewport({width:388, height:528});
        await page.setContent(html);
        await page.screenshot({path:process.argv[2] + '.png'});
        console.log(`${rows.length} color channel and grammar checks passed`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
