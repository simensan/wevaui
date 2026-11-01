// Independent expected pixels for the core's baseline/progressive/gray/CMYK matrix.
const {launch} = require('./chrome_test_browser.cjs');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
    const browser = await launch({headless: true, executablePath: process.argv[2]});
    try {
        const page = await browser.newPage();
        const source = fs.readFileSync(path.join(__dirname, '../../libweva/tests/jpeg_fixtures.inc'), 'utf8');
        const rows = [];
        for (const [name, r, g, b] of [
            ['baseline', 64, 128, 192], ['progressive', 64, 128, 192],
            ['gray', 96, 96, 96], ['cmyk', 167, 111, 55],
        ]) {
            const match = source.match(new RegExp('k_jpeg_' + name + '\\[\\] = \\{([^}]+)\\}'));
            assert.ok(match, name);
            const bytes = Buffer.from(match[1].split(',').map(v => v.trim()).filter(Boolean).map(Number));
            const observed = await page.evaluate(async url => {
                const image = new Image(); image.src = url; await image.decode();
                const canvas = document.createElement('canvas');
                canvas.width = image.width; canvas.height = image.height;
                const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
                const rgba = Array.from(ctx.getImageData(0, 0, image.width, image.height).data);
                return {width: image.width, height: image.height, pixel: rgba.slice(0, 4),
                    uniform: rgba.every((v, i) => v === rgba[i % 4])};
            }, 'data:image/jpeg;base64,' + bytes.toString('base64'));
            assert.deepEqual(observed, {width: 16, height: 8, pixel: [r, g, b, 255], uniform: true});
            rows.push({name, ...observed});
        }
        console.log(JSON.stringify({browser: await browser.version(), rows, passed: true}));
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
