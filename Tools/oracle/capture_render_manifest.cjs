// Fresh browser images for the Unity visual-review runner. No tracked captures
// are overwritten: each manifest row supplies its own output PNG.
const fs = require('fs');
const path = require('path');
const { launch } = require('./chrome_test_browser.cjs');

(async () => {
    if (process.argv.length !== 4)
        throw new Error('Usage: node capture_render_manifest.cjs <manifest.json> <receipt.json>');
    const rows = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
    if (!Array.isArray(rows) || rows.length === 0) throw new Error('The capture manifest is empty');
    const { captureOne } = await import('../Layout/capture-all-chrome-layouts.mjs');
    const browser = await launch({ headless: true, args: ['--hide-scrollbars'] });
    try {
        const captures = [];
        for (const row of rows) {
            if (!row.png || !(row.width > 0) || !(row.height > 0))
                throw new Error('Each row needs png, html and a positive viewport');
            fs.mkdirSync(path.dirname(row.png), { recursive: true });
            const result = await captureOne(browser, row, {
                metrics: 'inter', screenshot: true, noLayout: true, screenshotPath: row.png,
            });
            if (!result.ok) throw new Error(`${row.html}: ${result.error}`);
            captures.push({ ...row, elements: result.count });
            console.log(`captured ${path.basename(row.html)}`);
        }
        fs.writeFileSync(process.argv[3], JSON.stringify({ browser: await browser.version(), captures }, null, 2) + '\n');
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
