// One-off pixel-reference screenshot of a sample at an exact viewport.
// Usage: node Tools/Layout/screenshot-glass.mjs [width height [outPath]]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '..', '..');
const html = path.join(REPO, 'Assets', 'UI', 'glass.html');
const width = parseInt(process.argv[2] || '1280', 10);
const height = parseInt(process.argv[3] || '720', 10);
const out = process.argv[4] || path.join(process.env.TEMP || '/tmp', 'glass_chrome.png');

const candidates = [
    process.env.CHROME_PATH,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
].filter(Boolean);
let executablePath;
for (const p of candidates) if (fs.existsSync(p)) { executablePath = p; break; }

const browser = await puppeteer.launch({
    headless: true,
    defaultViewport: { width, height, deviceScaleFactor: 1 },
    args: ['--hide-scrollbars', '--force-color-profile=srgb'],
    ...(executablePath ? { executablePath } : {}),
});
const page = await browser.newPage();
await page.goto(pathToFileURL(html).href, { waitUntil: 'networkidle0', timeout: 30000 });
// Freeze animations so the capture is deterministic (same as the layout captures).
await page.addStyleTag({ content: '*,*::before,*::after{animation:none!important;transition:none!important;}' });
await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
await page.screenshot({ path: out });
await browser.close();
console.log('wrote', out);
