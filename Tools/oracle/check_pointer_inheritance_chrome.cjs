// Browser evidence for the inherited-pointer matrix in test_c_abi_interaction.cpp.
const {launch} = require('./chrome_test_browser.cjs');
const assert = require('node:assert/strict');

(async () => {
    const browser = await launch({headless: true, executablePath: process.argv[2]});
    try {
        const page = await browser.newPage();
        await page.setContent('<style>html,body{margin:0}#world,#hud{position:absolute;inset:0}' +
            '#hud{pointer-events:none}#plain,#active{display:block;width:100px;height:50px}' +
            '#active{pointer-events:auto}</style><div id=world></div>' +
            '<div id=hud><div id=plain></div><button id=active></button></div>');
        await page.evaluate(() => {
            window.clicks = [];
            document.addEventListener('click', e => clicks.push(e.target.id));
        });
        const results = [];
        for (const [parent, child, expected] of [
            ['none', '', 'none'], ['none', 'auto', 'auto'],
            ['none', 'inherit', 'none'], ['none', 'initial', 'auto'],
            ['none', 'unset', 'none'], ['auto', '', 'auto'], ['none', '', 'none'],
        ]) {
            await page.evaluate(([parent, child]) => {
                document.querySelector('#hud').style.pointerEvents = parent;
                document.querySelector('#plain').style.pointerEvents = child;
            }, [parent, child]);
            const observed = await page.evaluate(() => [
                getComputedStyle(document.querySelector('#plain')).pointerEvents,
                getComputedStyle(document.querySelector('#active')).pointerEvents,
                document.elementFromPoint(25, 25).id, document.elementFromPoint(25, 75).id,
            ]);
            const target = expected === 'none' ? 'world' : 'plain';
            assert.deepEqual(observed, [expected, 'auto', target, 'active']);
            await page.mouse.click(25, 25);
            await page.mouse.click(25, 75);
            const clicks = await page.evaluate(() => window.clicks.splice(0));
            assert.deepEqual(clicks, [target, 'active']);
            results.push({parent, child, observed, clicks});
        }
        console.log(JSON.stringify({browser: await browser.version(), results, passed: true}));
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
