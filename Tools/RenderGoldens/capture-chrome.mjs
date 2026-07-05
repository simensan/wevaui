// W6 GPU golden harness — Chrome reference captures.
// Renders every Assets/UI sample in headless Chrome at the SAME viewport the
// Unity capture test uses (1280x720, deviceScaleFactor 1) with animations
// frozen, into Tools/RenderGoldens/chrome/<name>.png.
//
// Usage:  node Tools/RenderGoldens/capture-chrome.mjs [sampleName ...]
//         (no args = all samples in Assets/UI)
//
// compare.py joins these against Tools/RenderGoldens/unity/*.png.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '..', '..');
const UI = path.join(REPO, 'Assets', 'UI');
const OUT = path.join(__dirname, 'chrome');
const W = 1280, H = 720;

function chromePath() {
    if (process.env.CHROME_PATH && fs.existsSync(process.env.CHROME_PATH)) return process.env.CHROME_PATH;
    const win = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
    return fs.existsSync(win) ? win : undefined;
}

async function main() {
    fs.mkdirSync(OUT, { recursive: true });
    const wanted = process.argv.slice(2);
    const samples = fs.readdirSync(UI)
        .filter(f => f.endsWith('.html'))
        .map(f => f.replace(/\.html$/, ''))
        .filter(n => wanted.length === 0 || wanted.includes(n))
        .sort();

    const browser = await puppeteer.launch({
        headless: true,
        executablePath: chromePath(),
        defaultViewport: { width: W, height: H, deviceScaleFactor: 1 },
        args: ['--hide-scrollbars', '--no-sandbox', '--force-color-profile=srgb'],
    });
    try {
        for (const name of samples) {
            const htmlPath = path.join(UI, name + '.html');
            const raw = fs.readFileSync(htmlPath, 'utf8');
            const isFragment = !/<\s*html[\s>]/i.test(raw) && !/<!doctype/i.test(raw);
            let loadPath = htmlPath, tempPath = null;
            if (isFragment) {
                const cssPath = path.join(UI, name + '.css');
                const css = fs.existsSync(cssPath) ? fs.readFileSync(cssPath, 'utf8') : '';
                tempPath = htmlPath + '.tmp.golden.html';
                fs.writeFileSync(tempPath,
                    '<!doctype html><html><head><meta charset="utf-8"><style>body{margin:0}</style><style>'
                    + css + '</style></head><body>' + raw + '</body></html>', 'utf8');
                loadPath = tempPath;
            }
            const page = await browser.newPage();
            try {
                await page.goto(pathToFileURL(loadPath).href, { waitUntil: 'networkidle0', timeout: 30000 });
                // Freeze motion at t=0 so animated samples are comparable —
                // the Unity capture settles a fixed frame count from load,
                // so neither side is mid-flight by design intent; residual
                // animation phase differences are flagged per-sample in
                // compare-config.json instead of chased here.
                await page.addStyleTag({
                    content: '*,*::before,*::after{animation-play-state:paused!important;animation-delay:-0.0001s!important;transition:none!important;caret-color:transparent!important}'
                });
                await page.evaluate(() => document.fonts.ready);
                await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
                await page.screenshot({ path: path.join(OUT, name + '.png') });
                console.log('chrome ref:', name);
            } finally {
                await page.close();
                if (tempPath) { try { fs.unlinkSync(tempPath); } catch { } }
            }
        }
    } finally {
        await browser.close();
    }
}

main().catch(e => { console.error(e); process.exit(1); });
