#!/usr/bin/env python3
"""W6 GPU golden harness - perceptual compare of Unity GPU captures vs Chrome.

Joins Tools/RenderGoldens/unity/<name>.png (from the RenderGoldenCaptureTests
PlayMode test) against Tools/RenderGoldens/chrome/<name>.png (from
capture-chrome.mjs) and reports per-sample divergence:

  - diffpct: % of pixels whose max RGB channel delta exceeds PIXEL_DELTA
    (perceptual-ish: tolerates AA/gamma wobble, catches structure changes).
  - For samples over their threshold, writes a heat PNG to diff/<name>.png
    (red = divergent pixels over the Unity capture, dimmed).

Per-sample knobs live in compare-config.json:
  { "<name>": { "skip": "reason"      -- don't compare at all
              , "animated": true       -- report-only (no threshold fail)
              , "maxDiffPct": 3.0 } }  -- override the default threshold

v1 is a REPORTING harness: exit code 1 only for samples that exceed their
threshold AND are not marked animated/skip. Thresholds get tightened as the
engine converges (start loose, ratchet down - never the reverse silently).

Usage:  python Tools/RenderGoldens/compare.py [sampleName ...]
"""
import json
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
UNITY = os.path.join(HERE, "unity")
CHROME = os.path.join(HERE, "chrome")
DIFF = os.path.join(HERE, "diff")
CONFIG = os.path.join(HERE, "compare-config.json")

PIXEL_DELTA = 24          # per-channel 0-255 delta below this = same pixel
DEFAULT_MAX_DIFF_PCT = 5.0


def load_config():
    if os.path.exists(CONFIG):
        with open(CONFIG, encoding="utf-8") as f:
            return json.load(f)
    return {}


def compare(name, cfg):
    up = os.path.join(UNITY, name + ".png")
    cp = os.path.join(CHROME, name + ".png")
    if not os.path.exists(up):
        return {"name": name, "status": "missing-unity"}
    if not os.path.exists(cp):
        return {"name": name, "status": "missing-chrome"}

    u = Image.open(up).convert("RGB")
    c = Image.open(cp).convert("RGB")
    if u.size != c.size:
        c = c.resize(u.size)

    w, h = u.size
    upx, cpx = u.load(), c.load()
    bad = 0
    heat = None
    sample_cfg = cfg.get(name, {})
    want_heat = True
    if want_heat:
        heat = Image.new("RGB", u.size)
        hpx = heat.load()
    for y in range(h):
        for x in range(w):
            ur, ug, ub = upx[x, y]
            cr, cg, cb = cpx[x, y]
            d = max(abs(ur - cr), abs(ug - cg), abs(ub - cb))
            if d > PIXEL_DELTA:
                bad += 1
                if heat:
                    hpx[x, y] = (255, 40, 40)
            elif heat:
                hpx[x, y] = (ur // 3, ug // 3, ub // 3)
    diffpct = 100.0 * bad / (w * h)

    threshold = float(sample_cfg.get("maxDiffPct", DEFAULT_MAX_DIFF_PCT))
    animated = bool(sample_cfg.get("animated", False))
    over = diffpct > threshold
    if over and heat is not None:
        os.makedirs(DIFF, exist_ok=True)
        heat.save(os.path.join(DIFF, name + ".png"))
    return {
        "name": name,
        "status": "over" if (over and not animated) else ("animated-over" if over else "ok"),
        "diffpct": round(diffpct, 2),
        "threshold": threshold,
    }


def main():
    cfg = load_config()
    wanted = sys.argv[1:]
    names = sorted(
        f[:-4] for f in os.listdir(UNITY) if f.endswith(".png")
    ) if os.path.isdir(UNITY) else []
    if wanted:
        names = [n for n in names if n in wanted]

    results = []
    failed = []
    for n in names:
        if cfg.get(n, {}).get("skip"):
            results.append({"name": n, "status": "skip", "reason": cfg[n]["skip"]})
            continue
        r = compare(n, cfg)
        results.append(r)
        if r["status"] == "over":
            failed.append(n)

    width = max((len(r["name"]) for r in results), default=4)
    for r in results:
        line = f"{r['name']:<{width}}  {r['status']:<14}"
        if "diffpct" in r:
            line += f"  {r['diffpct']:6.2f}%  (max {r['threshold']}%)"
        if "reason" in r:
            line += f"  [{r['reason']}]"
        print(line)

    with open(os.path.join(HERE, "report.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)

    if failed:
        print(f"\nFAIL: {len(failed)} sample(s) over threshold: {', '.join(failed)}")
        sys.exit(1)
    print(f"\nOK: {len([r for r in results if r['status'] == 'ok'])} ok, "
          f"{len([r for r in results if r['status'] == 'animated-over'])} animated-over (report-only), "
          f"{len([r for r in results if r['status'] == 'skip'])} skipped")


if __name__ == "__main__":
    main()
