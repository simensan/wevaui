import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const REPO = path.resolve(__dirname, '..', '..');
const UI_DIR = path.join(REPO, 'Assets', 'UI');
const TOL = Number.parseFloat(process.argv[2] || '2');

const fixtures = fs.readdirSync(UI_DIR)
    .filter(f => f.endsWith('.html'))
    .map(f => f.slice(0, -5))
    .filter(name =>
        fs.existsSync(path.join(UI_DIR, `${name}.html.chrome-layout.json`)) &&
        fs.existsSync(path.join(UI_DIR, `${name}.unity-layout.json`)))
    .sort();

let totalRows = 0;
let totalMismatches = 0;
let totalMissing = 0;

for (const name of fixtures) {
    const chromeDoc = JSON.parse(fs.readFileSync(path.join(UI_DIR, `${name}.html.chrome-layout.json`), 'utf8'));
    const unityDoc = JSON.parse(fs.readFileSync(path.join(UI_DIR, `${name}.unity-layout.json`), 'utf8'));
    const chrome = chromeDoc.elements || chromeDoc;
    const unity = (unityDoc.elements || unityDoc).filter(el => el.tag !== 'html' && el.tag !== 'body');

    const unityBuckets = new Map();
    for (const el of unity) {
        const key = signature(el);
        let bucket = unityBuckets.get(key);
        if (!bucket) unityBuckets.set(key, bucket = []);
        bucket.push(el);
    }

    const chromeBuckets = new Map();
    const rows = [];
    for (const ce of chrome) {
        const key = signature(ce);
        const idx = chromeBuckets.get(key) || 0;
        chromeBuckets.set(key, idx + 1);
        const ue = (unityBuckets.get(key) || [])[idx];
        if (!ue) {
            rows.push({ missing: true, tag: ce.tag, cls: ce.cls || '', chrome: rect(ce) });
            continue;
        }
        const d = {
            x: round2(ue.x - ce.x),
            y: round2(ue.y - ce.y),
            w: round2(ue.w - ce.w),
            h: round2(ue.h - ce.h),
        };
        rows.push({
            tag: ce.tag,
            cls: ce.cls || '',
            chrome: rect(ce),
            unity: rect(ue),
            d,
            maxDelta: Math.max(Math.abs(d.x), Math.abs(d.y), Math.abs(d.w), Math.abs(d.h)),
        });
    }

    const mismatches = rows.filter(r => r.missing || (r.maxDelta || 0) > TOL)
        .sort((a, b) => (b.maxDelta || 0) - (a.maxDelta || 0));
    totalRows += rows.length;
    totalMismatches += mismatches.length;
    totalMissing += mismatches.filter(r => r.missing).length;

    console.log(`${name}: ${mismatches.length}/${rows.length} mismatches > ${TOL}px` +
        ` (chrome=${chrome.length}, unity=${unity.length})`);
    for (const r of mismatches.slice(0, 8)) {
        if (r.missing) {
            console.log(`  MISS  ${label(r).padEnd(42)} C=${formatRect(r.chrome)}`);
            continue;
        }
        console.log(`  ${r.maxDelta.toFixed(1).padStart(5)} ${label(r).padEnd(42)}` +
            ` C=${formatRect(r.chrome).padEnd(23)} U=${formatRect(r.unity).padEnd(23)}` +
            ` d=(${r.d.x},${r.d.y},${r.d.w},${r.d.h})`);
    }
}

console.log(`\nTOTAL: ${totalMismatches}/${totalRows} mismatches > ${TOL}px, missing=${totalMissing}`);

function signature(el) {
    return `${el.tag || ''}|${el.id || ''}|${el.cls || ''}`;
}

function label(r) {
    const cls = r.cls ? `.${r.cls.replace(/\s+/g, '.')}` : '';
    return `${r.tag}${cls}`;
}

function rect(el) {
    return {
        x: Number(el.x) || 0,
        y: Number(el.y) || 0,
        w: Number(el.w) || 0,
        h: Number(el.h) || 0,
    };
}

function formatRect(r) {
    return `(${r.x.toFixed(0)},${r.y.toFixed(0)},${r.w.toFixed(0)}x${r.h.toFixed(0)})`;
}

function round2(v) {
    return Math.round(v * 100) / 100;
}
