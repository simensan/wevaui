// One-off portrait variant of __shot.mjs: full-page 450x800 screenshot.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';

const name = process.argv[2];
const outPath = process.argv[3];
const htmlPath = path.join('Assets', 'UI', name + '.html');
const DEFAULT_CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const executablePath = process.env.PUPPETEER_EXECUTABLE_PATH || (fs.existsSync(DEFAULT_CHROME) ? DEFAULT_CHROME : undefined);
const browser = await puppeteer.launch({ headless: true, executablePath, args: ['--hide-scrollbars', '--no-sandbox', '--force-device-scale-factor=2'] });
const page = await browser.newPage();
await page.setViewport({ width: 450, height: 800, deviceScaleFactor: 2 });
await page.goto(pathToFileURL(path.resolve(htmlPath)).href, { waitUntil: 'networkidle0' });
await page.screenshot({ path: outPath });
await browser.close();
console.log('ok');
