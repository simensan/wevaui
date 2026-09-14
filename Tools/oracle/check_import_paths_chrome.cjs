// Nested import paths, quoted URL punctuation, cycle aliases and anonymous
// layers. Mirrors test_abi_at_import_paths through a real HTTP stylesheet tree.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const {launch} = require('./chrome_test_browser.cjs');

const css = String.raw`@import "styles/theme.css";
    @import url("styles/close).css");
    @import "styles/quoted\".css";
    @import url(styles/paren\(.css);
    @import "layer.css" layer;
    @layer weva-anonymous-import-1 { .layer { width: 109px } }
    html, body { margin: 0 }
    div { height: 20px; background: #37a66f }`;
const ids = ['theme', 'palette', 'child', 'base', 'close', 'quote', 'paren', 'layer'];
const sheets = {
    '/ui/styles/theme.css': `@import '../palette.css'; @import 'nested/child.css';
        @import '../styles/./theme.css'; #theme { width: 101px }`,
    '/ui/palette.css': '#palette { width: 102px }',
    '/ui/styles/nested/child.css': `@import '../../shared/base.css'; #child { width: 103px }`,
    '/ui/shared/base.css': '#base { width: 104px }',
    '/ui/styles/close).css': '#close { width: 105px }',
    '/ui/styles/quoted".css': '#quote { width: 106px }',
    '/ui/styles/paren(.css': '#paren { width: 107px }',
    '/ui/layer.css': '#layer { width: 108px }',
};

(async () => {
    const requests = [];
    const server = http.createServer((req, res) => {
        const path = decodeURIComponent(new URL(req.url, 'http://fixture').pathname);
        if (path === '/ui/index.html') {
            res.setHeader('Content-Type', 'text/html');
            res.end(`<style>${css}</style>${ids.map(id => `<div id=${id} class=layer></div>`).join('')}`);
        } else if (Object.hasOwn(sheets, path)) {
            requests.push(path);
            res.setHeader('Content-Type', 'text/css');
            res.end(sheets[path]);
        } else { res.statusCode = 404; res.end(); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless: true, executablePath: process.argv[3], args: ['--no-sandbox']});
        const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}/ui/index.html`, {waitUntil: 'networkidle0'});
        const widths = await page.evaluate(ids => ids.map(id => document.getElementById(id).getBoundingClientRect().width), ids);
        const expected = [101, 102, 103, 104, 105, 106, 107, 109];
        fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), widths, expected, requests}, null, 2));
        assert.deepEqual(widths, expected);
        assert.deepEqual([...requests].sort(), Object.keys(sheets).sort());
        console.log('8 imported stylesheet paths and anonymous-layer priority passed');
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
