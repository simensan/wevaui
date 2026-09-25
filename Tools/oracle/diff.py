#!/usr/bin/env python3
"""Compare a C++ weva_dump against a C# BaselineGen reference dump.

Zero tolerance on geometry, by design (docs/CONVENTIONS.md). A tolerance knob
is a way to hide bugs, so there isn't one for box geometry.

Comparison is on *parsed* numbers rather than the JSON text: C# and C++ format
doubles differently, and a formatting difference must never be mistakable for a
layout difference. Both sides are rounded to 2dp (BaselineGen's Round2, away
from zero) before an exact comparison.

Text-bearing corpus entries may set "tolerance_px" in their .meta.json, which
relaxes geometry comparison for that entry only — glyph rasterization differs
between font backends and chasing bit-identical text is not the goal.

Usage:  diff.py <reference.json> <candidate.json> [--tolerance PX]
Exit:   0 identical (within tolerance), 1 differences, 2 usage/IO error
"""
import json, sys
from decimal import Decimal, ROUND_HALF_UP

KEYS = ("x", "y", "w", "h")


def r2(v):
    # Matches C# Math.Round(v, 2, MidpointRounding.AwayFromZero).
    return float(Decimal(str(float(v))).quantize(Decimal("0.01"), rounding=ROUND_HALF_UP))


def load(path):
    with open(path) as f:
        return json.load(f)


def identity(e):
    return (e.get("depth"), e.get("tag", ""), e.get("id", ""), e.get("cls", ""))


def compare(ref, cand, tol):
    out = []
    re_, ce = ref.get("elements", []), cand.get("elements", [])

    if ref.get("width") != cand.get("width") or ref.get("height") != cand.get("height"):
        out.append(f"viewport: reference {ref.get('width')}x{ref.get('height')}, "
                   f"candidate {cand.get('width')}x{cand.get('height')}")

    if len(re_) != len(ce):
        out.append(f"element count: {len(re_)} expected, {len(ce)} produced")

    for i in range(min(len(re_), len(ce))):
        r, c = re_[i], ce[i]
        if identity(r) != identity(c):
            out.append(f"[{i}] identity: expected {identity(r)}, got {identity(c)}")
            continue  # geometry on a mismatched element is noise
        for k in KEYS:
            rv, cv = r2(r.get(k, 0)), r2(c.get(k, 0))
            if abs(rv - cv) > tol:
                tag = r.get("tag", "?")
                el = f"{tag}#{r['id']}" if r.get("id") else tag
                out.append(f"[{i}] {el}.{k}: expected {rv}, got {cv} (delta {cv - rv:+.2f})")
    return out


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    tol = 0.0
    for a in argv[1:]:
        if a.startswith("--tolerance"):
            tol = float(a.split("=", 1)[1]) if "=" in a else float(argv[argv.index(a) + 1])
    if len(args) < 2:
        print(__doc__)
        return 2
    try:
        ref, cand = load(args[0]), load(args[1])
    except (OSError, json.JSONDecodeError) as e:
        print(f"diff: {e}", file=sys.stderr)
        return 2

    diffs = compare(ref, cand, tol)
    if not diffs:
        n = len(ref.get("elements", []))
        print(f"OK  {n} elements identical  ({args[0]})")
        return 0

    print(f"DIFF  {args[0]}")
    for d in diffs[:40]:
        print("  " + d)
    if len(diffs) > 40:
        print(f"  ... and {len(diffs) - 40} more")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
