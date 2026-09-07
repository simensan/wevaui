// Exercise the actual capture helper, including its cascade and font remapping.
// Usage: node Tools/Layout/check-capture-metrics.mjs [chrome.exe] [output-dir] [--no-sandbox]
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import puppeteer from 'puppeteer';
import { captureOne } from './capture-all-chrome-layouts.mjs';

const argv = process.argv.slice(2).filter(a => a !== '--no-sandbox');
const outputRoot = path.resolve(argv[1] || os.tmpdir());
fs.mkdirSync(outputRoot, { recursive: true });
const work = fs.mkdtempSync(path.join(outputRoot, 'capture-metrics-'));
const browser = await puppeteer.launch({
    headless: true, pipe: true, userDataDir: path.join(work, 'profile'),
    executablePath: argv[0] || process.env.CHROME_PATH || undefined,
    args: process.argv.includes('--no-sandbox') ? ['--no-sandbox'] : [],
});
let checks = 0;
const failures = [];
function check(message, actual, expected) {
    ++checks;
    try { assert.deepEqual(actual, expected, message); }
    catch (error) { failures.push(error.message); }
}

try {
    const cases = [
        {
            name: 'inheritance',
            html: `<div id="percent"><div id="percent-child">text</div></div>
                <div id="em"><div id="em-child">text</div></div>
                <div id="math"><div id="math-child">text</div></div>
                <div id="number"><div id="number-child">text</div></div>
                <div id="normal"><div id="normal-child">text</div></div>
                <div id="shorthand">text</div>
                <div id="mono"><span id="mono-direct">abcdef</span>
                    <span><span id="mono-deep">abcdef</span></span>
                    <span id="sans-override">abcdef</span></div>
                <div id="sans"><span id="sans-deep">abcdef</span></div>`,
            css: `html,body{font-size:20px} div>div{font-size:10px}
                #percent{line-height:150%} #em{line-height:1.5em}
                #math{line-height:calc(1em + 10px)} #number{line-height:1.5}
                #normal{line-height:normal} #shorthand{font:24px sans-serif}
                #mono{font-family:monospace} #sans,#sans-override{font-family:sans-serif}
                span{display:inline-block;white-space:pre}`,
            expected: [
                ['percent', 'h', 30], ['percent-child', 'h', 30],
                ['em-child', 'h', 30], ['math-child', 'h', 30],
                ['number-child', 'h', 15], ['normal-child', 'h', 11.42],
                ['shorthand', 'h', 27.42], ['mono-direct', 'w', 72],
                ['mono-deep', 'w', 72], ['sans-override', 'w', 54],
                ['sans-deep', 'w', 54],
            ],
        },
        {
            name: 'root-cascade',
            html: '<div id="root-line"></div><div id="text">text</div>',
            css: 'html,body{font-size:20px} *{line-height:2} #root-line{height:1rlh}',
            expected: [['root-line', 'h', 40], ['text', 'h', 40]],
        },
        {
            name: 'normal-root',
            html: '<div id="root-line"></div><div id="text">text</div>',
            css: 'html,body{font-size:20px} #root-line{height:1rlh}',
            expected: [['root-line', 'h', 22.86], ['text', 'h', 22.86]],
        },
    ];
    for (const fixture of cases) {
        const html = path.join(work, fixture.name + '.html');
        fs.writeFileSync(html, fixture.html, 'utf8');
        fs.writeFileSync(html.replace(/\.html$/, '.css'), fixture.css, 'utf8');
        const result = await captureOne(browser, { html, width: 800, height: 600 }, { metrics: 'mono' });
        const dump = JSON.parse(fs.readFileSync(result.outPath, 'utf8'));
        check(fixture.name + ' captured', result.ok, true);
        check(fixture.name + ' metrics', dump.metrics, 'mono');
        check(fixture.name + ' browser', dump.browser, await browser.version());
        check(fixture.name + ' UA provenance', dump.userAgentStylesheet, 'weva overlay on browser');
        check(fixture.name + ' temporary HTML removed', fs.existsSync(html + '.tmp.chrome-extract.html'), false);
        for (const [id, field, expected] of fixture.expected) {
            const row = dump.elements.find(e => e.id === id);
            check(`${fixture.name} #${id}.${field}`, row?.[field], expected);
        }
    }
    fs.writeFileSync(path.join(work, 'result.json'), JSON.stringify({
        browser: await browser.version(), checks, failures,
    }, null, 2));
    console.log(`Capture metrics: ${checks} checks, ${failures.length} failures; ${work}`);
    if (failures.length) {
        console.error(failures.join('\n'));
        process.exitCode = 1;
    }
} finally {
    await browser.close();
}
