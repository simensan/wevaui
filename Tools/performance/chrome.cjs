// Synchronous style/layout baseline; deliberately does not claim paint completion.
const fs = require('node:fs');
const {launch} = require('../oracle/chrome_test_browser.cjs');

(async () => {
    const [manifestPath, outputPath] = process.argv.slice(2);
    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    const browser = await launch({headless: true});
    try {
        const page = await browser.newPage();
        await page.setViewport({width: 1280, height: 720, deviceScaleFactor: 1});
        const results = [];
        for (const job of manifest.jobs) {
            const html = fs.readFileSync(job.html, 'utf8');
            const css = fs.readFileSync(job.css, 'utf8');
            await page.goto('about:blank');
            const result = await page.evaluate(({html, css, targetSelector, passes}) => {
                const source = html.replace('<head>', `<head><style>${css}</style>`);
                const install = () => {
                    document.open(); document.write(source); document.close();
                };
                install();
                document.body.getBoundingClientRect();
                const target = document.querySelector(targetSelector);
                if (!target) throw new Error('Missing mutation target');
                const geometry = [...document.querySelectorAll('div')].map(e => {
                    const r = e.getBoundingClientRect();
                    return {x: r.x, y: r.y, w: r.width, h: r.height};
                });
                let total = 0, best = Infinity, checksum = 0;
                const styles = ['padding-left:11px', 'padding-left:12px'];
                for (let i = 0; i < passes; ++i) {
                    const start = performance.now();
                    target.setAttribute('style', styles[i & 1]);
                    const rect = target.getBoundingClientRect();
                    const elapsed = performance.now() - start;
                    total += elapsed; best = Math.min(best, elapsed);
                    checksum += rect.height;
                }
                if (!(checksum > 0)) throw new Error('Empty layout');
                return {mean_ms: total / passes, best_ms: best, checksum, geometry,
                    elements: document.querySelectorAll('*').length};
            }, {html, css, targetSelector: job.target, passes: manifest.passes});
            results.push({...job, ...result});
        }
        fs.writeFileSync(outputPath, JSON.stringify({browser: await browser.version(),
            user_agent: await page.evaluate(() => navigator.userAgent), results}, null, 2));
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
