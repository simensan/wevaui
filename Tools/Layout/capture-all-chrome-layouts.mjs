// Drives extract-chrome-layout.mjs against every demo/snippet HTML in the
// repo. Reuses a single Chrome instance for speed (~5x faster than spawning
// one per file).
//
// Usage:
//   node Tools/Layout/capture-all-chrome-layouts.mjs
//
// Snippet sources are hard-coded to keep the script free of YAML/JSON config.
// Default viewport is 800x600 to match GoldenAssert.Match's default and the
// Unity LayoutDiffTests fixture. match3 uses its native 1280x720 viewport.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const REPO = path.resolve(__dirname, '..', '..');

const SNIPPET_DIR = path.join(REPO, 'Packages', 'com.wevaui', 'Tests', 'Runtime', 'Goldens', 'Snippets');

// W1 font determinism: load the engine's bundled Inter (Weva-Default*.ttf)
// via @font-face and default the body to it, so the Chrome reference
// measures with the SAME face the engine ships instead of the machine's
// sans-serif (Arial/Segoe, normal line-height ~1.15 vs Inter's 1.21).
// Mirrors extract-chrome-layout.mjs.
const FONTS_DIR = path.join(REPO, 'Packages', 'com.wevaui', 'Runtime', 'Text', 'Sdf', 'Fonts');
function bundledFontFaceCss() {
    const reg = path.join(FONTS_DIR, 'Weva-Default.ttf');
    const bold = path.join(FONTS_DIR, 'Weva-Default-Bold.ttf');
    const ital = path.join(FONTS_DIR, 'Weva-Default-Italic.ttf');
    if (!fs.existsSync(reg)) return '';
    const u = p => pathToFileURL(p).href;
    let s = `@font-face{font-family:'Inter';src:url('${u(reg)}');font-weight:100 600;font-style:normal}`;
    if (fs.existsSync(bold)) s += `@font-face{font-family:'Inter';src:url('${u(bold)}');font-weight:700 900;font-style:normal}`;
    if (fs.existsSync(ital)) s += `@font-face{font-family:'Inter';src:url('${u(ital)}');font-weight:100 600;font-style:italic}`;
    s += `body{font-family:'Inter',sans-serif}`;
    return s;
}

function listSnippets() {
    return fs.readdirSync(SNIPPET_DIR)
        .filter(f => f.endsWith('.html'))
        .sort()
        .map(f => ({ html: path.join(SNIPPET_DIR, f), width: 800, height: 600 }));
}

function targets() {
    const out = listSnippets();
    out.push({
        html: path.join(REPO, 'Assets', 'UI', 'match3.html'),
        width: 1280, height: 720,
    });
    // match3-endgame is captured at the viewport LayoutDiff_match3_endgame
    // runs (its original JSON was a one-off manual extract at an
    // uncontrolled window size — 1434x781 — which made every viewport-
    // anchored element drift).
    out.push({
        html: path.join(REPO, 'Assets', 'UI', 'match3-endgame.html'),
        width: 1729, height: 1080,
    });
    return out;
}

