// Browser control for the empty-box live-resize cases in font_size_tests.gd.
const puppeteer = require('puppeteer');
const assert = require('node:assert/strict');
const fs = require('node:fs');

(async () => {
    const browser = await puppeteer.launch({headless: true, executablePath: process.argv[2]});
    let checks = 0;
    const rows = [];
    try {
        const resized = await browser.newPage(), fresh = await browser.newPage();
        const cases = [
            ['vw', '10vw', (w, h) => w * .1], ['vh', '10vh', (w, h) => h * .1],
            ['vmin', '10vmin', (w, h) => Math.min(w, h) * .1],
            ['vmax', '10vmax', (w, h) => Math.max(w, h) * .1],
            ['dvw', '10dvw', (w, h) => w * .1],
            ['calc', 'calc(1em + 2vw)', (w, h) => 16 + w * .02],
            ['clamp', 'clamp(8px,10vw,40px)', (w, h) => Math.max(8, Math.min(40, w * .1))],
            ['inherit', '150%', (w, h) => w * .15],
        ];
        const read = page => page.$eval('#sample', e => {
            const r = e.getBoundingClientRect();
            return [parseFloat(getComputedStyle(e).fontSize), r.width, r.height];
        });
        for (const [mode, value, expected] of cases) {
            const html = `<style>html,body{margin:0;font-size:16px}
                #sample{display:block;width:1em;height:1em;background:#09f;font-size:${value}}
                ${mode === 'inherit' ? '#parent{font-size:10vw}' : ''}</style>
                <div id=parent><div id=sample></div></div>`;
            await resized.setViewport({width: 100, height: 200});
            await resized.setContent(html);
            for (const [width, height] of [[100,200],[200,200],[200,400],[400,200],[100,200]]) {
                await resized.setViewport({width, height});
                await fresh.setViewport({width, height});
                await fresh.setContent(html);
                const actual = await read(resized), control = await read(fresh);
                assert.ok(actual.every(n => Math.abs(n - expected(width, height)) < .01),
                    `${mode} ${width}x${height}: ${actual}`);
                ++checks;
                assert.deepEqual(actual, control);
                ++checks;
                rows.push({mode, width, height, actual, fresh: control});
            }
        }
        if (process.argv[3]) fs.writeFileSync(process.argv[3], JSON.stringify(rows, null, 2));
        console.log(`${await browser.version()}: font-size context ${checks} checks, 0 failures`);
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
