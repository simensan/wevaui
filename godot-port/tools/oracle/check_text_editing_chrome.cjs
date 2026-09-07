// Browser counterparts of test_c_abi_text_editing.cpp. Selection offsets in
// Chrome are UTF-16 code units; the core API uses UTF-8 bytes.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');

(async () => {
    const browser = await puppeteer.launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const check = (got, want) => { assert.deepEqual(got, want); ++checks; };
    try {
        const page = await browser.newPage();
        await page.setContent('<input id=f><textarea id=t></textarea>');
        const cases = [
            ['a\u0301', 'a'], ['a\u0301\u0327', 'a\u0301'], ['각', '가'],
            ['😀', ''], ['界', ''], ['👨‍👩‍👧‍👦', ''], ['👩🏽‍💻', ''],
            ['🇸🇪', ''], ['🇸🇪🇫', '🇸🇪'], ['🇸🇪🇫🇷', '🇸🇪'],
            ['👍🏽', ''], ['a🏽', 'a'], ['1️⃣', ''], ['#⃣', ''], ['a⃣', 'a'],
            ['❤️', ''], ['a\uFE0F', ''], ['a\u0301\uFE0F', 'a\u0301'],
            ['a\uFE0F\uFE0F', 'a\uFE0F'], ['a‍😀', 'a‍'],
            ['🏴\u{E0067}\u{E0062}\u{E0065}\u{E006E}\u{E0067}\u{E007F}', ''],
        ];
        for (const selector of ['#f', '#t']) {
            for (const [value, remaining] of cases) {
                await page.$eval(selector, (field, value) => {
                    field.value = value; field.focus(); field.setSelectionRange(value.length, value.length);
                }, value);
                await page.keyboard.press('Backspace');
                check(await page.$eval(selector, f => f.value), remaining);
            }
            for (const value of ['a\u0301', '각', '👨‍👩‍👧‍👦', '👩🏽‍💻', '🇸🇪', '1️⃣']) {
                await page.$eval(selector, (f, value) => {
                    f.value = value; f.focus(); f.setSelectionRange(value.length, value.length);
                }, value);
                await page.keyboard.press('ArrowLeft');
                check(await page.$eval(selector, f => f.selectionStart), 0);
                await page.keyboard.press('ArrowRight');
                check(await page.$eval(selector, f => f.selectionStart), value.length);
                await page.keyboard.press('Home'); await page.keyboard.press('Delete');
                check(await page.$eval(selector, f => f.value), '');
            }
        }
        await page.setContent('<input id=f type=password style="width:1px;padding:0;border:0;font:40px monospace">');
        const width = async value => page.$eval('#f', (f, value) => {f.value=value; return f.scrollWidth;}, value);
        const bullet = await width('a');
        for (const value of ['😀', '界', 'a\u0301', '👨‍👩‍👧‍👦', '🇸🇪', '각']) check(await width(value), bullet);
        check(await width('😀界a'), bullet * 3);
        // Blink editing predates UAX #29's GB9c Indic conjunct rule. Record
        // that distinction explicitly instead of calling all segmentation equal.
        check(await width('क्‍ष'), bullet);
        await page.$eval('#f', f => {f.type='text'; f.value='क्‍ष'; f.focus(); f.setSelectionRange(4,4);});
        await page.keyboard.press('ArrowLeft');
        check(await page.$eval('#f', f => f.selectionStart), 3);
        await page.keyboard.press('Home'); await page.keyboard.press('Delete');
        check(await page.$eval('#f', f => f.value), 'ष');
        await page.setContent('<input id=f style="width:100px;padding:0;border:0;font:20px monospace">');
        await page.$eval('#f', f => {f.value='abcdefghijklmnopqrstuvwxyz'; f.focus(); f.setSelectionRange(0,0);});
        await page.keyboard.press('End');
        check(await page.$eval('#f', f => f.scrollLeft > 0), true);
        await page.keyboard.press('Home');
        check(await page.$eval('#f', f => f.scrollLeft), 0);
        await page.keyboard.press('End'); await page.$eval('#f', f => f.blur());
        check(await page.$eval('#f', f => f.scrollLeft), 0);
        console.log(`Chrome ${await browser.version()}: ${checks} text editing checks, 0 failures`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