async function captureOne(browser, target) {
    const { html: htmlPath, width, height } = target;
    if (!fs.existsSync(htmlPath)) {
        return { htmlPath, ok: false, error: 'missing' };
    }
    const raw = fs.readFileSync(htmlPath, 'utf8');
    const cssPath = htmlPath.replace(/\.html$/i, '.css');
    const css = fs.existsSync(cssPath) ? fs.readFileSync(cssPath, 'utf8') : '';
    const isFragment = !/<\s*html[\s>]/i.test(raw) && !/<!doctype/i.test(raw);

    let loadPath = htmlPath;
    let tempPath = null;
    if (isFragment) {
        // Zero the body's UA margin so the fragment lays out at (0, 0).
        // Unity's HtmlParser does NOT synthesize <body> around a fragment,
        // so the fragment's first box sits at the document origin — Chrome
        // would otherwise add an 8px offset that produces a systematic
        // mismatch on every snippet. Add `body { margin: 0 }` BEFORE the
        // author sheet so the author can still override it if intentional.
        const wrapped =
            '<!doctype html>\n' +
            '<html><head><meta charset="utf-8"><style>body{margin:0}</style>' +
            '<style>' + bundledFontFaceCss() + '</style>' +
            '<style>' + css + '</style></head>' +
            '<body>' + raw + '</body></html>\n';
        tempPath = htmlPath + '.tmp.chrome-extract.html';
        fs.writeFileSync(tempPath, wrapped, 'utf8');
        loadPath = tempPath;
    }

    let elements = null;
    const page = await browser.newPage();
    try {
        await page.setViewport({ width, height });
        await page.goto(pathToFileURL(loadPath).href, { waitUntil: 'networkidle0', timeout: 30000 });
        // Layout dumps compare engine layout boxes, not transient visual
        // animation transforms. getBoundingClientRect() includes active
        // CSS animations/transitions, while Unity's headless layout dump
        // records the stable pre-transform box tree. Freeze motion before
        // measuring so animated samples do not produce false layout diffs.
        await page.addStyleTag({
            content: '*,*::before,*::after{animation:none!important;transition:none!important;}'
        });
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
        elements = await page.evaluate(() => {
            const out = [];
            // Skip the synthetic wrapper (html/head/body) and anything inside
            // <head> — meta/style/title don't participate in body layout and
            // Unity's tree walk starts at the fragment root. Also drop
            // display:none elements (matches Unity's UA: head, head * { display:none }).
            function walk(el, depth, inHead) {
                const tag = el.tagName;
                if (tag === 'HEAD') {
                    // Don't recurse into head — those children don't lay out.
                    return;
                }
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
                for (const c of el.children) walk(c, isWrapper ? depth : depth + 1, false);
            }
            // documentElement -> html (wrapper, depth not incremented)
            //  -> body (wrapper) -> snippet root counts as depth 0.
            walk(document.documentElement, 0, false);
            return out;
        });
    } finally {
        await page.close();
        if (tempPath && fs.existsSync(tempPath)) {
            try { fs.unlinkSync(tempPath); } catch { /* ignore */ }
        }
    }

    const outPath = htmlPath + '.chrome-layout.json';
    fs.writeFileSync(outPath, JSON.stringify({
        source: path.basename(htmlPath),
        width, height,
        count: elements.length,
        elements,
    }, null, 2));
    return { htmlPath, outPath, ok: true, count: elements.length };
}

async function main() {
    const list = targets();
    // W1: resolve the Chrome/Chromium executable. Prefer the env override so CI
    // can supply a custom path. Otherwise fall back to the system Chrome on the
    // canonical Windows path (matching the task-description note), then try the
    // puppeteer-bundled download (available when `npx puppeteer browsers install
    // chrome` has been run).
    const candidates = [
        process.env.CHROME_PATH,
        'C:/Program Files/Google/Chrome/Application/chrome.exe',
        'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    ].filter(Boolean);
    let executablePath;
    for (const p of candidates) {
        if (fs.existsSync(p)) { executablePath = p; break; }
    }
    // Fall back to puppeteer's downloaded browser (may throw if absent).
    if (!executablePath) {
        try { executablePath = puppeteer.executablePath(); } catch {}
    }
    const launchOpts = {
        headless: true,
        defaultViewport: { width: 800, height: 600 },
        args: ['--hide-scrollbars'],
    };
    if (executablePath) launchOpts.executablePath = executablePath;
    const browser = await puppeteer.launch(launchOpts);
    try {
        let okCount = 0;
        for (const t of list) {
            try {
                const res = await captureOne(browser, t);
                if (res.ok) {
                    okCount++;
                    console.log(`OK ${path.basename(res.htmlPath)} (${res.count} elements)`);
                } else {
                    console.log(`SKIP ${path.basename(t.html)} (${res.error})`);
                }
            } catch (e) {
                console.log(`FAIL ${path.basename(t.html)}: ${e.message || e}`);
            }
        }
        console.log(`\nCaptured ${okCount}/${list.length} demos.`);
    } finally {
        await browser.close();
    }
}

main().catch(err => {
    console.error(err && err.stack || err);
    process.exit(1);
});
