// Clipping alone does not establish a scroll container for sticky/snap.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {launch} = require('./chrome_test_browser.cjs');
(async () => {
    const browser = await launch({headless:true});
    try {
        const page = await browser.newPage();
        await page.setViewport({width:400,height:300});
        const rows = [];
        for (const overflow of ['visible','clip','hidden','auto']) {
            await page.setContent(`<style>html,body{margin:0}#s{overflow:auto;height:100px;width:200px}
                #clip{overflow:${overflow};height:300px}#pre{height:50px}#h{position:sticky;top:0;height:20px;background:red}
                #tail{height:400px}</style><div id=s><div id=clip><div id=pre></div><div id=h></div></div><div id=tail></div></div>`);
            const sticky = await page.evaluate(() => { s.scrollTop=100; return h.getBoundingClientRect().top; });
            assert.equal(sticky, overflow === 'visible' || overflow === 'clip' ? 0 : -50, `${overflow} sticky`);
            await page.setContent(`<style>html,body{margin:0}#s{overflow:auto;height:100px;width:200px;scroll-snap-type:y mandatory}
                #clip{overflow:${overflow};height:400px}.row{height:100px;scroll-snap-align:start}</style>
                <div id=s><div id=clip><div class=row></div><div class=row></div><div class=row></div><div class=row></div></div></div>`);
            const snap = await page.evaluate(() => { s.scrollTop=130; return s.scrollTop; });
            assert.equal(snap, overflow === 'visible' || overflow === 'clip' ? 100 : 130, `${overflow} snap`);
            await page.setContent(`<style>html,body{margin:0}#s{overflow:${overflow};height:100px;width:200px}#t{height:400px}</style><div id=s><div id=t></div></div>`);
            const programmatic = await page.evaluate(() => { s.scrollTop=100; return s.scrollTop; });
            assert.equal(programmatic, overflow === 'visible' || overflow === 'clip' ? 0 : 100, `${overflow} scrollTop`);
            rows.push({overflow,sticky,snap,programmatic});
        }
        fs.writeFileSync(process.argv[2], JSON.stringify({browser:await browser.version(), rows}, null, 2));
        console.log('12 clipping and scroll-container checks passed');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
