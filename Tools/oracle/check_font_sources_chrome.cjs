// Font source strings use CSS escapes and balanced functions. A later valid
// src descriptor replaces the earlier source list, including a local-only list.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {launch} = require('./chrome_test_browser.cjs');
const cases = [
    {id:'parenthesis', css:'src:url("round)font.ttf")', expected:['round)font.ttf']},
    {id:'escapes', css:String.raw`src:u\72 l(escaped\29 font.ttf)`, expected:['escaped)font.ttf']},
    {id:'quote', css:String.raw`src:url("quote\"font.ttf")`, expected:['quote"font.ttf']},
    {id:'literal', css:String.raw`src:url("\new\t\r.ttf")`, expected:['newtr.ttf']},
    {id:'local-escape', css:String.raw`src:local(No\ Such\,Review\ Face),url(ok.ttf)`, expected:['ok.ttf']},
    {id:'unicode', css:String.raw`src:url(\01f41f.ttf)`, expected:['🐟.ttf']},
    {id:'crlf', css:'src:url("a\\\r\nb.ttf")', expected:['ab.ttf']},
    {id:'local', css:'src:local("No Such (Review Face)"),url(fallback.ttf)', expected:['fallback.ttf']},
    {id:'comma', css:'src:url("missing,a.ttf") format("truetype"),url(ok.ttf)', expected:['missing,a.ttf','ok.ttf']},
    {id:'replace', css:'src:url(old.ttf);src:local("No Such Review Face"),url(new.ttf)', expected:['new.ttf']},
    {id:'invalid', css:'src:url(old.ttf);src:bogus', expected:['old.ttf']},
    {id:'from-local', css:'src:local("No Such Review Face");src:url(new.ttf)', expected:['new.ttf']},
    {id:'to-local', css:'src:url(old.ttf);src:local("No Such Review Face")', expected:[]},
];

(async () => {
    const requests = [];
    const font = fs.readFileSync(path.join(__dirname, '../../Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default.ttf'));
    const server = http.createServer((req, res) => {
        if (req.url === '/index.html') {
            res.setHeader('Content-Type', 'text/html');
            res.end(cases.map(c => `<link rel=stylesheet href=/cases/${c.id}/face.css>` +
                `<p style='font-family:Case-${c.id}'>AV ${c.id}</p>`).join(''));
            return;
        }
        const parts = new URL(req.url, 'http://localhost').pathname.split('/');
        const test = cases.find(c => c.id === parts[2]);
        if (test && parts[3] === 'face.css') {
            res.setHeader('Content-Type', 'text/css');
            res.end(`@font-face{font-family:Case-${test.id};${test.css}}`);
        } else if (test && parts[3]) {
            const name = decodeURIComponent(parts.slice(3).join('/'));
            requests.push({id:test.id, name});
            if (name.startsWith('missing')) { res.statusCode = 404; res.end(); }
            else { res.setHeader('Content-Type', 'font/ttf'); res.end(font); }
        } else { res.statusCode = 404; res.end(); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await launch({headless:true});
        const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}/index.html`, {waitUntil:'networkidle0'});
        await page.evaluate(() => document.fonts.ready);
        const descriptors = await page.evaluate(() => [...document.styleSheets].map(sheet => sheet.cssRules[0].style.getPropertyValue('src')));
        const results = cases.map((test, i) => ({...test, src:descriptors[i], actual:requests.filter(r => r.id === test.id).map(r => r.name)}));
        fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), results}, null, 2));
        for (const result of results) assert.deepEqual(result.actual, result.expected, result.id);
        console.log(`${cases.length} font source parsing and descriptor replacement checks passed`);
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
