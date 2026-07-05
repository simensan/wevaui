// Chrome layout extractor: loads an HTML snippet (fragment or full doc) in
// headless Chrome, walks the body subtree in DOM order, and emits one record
// per element with getBoundingClientRect() coordinates. Output is keyed by
// document-order index so it can be aligned 1:1 with Unity's Box tree walk.
//
// Usage:
//   node Tools/Layout/extract-chrome-layout.mjs <htmlPath> [width=800] [height=600]
//
// Behavior:
//   - If <htmlPath> contains "<html" or "<!doctype", it's loaded as-is.
//   - Otherwise it's treated as a fragment, wrapped with <!doctype html><html>
//     <head><style>...</style></head><body>FRAGMENT</body></html>, where the
//     style is the sibling .css file (if present).
//   - For wrapped fragments we point Chrome at a temp file in the same
//     directory so any relative `url(...)` references still resolve.
//   - Writes JSON to <htmlPath>.chrome-layout.json:
//     {
//       width, height, count,
//       elements: [
//         { i, tag, id, cls, depth, x, y, w, h, display, position, text }
//       ]
//     }
//
// We intentionally drop the wrapper html/head/body from the dump because the
// snippets are fragments — Unity layouts the fragment elements as the
// top-level boxes under an implicit document root.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const DEFAULT_CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

async function main() {
    const argv = process.argv.slice(2);
    if (argv.length < 1) {
        console.error('Usage: node extract-chrome-layout.mjs <htmlPath> [width=800] [height=600]');
        process.exit(2);
    }
    const htmlPath = path.resolve(argv[0]);
    const width = parseInt(argv[1] || '800', 10);
    const height = parseInt(argv[2] || '600', 10);

    if (!fs.existsSync(htmlPath)) {
        console.error('File not found: ' + htmlPath);
        process.exit(2);
    }

    let html = fs.readFileSync(htmlPath, 'utf8');
    const cssPath = htmlPath.replace(/\.html$/i, '.css');
    let css = '';
    if (fs.existsSync(cssPath)) {
        css = fs.readFileSync(cssPath, 'utf8');
    }

    const isFragment = !/<\s*html[\s>]/i.test(html) && !/<!doctype/i.test(html);
    let loadPath = htmlPath;
    let tempPath = null;
    // W1 font determinism: the reference must measure with the SAME face the
    // engine bundles (Inter as Weva-Default*.ttf), not whatever sans-serif
    // the machine maps to (Arial/Segoe, line-height ~1.15 vs Inter's 1.21).
    // Register the bundled files via @font-face under both the "Inter" name
    // and as the default body family, so snippets that say nothing get Inter
    // and snippets that name Inter in their stacks resolve to the identical
    // file. Snippet CSS still overrides (the body rule has zero specificity
    // advantage and comes first).
    const fontsDir = path.resolve(__dirname, '..', '..',
        'Packages', 'com.wevaui', 'Runtime', 'Text', 'Sdf', 'Fonts');
    const fontFaceCss = (() => {
        const reg = path.join(fontsDir, 'Weva-Default.ttf');
        const bold = path.join(fontsDir, 'Weva-Default-Bold.ttf');
        const ital = path.join(fontsDir, 'Weva-Default-Italic.ttf');
        if (!fs.existsSync(reg)) return '';
        const u = p => pathToFileURL(p).href;
        let s = `@font-face{font-family:'Inter';src:url('${u(reg)}');font-weight:100 600;font-style:normal}`;
        if (fs.existsSync(bold)) s += `@font-face{font-family:'Inter';src:url('${u(bold)}');font-weight:700 900;font-style:normal}`;
        if (fs.existsSync(ital)) s += `@font-face{font-family:'Inter';src:url('${u(ital)}');font-weight:100 600;font-style:italic}`;
        s += `body{font-family:'Inter',sans-serif}`;
        return s;
    })();
    if (isFragment) {
        // Inline CSS so the temp file is self-contained and doesn't have to
        // chase external resources besides whatever the fragment itself
        // references (e.g. background-image url()). Use UTF-8 explicitly so
        // emoji glyphs in snippets render with the same font selection Chrome
        // would pick for the original file.
        // Zero body's UA margin so the fragment lays out at (0,0) — matches
        // Unity's HtmlParser behavior which doesn't synthesize <body> around
        // fragments. See capture-all-chrome-layouts.mjs for the same rationale.
        const wrapped =
            '<!doctype html>\n' +
            '<html><head><meta charset="utf-8"><style>body{margin:0}</style>' +
            '<style>' + fontFaceCss + '</style>' +
            '<style>' + css + '</style></head>' +
            '<body>' + html + '</body></html>\n';
        tempPath = htmlPath + '.tmp.chrome-extract.html';
        fs.writeFileSync(tempPath, wrapped, 'utf8');
        loadPath = tempPath;
    }

    const executablePath = process.env.PUPPETEER_EXECUTABLE_PATH ||
        (fs.existsSync(DEFAULT_CHROME) ? DEFAULT_CHROME : undefined);
    const browser = await puppeteer.launch({
        headless: true,
        executablePath,
        defaultViewport: { width, height },
        args: ['--hide-scrollbars', '--no-sandbox'],
    });
    let elements;
    try {
        const page = await browser.newPage();
        await page.setViewport({ width, height });
        const fileUrl = pathToFileURL(loadPath).href;
        await page.goto(fileUrl, { waitUntil: 'networkidle0', timeout: 30000 });
        // Layout dumps compare engine layout boxes, not transient visual
        // animation transforms. getBoundingClientRect() includes active
        // CSS animations/transitions, while Unity's headless layout dump
        // records the stable pre-transform box tree. Freeze motion before
        // measuring so animated samples do not produce false layout diffs.
        await page.addStyleTag({
            content: '*,*::before,*::after{animation:none!important;transition:none!important;}'
        });
        // Wait for @font-face loads (the bundled Inter registers async even
        // from file://) THEN a frame so layout settled with final metrics.
        await page.evaluate(() => document.fonts.ready);
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));

        elements = await page.evaluate(() => {
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
                            i: out.length,
                            depth,
                            tag: tag.toLowerCase(),
                            id: el.id || '',
                            cls: (typeof el.className === 'string' ? el.className : '') || '',
                            x: Math.round(r.x * 100) / 100,
                            y: Math.round(r.y * 100) / 100,
                            w: Math.round(r.width * 100) / 100,
                            h: Math.round(r.height * 100) / 100,
                            display: cs.display,
                            position: cs.position,
                            text: el.children.length === 0 ? (el.textContent || '').trim().slice(0, 80) : '',
                        });
                    }
                }
                for (const c of el.children) walk(c, isWrapper ? depth : depth + 1);
            }
            walk(document.documentElement, 0);
            return out;
        });
    } finally {
        await browser.close();
        if (tempPath && fs.existsSync(tempPath)) {
            try { fs.unlinkSync(tempPath); } catch { /* ignore */ }
        }
    }

    const outPath = htmlPath + '.chrome-layout.json';
    const payload = {
        source: path.basename(htmlPath),
        width, height,
        count: elements.length,
        elements,
    };
    fs.writeFileSync(outPath, JSON.stringify(payload, null, 2));
    console.log(`Wrote ${elements.length} elements -> ${outPath}`);
}

main().catch(err => {
    console.error(err && err.stack || err);
    process.exit(1);
});
