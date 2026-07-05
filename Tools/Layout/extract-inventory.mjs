import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const REPO = path.resolve(__dirname, '..', '..');

const HTML = path.join(REPO, 'Assets', 'UI', 'inventory.html');
const VIEWPORT_W = parseInt(process.argv[2] || '1600', 10);
const VIEWPORT_H = parseInt(process.argv[3] || '785', 10);

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

async function main() {
    const browser = await puppeteer.launch({
        headless: true,
        executablePath: CHROME,
        defaultViewport: { width: VIEWPORT_W, height: VIEWPORT_H },
        args: ['--hide-scrollbars', '--no-sandbox'],
    });
    try {
        const page = await browser.newPage();
        await page.setViewport({ width: VIEWPORT_W, height: VIEWPORT_H });
        await page.goto(pathToFileURL(HTML).href, { waitUntil: 'networkidle0', timeout: 30000 });
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        const elements = await page.evaluate(() => {
            const out = [];
            function walk(el, depth) {
                const tag = el.tagName;
                if (tag === 'HEAD') return;
                const isWrapper = tag === 'HTML' || tag === 'BODY';
                if (!isWrapper) {
                    const cs = getComputedStyle(el);
                    if (cs.display !== 'none') {
                        const r = el.getBoundingClientRect();
                        out.push({
                            i: out.length, depth,
                            tag: tag.toLowerCase(),
                            id: el.id || '',
                            cls: (typeof el.className === 'string' ? el.className : '') || '',
                            x: Math.round(r.x * 100) / 100,
                            y: Math.round(r.y * 100) / 100,
                            w: Math.round(r.width * 100) / 100,
                            h: Math.round(r.height * 100) / 100,
                            display: cs.display,
                            position: cs.position,
                        });
                    }
                }
                for (const c of el.children) walk(c, isWrapper ? depth : depth + 1);
            }
            walk(document.documentElement, 0);
            return out;
        });
        const outPath = HTML + '.chrome-layout.json';
        fs.writeFileSync(outPath, JSON.stringify({ source: path.basename(HTML), width: VIEWPORT_W, height: VIEWPORT_H, count: elements.length, elements }, null, 2));
        console.log(`Wrote ${outPath} (${elements.length} elements)`);
    } finally { await browser.close(); }
}
main().catch(err => { console.error(err && err.stack || err); process.exit(1); });
