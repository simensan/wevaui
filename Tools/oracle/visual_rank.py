#!/usr/bin/env python3
"""Rank sample renders by how far the Godot host is from Chrome.

This is NOT a pass/fail gate. Two different rasterisers never agree pixel for
pixel — glyph rendering alone differs everywhere text appears — so the number
here is only good for ORDERING: a page missing a whole effect (a backdrop blur,
a mask) lands far above one that merely antialiases differently. Read it as
"look at these first", never as "this many pixels are wrong".

Both sides must be captured with the SAME faces, or this measures the fonts
instead of the renderer. Chrome's screenshots need the same `--metrics=mono`
the layout capture uses:

    cd Tools/Layout
    node capture-all-chrome-layouts.mjs <corpus> 1280 720 --screenshot \\
         --no-layout --metrics=mono

and the Godot side comes from capture.tscn WITHOUT `--headless` (that flag
disables rendering, and every capture then writes nothing):

    godot --rendering-driver opengl3 --path hosts/godot/project \\
          --scene res://capture.tscn -- --html X --css Y --size 1280x720 \\
          --png out.png

Usage: visual_rank.py <godot-png-dir> [<corpus-dir>]
"""
import os
import sys

try:
    from PIL import Image, ImageChops
except ImportError:  # pragma: no cover - the message is the whole point
    sys.exit("visual_rank needs Pillow: pip install pillow")

# Below this, a per-channel difference is glyph antialiasing and JPEG-grade
# noise rather than anything a person would call wrong.
NOISE_FLOOR = 24


def main(argv):
    if len(argv) < 2:
        sys.exit(__doc__)
    godot_dir = argv[1]
    corpus = argv[2] if len(argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "corpus", "samples")

    rows = []
    for name in sorted(os.listdir(corpus)):
        if not name.endswith(".html.chrome.png"):
            continue
        base = name[: -len(".html.chrome.png")]
        got = os.path.join(godot_dir, base + ".png")
        if not os.path.exists(got):
            rows.append((float("inf"), base, "NO RENDER"))
            continue
        a = Image.open(os.path.join(corpus, name)).convert("RGB")
        b = Image.open(got).convert("RGB")
        if a.size != b.size:
            rows.append((float("inf"), base,
                         "size %s vs %s — not comparable" % (a.size, b.size)))
            continue
        hist = ImageChops.difference(a, b).convert("L").histogram()
        total = float(sum(hist))
        hard = sum(hist[NOISE_FLOOR:]) / total
        mean = sum(i * c for i, c in enumerate(hist)) / total
        rows.append((hard, base, "%5.1f%% differ hard, mean delta %4.1f" % (hard * 100, mean)))

    rows.sort(key=lambda r: -r[0])
    for _, base, note in rows:
        print("%-24s %s" % (base, note))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
