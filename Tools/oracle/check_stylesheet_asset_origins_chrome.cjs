// Relative image/font URLs belong to the stylesheet that uses them. A URL
// introduced through var() uses that declaration's sheet, not the variable's.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const {launch} = require('./chrome_test_browser.cjs');
const theme = `@import 'nested/child.css'; @import './theme.css';
    :root{--external:url(shared.png)}
    #direct{background-image:url(tile.png)}
    #variable{background-image:var(--inline)}
    #shorthand{background:var(--inline)}
    #fallback{background-image:var(--missing,url(fallback.png))}
    @keyframes origin{from,to{background-image:url(animated.png)}}
    #animation{animation:origin 1s paused both}
    #pseudo::before{content:'';display:block;width:80px;height:20px;background-image:var(--inline)}
    @font-face{font-family:Origin;src:local(NoSuchOriginFont),url(font.woff2)}
    #font{font-family:Origin}`;
const child = '#nested{background-image:url(tile.png)}';
const inline = `:root{--inline:url(inline.png)}
    html,body{margin:0}div{width:80px;height:20px;margin:2px;background-size:cover}
    #document{background-image:var(--external)}`;
const ids = ['direct', 'variable', 'shorthand', 'fallback', 'nested', 'document', 'animation'];
const expected = ['/ui/styles/tile.png', '/ui/styles/inline.png', '/ui/styles/inline.png',
    '/ui/styles/fallback.png', '/ui/styles/nested/tile.png', '/ui/shared.png', '/ui/styles/animated.png'];

(async () => {
    const requests = [];
    const server = http.createServer((req, res) => {
        requests.push(req.url);
        if (req.url === '/ui/index.html') {
            res.setHeader('Content-Type', 'text/html');
            res.end(`<link rel=stylesheet href=styles/theme.css><style>${inline}</style>` +
                [...ids, 'pseudo'].map(id => `<div id=${id}></div>`).join('') + '<span id=font>x</span>');
        } else if (req.url === '/ui/styles/theme.css' || req.url === '/ui/styles/nested/child.css') {
            res.setHeader('Content-Type', 'text/css');
            res.end(req.url.endsWith('theme.css') ? theme : child);
        } else if (req.url.endsWith('.png')) {
            res.setHeader('Content-Type', 'image/png');
            res.end(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMwX5YPAAJkAU2q/7qVAAAAAElFTkSuQmCC', 'base64'));
        } else { res.statusCode = 404; res.end(); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless: true});
        const page = await browser.newPage();
        await page.setViewport({width: 200, height: 220});
        await page.goto(`http://127.0.0.1:${server.address().port}/ui/index.html`, {waitUntil:'networkidle0'});
        const actual = await page.evaluate(ids => {
            const path = value => new URL(value.slice(5, -2)).pathname;
            return [...ids.map(id => path(getComputedStyle(document.getElementById(id)).backgroundImage)),
                path(getComputedStyle(document.getElementById('pseudo'), '::before').backgroundImage)];
        }, ids);
        const references = ['?v=2', '#icon', '/shared.png', '//cdn.test/icon.png', '../media/a).png'];
        const urls = await page.evaluate(references => references.map(ref =>
            new URL(ref, 'https://example.test/ui/styles/theme.css?v=1#top').href), references);
        await page.screenshot({path: process.argv[2] + '.png'});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), actual, requests, urls}, null, 2));
        assert.deepEqual(actual, [...expected, '/ui/styles/inline.png']);
        assert(requests.includes('/ui/styles/font.woff2'));
        assert.equal(requests.filter(path => path === '/ui/styles/theme.css').length, 1);
        assert.deepEqual(urls, ['https://example.test/ui/styles/theme.css?v=2',
            'https://example.test/ui/styles/theme.css?v=1#icon', 'https://example.test/shared.png',
            'https://cdn.test/icon.png', 'https://example.test/ui/media/a).png']);
        console.log('15 stylesheet image/font origin, cycle and URL reference checks passed');
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
