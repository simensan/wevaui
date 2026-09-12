// Browser evidence for test_c_abi_ime.cpp. CDP drives Chrome's composition
// implementation; this does not stand in for testing an OS input method.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');

(async () => {
    const browser = await puppeteer.launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const check = (actual, expected) => { assert.deepEqual(actual, expected); ++checks; };
    try {
        const page = await browser.newPage();
        const cdp = await page.createCDPSession();
        const preview = (text, start = text.length, end = start) => cdp.send('Input.imeSetComposition',
            {text, selectionStart: start, selectionEnd: end});
        const value = () => page.$eval('#field', field => field.value);
        const events = () => page.evaluate(() => window.events.splice(0));
        const undo = async () => {
            await page.keyboard.down('Control'); await page.keyboard.press('z'); await page.keyboard.up('Control');
        };
        for (const markup of ['<input id=field value=abcd>', '<textarea id=field>abcd</textarea>']) {
            const reset = async () => {
                await page.setContent(markup + '<button id=other>Other</button>');
                await page.evaluate(() => {
                    const field = document.querySelector('#field');
                    window.events = [];
                    for (const kind of ['compositionstart', 'compositionupdate', 'compositionend', 'input', 'change'])
                        field.addEventListener(kind, event => events.push([kind, event.data ?? null]));
                    field.focus(); field.setSelectionRange(1, 3);
                });
            };
            await reset();
            await preview('に');
            check(await value(), 'aにd');
            check(await events(), [['compositionstart', 'bc'], ['compositionupdate', 'に'], ['input', 'に']]);
            await preview('日本', 0, 2);
            check(await value(), 'a日本d');
            check(await page.$eval('#field', f => [f.selectionStart, f.selectionEnd]), [1, 3]);
            check(await events(), [['compositionupdate', '日本'], ['input', '日本']]);
            await cdp.send('Input.insertText', {text: '日本語'});
            check(await value(), 'a日本語d');
            check(await events(), [['compositionupdate', '日本語'], ['input', '日本語'], ['compositionend', '日本語']]);
            await undo(); check(await value(), 'abcd');
            await page.keyboard.down('Control'); await page.keyboard.press('y'); await page.keyboard.up('Control');
            check(await value(), 'a日本語d');

            await reset(); await preview('日本'); await events();
            await preview('');
            check(await value(), 'ad');
            check(await events(), [['compositionupdate', ''], ['input', null], ['compositionend', '']]);
            await undo(); check(await value(), 'abcd');

            for (const action of ['blur', 'disable']) {
                await reset(); await preview('日本'); await events();
                if (action === 'blur') await page.focus('#other');
                else await page.$eval('#field', f => { f.disabled = true; });
                check(await value(), 'a日本d');
                check(await events(), [['compositionend', '日本'], ['change', null]]);
            }

            await reset();
            await page.$eval('#field', f => { f.value = ''; });
            await page.keyboard.type('prior');
            await preview('preview'); await preview('');
            check(await value(), 'prior');
            await undo(); check(await value(), '');

            await reset();
            const longText = '日本語の確定された文字列😀';
            await preview('日本語の長い候補😀'); await events();
            await cdp.send('Input.insertText', {text: longText});
            check(await value(), 'a' + longText + 'd');
            check(await events(), [['compositionupdate', longText], ['input', longText], ['compositionend', longText]]);
        }
        console.log(`Chrome IME: ${checks} checks, 0 failures (${await browser.version()})`);
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
