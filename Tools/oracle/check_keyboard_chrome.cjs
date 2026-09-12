// Browser evidence for test_c_abi_keyboard.cpp. Uses the repository's
// Puppeteer dependency; pass a Chrome executable as the optional argument.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');

(async () => {
    const browser = await puppeteer.launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const check = (got, want) => { assert.deepEqual(got, want); ++checks; };
    try {
        const page = await browser.newPage();
        const html = '<button id=b>Go</button><input id=i type=button>' +
            '<input id=c type=checkbox><input id=r type=radio>' +
            '<details id=d><summary id=s>More</summary></details><input id=other>';
        const reset = async () => {
            await page.setContent(html);
            await page.evaluate(() => {
                window.events = [];
                for (const kind of ['click', 'input', 'change'])
                    document.addEventListener(kind, e => events.push(kind + ':' + e.target.id));
            });
        };
        const drain = () => page.evaluate(() => events.splice(0));
        for (const id of ['b', 'i', 's']) {
            await reset();
            await page.focus('#' + id);
            for (let repeat = 0; repeat < 2; ++repeat) {
                await page.keyboard.down('Enter');
                check(await drain(), ['click:' + id]);
            }
            await page.keyboard.up('Enter');
            check(await drain(), []);
            for (let repeat = 0; repeat < 2; ++repeat) {
                await page.keyboard.down('Space');
                check(await drain(), []);
            }
            await page.keyboard.up('Space');
            check(await drain(), ['click:' + id]);
        }
        for (const id of ['c', 'r']) {
            await reset(); await page.focus('#' + id);
            await page.keyboard.down('Space'); check(await drain(), []);
            await page.keyboard.up('Space');
            check(await drain(), ['click:' + id, 'input:' + id, 'change:' + id]);
            check(await page.$eval('#' + id, e => e.checked), true);
        }
        await reset(); await page.focus('#b'); await page.keyboard.down('Space');
        await page.focus('#other'); await page.keyboard.up('Space'); check(await drain(), []);
        await page.setContent('<details id=d><summary><button id=b>Action</button><span id=t>More</span></summary></details>');
        await page.focus('#b'); await page.keyboard.press('Enter');
        check(await page.$eval('#d', e => e.open), false);
        await page.keyboard.press('Space');
        check(await page.$eval('#d', e => e.open), false);
        await page.click('#t');
        check(await page.$eval('#d', e => e.open), true);

        const forms = [
            ['<form id=f><input id=field><button id=go>Go</button></form>', ['click:go','submit:f']],
            ['<form id=f><input id=field><button disabled>Off</button><button>Next</button></form>', []],
            ['<form id=f><input id=field></form>', ['submit:f']],
            ['<form id=f><input id=field><input></form>', []],
            ['<form id=f><input id=field><input disabled></form>', []],
            ['<form id=f><input id=field type=checkbox></form>', []],
            ['<form id=f><input id=field type=radio><button id=go>Go</button></form>', ['click:go','submit:f']],
            ['<input id=field form=f><button id=go form=f>Go</button><form id=f></form>', ['click:go','submit:f']],
            ['<form id=f><input id=field form=missing><button>Go</button></form>', []]
        ];
        for (const [markup, expected] of forms) {
            await page.setContent(markup);
            await page.evaluate(() => {
                window.events = [];
                for (const kind of ['click','submit']) document.addEventListener(kind, e => {
                    events.push(kind + ':' + e.target.id);
                    if (kind === 'submit') e.preventDefault();
                });
            });
            await page.focus('#field'); await page.keyboard.press('Enter');
            check(await drain(), expected);
            check(await page.evaluate(() => document.activeElement.id), 'field');
        }
        const radios = '<button id=before>B</button><form><input id=a type=radio name=g>' +
            '<input type=radio name=g disabled><input id=c type=radio name=g></form>' +
            '<form><input id=other type=radio name=g checked></form><button id=after>A</button>';
        await page.setContent(radios); await page.focus('#before');
        for (const id of ['a','other','after']) {
            await page.keyboard.press('Tab');
            check(await page.evaluate(() => document.activeElement.id), id);
        }
        await page.keyboard.down('Shift');
        for (const id of ['other','a']) {
            await page.keyboard.press('Tab');
            check(await page.evaluate(() => document.activeElement.id), id);
        }
        await page.keyboard.up('Shift'); await page.keyboard.press('ArrowRight');
        check(await page.evaluate(() => document.activeElement.id), 'c');
        check(await page.evaluate(() => [...document.querySelectorAll(':checked')].map(e => e.id)), ['c','other']);
        await page.setContent(radios); await page.focus('#after');
        await page.keyboard.down('Shift'); await page.keyboard.press('Tab'); await page.keyboard.press('Tab');
        await page.keyboard.up('Shift');
        check(await page.evaluate(() => document.activeElement.id), 'c');

        const keys = ['ArrowRight', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'];
        const ranges = [
            ['', '50', ['51','49','51','49','0','100','60','40']],
            ['min=0 max=10 step=0.3 value=5.1', '5.1', ['5.4','4.8','5.4','4.8','0','9.9','6','4.2']],
            ['min=2 max=7 step=any value=4', '4', ['4.05','3.95','4.05','3.95','2','7','4.5','3.5']],
            ['min=10 max=5 value=8', '10', ['10','10','10','10','10','10','10','10']],
            ['min=0 max=10 step=3 value=5', '6', ['9','3','9','3','0','9','9','3']]
        ];
        for (const [attributes, before, values] of ranges) for (let i = 0; i < keys.length; ++i) {
            await page.setContent('<input id=r type=range ' + attributes + '>');
            await page.focus('#r');
            check(await page.$eval('#r', e => e.value), before);
            await page.keyboard.press(keys[i]);
            check(await page.$eval('#r', e => e.value), values[i]);
        }
        console.log(`${await browser.version()}: ${checks} keyboard oracle checks, 0 failures`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
