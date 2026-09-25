// Browser counterparts of the transformed hit and text-caret ABI regressions.
const {launch} = require('./chrome_test_browser.cjs');
const assert = require('node:assert/strict');

(async () => {
    const browser = await launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const check = (actual, expected) => { assert.deepEqual(actual, expected); ++checks; };
    try {
        const page = await browser.newPage();
        await page.setViewport({width: 400, height: 300});
        await page.setContent('<style>html,body{margin:0}div{width:100px;height:60px}' +
            'span{display:block;width:40px;height:20px}</style>' +
            '<div id=a><span id=inner>x</span></div><div id=b>y</div>');
        const hit = (x, y) => page.evaluate((x, y) => document.elementFromPoint(x, y)?.id, x, y);
        for (const transform of ['translate(120px,80px)', 'translate(30vw,80px)']) {
            await page.$eval('#a', (a, t) => a.style.transform = t, transform);
            check(await hit(130, 90), 'inner');
            check(await hit(10, 10) === 'inner', false);
        }
        await page.$eval('#inner', e => e.style.cssText = 'transform-origin:0 0;transform:scale(2)');
        check(await hit(190, 110), 'inner');
        await page.$eval('#a', e => e.style.cssText = 'transform-origin:0 0;transform:translate(120px,80px) rotate(90deg)');
        check(await hit(90, 150), 'inner');
        check(await hit(130, 90) === 'inner', false);
        await page.$eval('#a', e => e.style.transform = 'scale(0)');
        check(await hit(10, 10) === 'inner', false);
        await page.$eval('#a', e => e.style.cssText = 'transform:translate(120px,80px);overflow:hidden;width:30px');
        check(await hit(140, 90), 'inner');
        check(await hit(160, 90) === 'inner', false);
        for (const tag of ['input', 'textarea']) {
            await page.setContent(`<style>body{margin:0}#f{display:block;box-sizing:border-box;width:100px;height:30px;padding:0;border:0;font:20px monospace;transform-origin:0 0;transform:translate(120px,40px) scale(2)}</style><${tag} id=f></${tag}>`);
            const advance = await page.$eval('#f', f => {
                f.value = 'abcdef';
                const canvas = document.createElement('canvas');
                const ctx = canvas.getContext('2d');
                ctx.font = getComputedStyle(f).font;
                return ctx.measureText('ab').width;
            });
            await page.mouse.click(120 + advance * 2, 55);
            check(await page.$eval('#f', f => [f.selectionStart, f.selectionEnd]), [2, 2]);
        }
        console.log(`Chrome transformed input: ${checks} checks passed`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
