import fs from 'node:fs';
import path from 'node:path';

const CHROME = path.resolve(process.cwd(), '..', '..', 'Assets', 'UI', 'inventory.html.chrome-layout.json');
const UNITY  = path.resolve(process.cwd(), '..', '..', 'Assets', 'UI', 'inventory.unity-layout.json');

const chrome = JSON.parse(fs.readFileSync(CHROME, 'utf8'));
const unity  = JSON.parse(fs.readFileSync(UNITY, 'utf8'));

const unityBuckets = new Map();
for (const el of unity) {
    const key = `${el.tag}|${el.cls}|${el.depth}`;
    if (!unityBuckets.has(key)) unityBuckets.set(key, []);
    unityBuckets.get(key).push(el);
}

const rows = [];
const chromeBuckets = new Map();
for (const ce of chrome.elements) {
    const key = `${ce.tag}|${ce.cls}|${ce.depth}`;
    const idx = chromeBuckets.get(key) || 0;
    chromeBuckets.set(key, idx + 1);
    const bucket = unityBuckets.get(key) || [];
    const ue = bucket[idx];
    if (!ue) {
        rows.push({ tag: ce.tag, cls: ce.cls, depth: ce.depth, missing: 'unity', chrome: { x: ce.x, y: ce.y, w: ce.w, h: ce.h } });
        continue;
    }
    const dx = +(ue.x - ce.x).toFixed(2);
    const dy = +(ue.y - ce.y).toFixed(2);
    const dw = +(ue.w - ce.w).toFixed(2);
    const dh = +(ue.h - ce.h).toFixed(2);
    const maxDelta = Math.max(Math.abs(dx), Math.abs(dy), Math.abs(dw), Math.abs(dh));
    rows.push({ tag: ce.tag, cls: ce.cls, depth: ce.depth, chrome: { x: ce.x, y: ce.y, w: ce.w, h: ce.h }, unity:  { x: ue.x, y: ue.y, w: ue.w, h: ue.h }, d: { x: dx, y: dy, w: dw, h: dh }, maxDelta });
}

const tol = 2.0;
const mismatches = rows.filter(r => r.missing || (r.maxDelta || 0) > tol);
mismatches.sort((a, b) => (b.maxDelta || 0) - (a.maxDelta || 0));

console.log(`Chrome: ${chrome.elements.length}  Unity: ${unity.length}  Mismatches (>${tol}px): ${mismatches.length} / ${rows.length}`);
console.log('');
console.log('  delta    tag         cls                                  chrome (x,y,w,h)            unity (x,y,w,h)              dx,dy,dw,dh');
console.log('  -------  ----------  -----------------------------------  ---------------------------  ---------------------------  ------------');
for (const r of mismatches.slice(0, 60)) {
    if (r.missing) {
        const c = r.chrome;
        console.log(`  MISS     ${r.tag.padEnd(10)} ${r.cls.padEnd(35)} ${formatRect(c).padEnd(28)} (missing in unity)`);
        continue;
    }
    const c = r.chrome, u = r.unity, d = r.d;
    const max = r.maxDelta.toFixed(1).padStart(6);
    console.log(`  ${max}   ${r.tag.padEnd(10)} ${r.cls.padEnd(35)} ${formatRect(c).padEnd(28)} ${formatRect(u).padEnd(28)} ${d.x},${d.y},${d.w},${d.h}`);
}

function formatRect(r) { return `(${r.x.toFixed(0)},${r.y.toFixed(0)},${r.w.toFixed(0)},${r.h.toFixed(0)})`; }
