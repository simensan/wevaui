// HTML parsing used for linked stylesheet discovery in both the editor baker
// and live host. Mirrors NativeLinkedStylesheetTests.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');

const cases = [
    ["<!-- <link rel=stylesheet href=comment.css> -->", []],
    ["<script>'<link rel=stylesheet href=script.css>'</script>", []],
    ["<textarea><link rel=stylesheet href=textarea.css></textarea>", []],
    ["<template><link rel=stylesheet href=template.css></template>", []],
    ["<link rel=stylesheet data-href=data.css>", []],
    ["<link data-rel=stylesheet href=data.css>", []],
    ["<link title='a>b' rel=stylesheet href=real.css>", ['real.css']],
    ["<link rel='style&#x73;heet' href='theme&amp;mode.css'>", ['theme&mode.css']],
    ["<link rel='alternate\fSTYLESHEET' href=real.css>", ['real.css']],
    ["<link rel=stylesheet href=first.css href=second.css>", ['first.css']],
    ["<link id=sheet rel=stylesheet href='' HREF=second.css>", []],
];

(async () => {
    const browser = await launch({headless: true, executablePath: process.argv[3], args: ['--no-sandbox']});
    try {
        const page = await browser.newPage();
        const receipts = [];
        for (const [html, expected] of cases) {
            const actual = await page.evaluate(source => {
                const doc = new DOMParser().parseFromString(source, 'text/html');
                return [...doc.querySelectorAll('link[rel~="stylesheet" i][href]')]
                    .map(link => link.getAttribute('href')).filter(Boolean);
            }, html);
            receipts.push({html, expected, actual});
        }
        // Pin raw-text/RCDATA behavior in the shared parser as well as link
        // discovery. These elements do not turn their contents into live tags.
        for (const tag of ['script', 'style', 'textarea', 'title', 'xmp', 'iframe', 'noembed', 'noframes']) {
            const text = `<link id=fake rel=stylesheet href=fake.css><2</${tag}-other>&amp;`;
            const html = `<${tag} id=source>${text}</${tag}><div id=after></div>`;
            const actual = await page.evaluate(source => {
                const doc = new DOMParser().parseFromString(source, 'text/html');
                return {fake: !!doc.getElementById('fake'), after: !!doc.getElementById('after'),
                    text: doc.getElementById('source').textContent};
            }, html);
            const decoded = tag === 'textarea' || tag === 'title';
            const expected = {fake: false, after: true, text: decoded ? text.slice(0, -5) + '&' : text};
            receipts.push({html, expected, actual});
        }
        const newlineHtml = '<textarea id=f>\n\n&lt;b&gt;</TEXTAREA ><div id=after></div>';
        const newlineActual = await page.evaluate(source => {
            const doc = new DOMParser().parseFromString(source, 'text/html');
            return doc.getElementById('f').textContent;
        }, newlineHtml);
        receipts.push({html: newlineHtml, expected: '\n<b>', actual: newlineActual});
        fs.writeFileSync(process.argv[2], JSON.stringify({browser: await browser.version(), receipts}, null, 2));
        for (const row of receipts) assert.deepEqual(row.actual, row.expected, row.html);
        console.log(`${receipts.length} linked stylesheet cases passed`);
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
