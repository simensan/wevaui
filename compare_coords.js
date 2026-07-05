// Compare Chrome vs Unity element coords. Matches by best-effort signature
// (tag + class + id + depth-relative position) and reports the worst N
// mismatches by Manhattan distance of (x,y,w,h).
const fs = require('fs');
const chromeRaw = JSON.parse(fs.readFileSync('chrome_coords_1434x781.json', 'utf8'));
const unity  = JSON.parse(fs.readFileSync('unity_coords.json', 'utf8'));
// Filter chrome entries with `display: inline` that have no class/id —
// these inline `<span>` text wrappers don't appear in our dump (they
// become InlineBox during layout and get folded into LineBox children).
// Including them in the diff produces phantom matches because the
// pairwise comparator pairs them with unrelated Unity elements.
const chrome = chromeRaw.filter(e => {
    if (e.display === 'inline' && !e.cls && !e.id) return false;
    return true;
});

function key(e) { return `${e.tag}|${e.cls}|${e.id}`; }

// Group by key, then index-within-group; assume same DOM walk order.
function index(arr) {
  const idx = new Map();
  const out = [];
  for (const e of arr) {
    const k = key(e);
    const i = idx.get(k) || 0;
    idx.set(k, i + 1);
    out.push({ ...e, _k: k, _i: i });
  }
  return out;
}
const ci = index(chrome), ui = index(unity);

// Map Unity by (key, occurrence)
const umap = new Map();
for (const u of ui) umap.set(`${u._k}#${u._i}`, u);

const rows = [];
for (const c of ci) {
  const u = umap.get(`${c._k}#${c._i}`);
  if (!u) { rows.push({ tag: c.tag, cls: c.cls, id: c.id, status: 'MISSING_IN_UNITY', chrome: c }); continue; }
  const dx = u.x - c.x, dy = u.y - c.y, dw = u.w - c.w, dh = u.h - c.h;
  const score = Math.abs(dx) + Math.abs(dy) + Math.abs(dw) + Math.abs(dh);
  rows.push({ tag: c.tag, cls: c.cls, id: c.id, score, dx, dy, dw, dh, c, u });
}

// Mark unity-only
const cmap = new Set(ci.map(c => `${c._k}#${c._i}`));
for (const u of ui) {
  if (!cmap.has(`${u._k}#${u._i}`)) {
    rows.push({ tag: u.tag, cls: u.cls, id: u.id, status: 'UNITY_ONLY', unity: u });
  }
}

// Print summary
const matched = rows.filter(r => r.score !== undefined);
matched.sort((a, b) => b.score - a.score);
console.log(`# Coord comparison @ ${1434}x${781}`);
console.log(`Chrome elements: ${chrome.length}, Unity elements: ${unity.length}`);
console.log(`Matched: ${matched.length}, Missing in Unity: ${rows.filter(r => r.status === 'MISSING_IN_UNITY').length}, Unity-only: ${rows.filter(r => r.status === 'UNITY_ONLY').length}`);
console.log('\n## Top 30 worst mismatches (Δx Δy Δw Δh):');
for (const r of matched.slice(0, 30)) {
  const sig = `${r.tag}.${r.cls.split(' ').join('.')}${r.id ? '#' + r.id : ''}`.slice(0, 50);
  console.log(`  ${sig.padEnd(50)} | Δx=${String(Math.round(r.dx)).padStart(6)} Δy=${String(Math.round(r.dy)).padStart(6)} Δw=${String(Math.round(r.dw)).padStart(6)} Δh=${String(Math.round(r.dh)).padStart(6)} | C=(${Math.round(r.c.x)},${Math.round(r.c.y)},${Math.round(r.c.w)}x${Math.round(r.c.h)}) U=(${Math.round(r.u.x)},${Math.round(r.u.y)},${Math.round(r.u.w)}x${Math.round(r.u.h)})`);
}
console.log('\n## Missing in Unity (top-level only — depth ≤ 3):');
for (const r of rows.filter(r => r.status === 'MISSING_IN_UNITY' && r.chrome.depth <= 3).slice(0, 20)) {
  console.log(`  depth=${r.chrome.depth} ${r.tag}.${r.cls}${r.id ? '#' + r.id : ''}`);
}

fs.writeFileSync('compare_report.json', JSON.stringify(rows, null, 2));
console.log('\nFull report → compare_report.json');
