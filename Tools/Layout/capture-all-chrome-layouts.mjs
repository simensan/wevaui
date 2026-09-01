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
const FONTS_DIR = path.join(REPO, 'Packages', 'com.wevaui', 'Runtime', 'Resources', 'Fonts');
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

// Under --metrics=mono Chrome also lays out under the ENGINES' user-agent
// sheet (godot-port/libweva/src/user_agent_stylesheet.cpp is the C#
// UserAgentStylesheet verbatim): `html, body { margin: 0; height: 100% }`,
// the form-control and heading defaults, the table defaults. Injected inside
// `@layer weva-ua`, so it beats Chrome's own UA sheet (author origin) but
// loses to every unlayered author rule whatever its specificity — exactly
// where a UA sheet sits. Without it every page that relies on a UA default
// (a body without `margin: 0`, an unstyled <h2>) is off Chrome by that
// default and nothing on it can be arbitrated.
const UA_SHEET_CPP = path.join(REPO, 'godot-port', 'libweva', 'src', 'user_agent_stylesheet.cpp');
function wevaUaCss() {
    if (!fs.existsSync(UA_SHEET_CPP)) return '';
    const src = fs.readFileSync(UA_SHEET_CPP, 'utf8');
    const m = /R"CSS\(([\s\S]*?)\)CSS"/.exec(src);
    return m ? m[1] : '';
}

function listSnippets() {
    return fs.readdirSync(SNIPPET_DIR)
        .filter(f => f.endsWith('.html'))
        .sort()
        .map(f => ({ html: path.join(SNIPPET_DIR, f), width: 800, height: 600 }));
}

// Directory mode: `node capture-all-chrome-layouts.mjs <dir> [w] [h]`
// captures every .html in <dir> (with its sibling .css) instead of the
// hard-coded demo list. The oracle's harvested corpus is generated, not
// versioned, so it cannot be a hard-coded target.
function listDir(dir, width, height) {
    return fs.readdirSync(dir)
        .filter(f => f.endsWith('.html'))
        .sort()
        .map(f => ({ html: path.join(dir, f), width, height }));
}

// --metrics=mono: measure with the engines' synthetic faces instead of Inter.
// Both BaselineGen and weva_dump use MonoFontMetrics (0.45em per glyph, 0.85 /
// 0.293 ascent/descent, 1.143 normal line-height; 0.6em for `monospace`), so
// with these faces Chrome's text widths equal the engines' exactly and it can
// arbitrate text-dependent differences too. `line-height: normal` is pinned to
// 1.143 because Blink rounds a face's ascent and descent to whole pixels for
// `normal` but computes a numeric line-height precisely.
const METRICS = (() => {
    const i = process.argv.findIndex(a => a.startsWith('--metrics='));
    if (i < 0) return 'inter';
    const v = process.argv[i].slice('--metrics='.length);
    process.argv.splice(i, 1);
    return v;
})();
const MONO_FONTS_DIR = path.join(REPO, 'godot-port', 'tools', 'oracle', 'fonts');
function monoFontFaceCss() {
    const u = p => pathToFileURL(p).href;
    const sans = path.join(MONO_FONTS_DIR, 'WevaMonoSans.ttf');
    const mono = path.join(MONO_FONTS_DIR, 'WevaMonoMonospace.ttf');
    if (!fs.existsSync(sans) || !fs.existsSync(mono)) {
        throw new Error('--metrics=mono needs the synthetic fonts: run godot-port/tools/oracle/make_mono_font.py');
    }
    return `@font-face{font-family:'WevaMonoSans';src:url('${u(sans)}')}` +
           `@font-face{font-family:'WevaMonoMonospace';src:url('${u(mono)}')}` +
           `html{font-family:'WevaMonoSans'}*{line-height:1.143}`;
}

function targets() {
    const argv = process.argv.slice(2);
    if (argv[0]) {
        return listDir(path.resolve(argv[0]),
                       parseInt(argv[1] || '800', 10), parseInt(argv[2] || '600', 10));
    }
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

    // What goes into <head> before the author's styles: the body margin
    // reset (or, under --metrics=mono, the engines' whole UA sheet in a
    // layer) and the font faces.
    const injected =
        (METRICS === 'mono'
            ? '<style>@layer weva-ua{' + wevaUaCss() + '}</style>'
            : '<style>body{margin:0}</style>') +
        '<style>' + (METRICS === 'mono' ? monoFontFaceCss() : bundledFontFaceCss()) + '</style>';

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
            '<html><head><meta charset="utf-8">' + injected +
            '<style>' + css + '</style></head>' +
            '<body>' + raw + '</body></html>\n';
        tempPath = htmlPath + '.tmp.chrome-extract.html';
        fs.writeFileSync(tempPath, wrapped, 'utf8');
        loadPath = tempPath;
    } else if (METRICS === 'mono') {
        // A full document gets the same injection at the start of its
        // <head>, from a temp copy beside it so relative <link>s still
        // resolve.
        let doc = raw;
        if (/<head[^>]*>/i.test(doc)) doc = doc.replace(/<head[^>]*>/i, m => m + injected);
        else if (/<html[^>]*>/i.test(doc)) doc = doc.replace(/<html[^>]*>/i, m => m + '<head>' + injected + '</head>');
        else doc = injected + doc;
        tempPath = htmlPath + '.tmp.chrome-extract.html';
        fs.writeFileSync(tempPath, doc, 'utf8');
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
        if (METRICS === 'mono') {
            // The engines resolve a font-family stack to the first REGISTERED
            // family — only `monospace` is registered beside the default — so
            // every element measures with the sans face unless its stack names
            // monospace anywhere. Mirror that per element, then let fonts
            // settle again.
            await page.evaluate(() => {
                const all = document.querySelectorAll('body, body *');
                for (const el of all) {
                    const fam = getComputedStyle(el).fontFamily || '';
                    const mono = /(^|,)\s*['"]?monospace['"]?\s*(,|$)/i.test(fam);
                    el.style.setProperty('font-family', mono ? 'WevaMonoMonospace' : 'WevaMonoSans', 'important');
                }
            });
        }
        await page.evaluate(() => document.fonts.ready);
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
