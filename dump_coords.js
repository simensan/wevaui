// Dump getBoundingClientRect for every element in randhtml.html at a target viewport.
// Usage: node dump_coords.js [width] [height]
const path = require('path');
const fs = require('fs');
const puppeteer = require('puppeteer');

(async () => {
  const width = parseInt(process.argv[2] || '1434', 10);
  const height = parseInt(process.argv[3] || '781', 10);
  // Page to capture: 3rd CLI arg, else the demo page relative to repo root.
  const target = process.argv[4] || path.resolve(__dirname, 'Assets/UI/randhtml.html');
  const fileUrl = 'file://' + target.replace(/\\/g, '/');

  const browser = await puppeteer.launch({ headless: true, defaultViewport: { width, height } });
  const page = await browser.newPage();
  await page.setViewport({ width, height });
  await page.goto(fileUrl, { waitUntil: 'networkidle0' });

  const data = await page.evaluate(() => {
    function describe(el, depth) {
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      return {
        depth,
        tag: el.tagName.toLowerCase(),
        cls: el.className && typeof el.className === 'string' ? el.className : '',
        id: el.id || '',
        x: Math.round(r.x * 100) / 100,
        y: Math.round(r.y * 100) / 100,
        w: Math.round(r.width * 100) / 100,
        h: Math.round(r.height * 100) / 100,
        display: cs.display,
        position: cs.position,
        text: el.children.length === 0 ? (el.textContent || '').trim().slice(0, 60) : ''
      };
    }
    const out = [];
    function walk(el, depth) {
      out.push(describe(el, depth));
      for (const c of el.children) walk(c, depth + 1);
    }
    walk(document.body, 0);
    return out;
  });

  const outPath = path.join(__dirname, `chrome_coords_${width}x${height}.json`);
  fs.writeFileSync(outPath, JSON.stringify(data, null, 2));
  console.log(`Wrote ${data.length} elements to ${outPath}`);
  await browser.close();
})();
