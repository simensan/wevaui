// Temp diagnostic: dump Chrome's computed font/line-height for paragraphs in a sample.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const repo = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\//, '')), '..', '..');
const name = process.argv[2] || 'menu';
const htmlPath = path.join('Assets', 'UI', name + '.html');
const cssPath = path.join('Assets', 'UI', name + '.css');

const raw = fs.readFileSync(htmlPath, 'utf8');
const css = fs.existsSync(cssPath) ? fs.readFileSync(cssPath, 'utf8') : '';
const isFragment = !/<\s*html[\s>]/i.test(raw) && !/<!doctype/i.test(raw);

let loadPath = htmlPath;
if (isFragment) {
  const wrapped = '<!doctype html><html><head><meta charset="utf-8"><style>body{margin:0}</style><style>' + css + '</style></head><body>' + raw + '</body></html>';
  loadPath = path.join('Assets', 'UI', '__probe.html');
  fs.writeFileSync(loadPath, wrapped);
}

const DEFAULT_CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const executablePath = process.env.PUPPETEER_EXECUTABLE_PATH || (fs.existsSync(DEFAULT_CHROME) ? DEFAULT_CHROME : undefined);
const browser = await puppeteer.launch({ headless: true, executablePath, args: ['--hide-scrollbars', '--no-sandbox'] });
const page = await browser.newPage();
await page.setViewport({ width: 1434, height: 781 });
await page.goto(pathToFileURL(path.resolve(loadPath)).href, { waitUntil: 'networkidle0' });
const data = await page.evaluate(() => {
  const out = [];
  const els = document.querySelectorAll('p, h1, h2, h3, span.lbl, div.label');
  let n = 0;
  for (const el of els) {
    if (n++ > 5) break;
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    out.push({
      sel: el.tagName + '.' + (el.className || ''),
      text: (el.textContent || '').slice(0, 24),
      fontFamily: cs.fontFamily,
      fontSize: cs.fontSize,
      lineHeight: cs.lineHeight,
      rectH: r.height,
    });
  }
  return out;
});
console.log(JSON.stringify(data, null, 1));
await browser.close();
if (isFragment) { try { fs.unlinkSync(loadPath); } catch {} }
