// Compare numeric editing against the retained Chrome fixture.
// node check_number_editing_chrome.cjs <chrome executable> [receipt.json]
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const puppeteer = require('puppeteer');
(async () => {
    const fixture = JSON.parse(fs.readFileSync(path.join(__dirname,
        '../../hosts/godot/project/form_validation_numbers.json'), 'utf8'));
    const browser = await puppeteer.launch({headless: true, executablePath: process.argv[2]});
    const results = [];
    try {
        const page = await browser.newPage();
        const cdp = await page.createCDPSession();
        for (const row of fixture.rows) {
            await page.setContent('<input id=c type=number>');
            await page.focus('#c');
            await page.keyboard.type(row.initial);
            await page.keyboard.press(row.place === 'end' ? 'End' : 'Home');
            if (row.place === 'all') {
                await page.keyboard.down('Control');
                await page.keyboard.press('a');
                await page.keyboard.up('Control');
            } else if (row.place === 'middle') {
                await page.keyboard.press('ArrowRight');
                await page.keyboard.press('ArrowRight');
            }
            await cdp.send('Input.insertText', {text: row.text});
            const tree = await cdp.send('DOM.getDocument', {depth: -1, pierce: true});
            let raw = '';
            function walk(node) {
                if (node.nodeName === '#text') raw += node.nodeValue;
                for (const child of [...(node.children || []), ...(node.shadowRoots || [])]) walk(child);
            }
            walk(tree.root);
            const actual = {...await page.evaluate(() => ({value: c.value, bad: c.validity.badInput})), raw};
            assert.deepEqual(actual, {value: row.value, bad: row.bad, raw: row.raw}, JSON.stringify(row));
            results.push({...row, ...actual});
        }
        const receipt = {browser: await browser.version(), passed: true, cases: results.length, rows: results};
        if (process.argv[3]) fs.writeFileSync(process.argv[3], JSON.stringify(receipt, null, 2) + '\n');
        console.log(`${results.length} numeric editing cases passed`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
