import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const name = process.argv[2];
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
const DEFAULT_CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const executablePath = process.env.PUPPETEER_EXECUTABLE_PATH || (fs.existsSync(DEFAULT_CHROME) ? DEFAULT_CHROME : undefined);
const browser = await puppeteer.launch({ headless: true, executablePath, args: ['--hide-scrollbars', '--no-sandbox', '--force-device-scale-factor=3'] });
const page = await browser.newPage();
await page.setViewport({ width: 1434, height: 781, deviceScaleFactor: 3 });
await page.goto(pathToFileURL(path.resolve(loadPath)).href, { waitUntil: 'networkidle0' });
// clip from argv: x y w h (css px)
const [x, y, w, h] = process.argv.slice(3, 7).map(Number);
await page.screenshot({ path: process.argv[7], clip: { x, y, width: w, height: h } });
await browser.close();
if (isFragment) { try { fs.unlinkSync(loadPath); } catch {} }
console.log('ok');
