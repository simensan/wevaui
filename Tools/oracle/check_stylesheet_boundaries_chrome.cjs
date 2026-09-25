// Separate stylesheets have independent parser/import boundaries. Mirrors
// NativeLinkedStylesheetTests and the ABI host-before-markup ordering test.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const {launch} = require('./chrome_test_browser.cjs');
const cases = [
    {first: '#other{width:1px}', second: "@import 'imported.css'; #markup{width:55px}", width: 73},
    {first: '/* unfinished', second: '#box{width:83px} #markup{width:55px}', width: 83},
];
(async () => {
    const server = http.createServer((req, res) => {
        const [, index, file] = req.url.split('/');
        const test = cases[Number(index)];
        if (!test) { res.statusCode = 404; res.end(); return; }
        if (file === 'index.html') {
            res.setHeader('Content-Type', 'text/html');
            res.end('<link rel=stylesheet href=first.css><link rel=stylesheet href=second.css>' +
                '<style id=markup-style>html,body{margin:0}div{height:20px;background:#37a66f}' +
                '#markup{width:99px;background:#5899d7}</style><div id=box></div><div id=markup></div>');
        } else {
            res.setHeader('Content-Type', 'text/css');
            res.end(file === 'first.css' ? test.first : file === 'second.css' ? test.second : '#box{width:73px}');
        }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless: true});
        const page = await browser.newPage();
        const receipts = [];
        for (let index = 0; index < cases.length; ++index) {
            await page.setViewport({width: 320, height: 100});
            await page.goto(`http://127.0.0.1:${server.address().port}/${index}/index.html`);
            const widths = () => page.evaluate(() => ['box', 'markup'].map(id => document.getElementById(id).getBoundingClientRect().width));
            const initial = await widths();
            await page.evaluate(() => {
                const sheet = document.createElement('style');
                sheet.textContent = '#markup{width:61px}';
                document.head.insertBefore(sheet, document.getElementById('markup-style'));
            });
            const appended = await widths();
            await page.setViewport({width: 420, height: 100});
            const resized = await widths();
            receipts.push({index, initial, appended, resized});
            for (const actual of [initial, appended, resized]) assert.deepEqual(actual, [cases[index].width, 99]);
        }
        await page.screenshot({path: process.argv[2] + '.png'});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), receipts}, null, 2));
        console.log('6 stylesheet boundary/order checks passed');
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
