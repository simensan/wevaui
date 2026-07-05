import { pathToFileURL } from 'node:url';
import puppeteer from 'puppeteer';
const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const browser = await puppeteer.launch({ headless: true, executablePath: CHROME, args: ['--no-sandbox'] });
const page = await browser.newPage();
await page.setViewport({ width: 1024, height: 768 });
await page.goto(pathToFileURL('test-modal.html').href);
const r = await page.evaluate(() => {
    const m = document.querySelector('.modal').getBoundingClientRect();
    return { x: m.x, y: m.y, w: m.width, h: m.height };
});
console.log(JSON.stringify(r));
await browser.close();
