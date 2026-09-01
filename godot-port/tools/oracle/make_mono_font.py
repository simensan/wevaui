#!/usr/bin/env python3
"""Generates the synthetic faces that let Chrome measure text the way the two
engines do.

Both BaselineGen and weva_dump measure with MonoFontMetrics::chrome_sans_serif
(0.45em per glyph, 0.85em ascent, 0.293em descent, so `normal` line-height is
1.143em) and chrome_monospace (0.6em per glyph, same vertical metrics). Chrome
measured with Inter, so every text-dependent value the two engines disagreed on
was undecidable: neither matched Chrome and neither could.

These fonts give every glyph the same advance and the engines' vertical
metrics. Loaded through @font-face by capture-all-chrome-layouts.mjs
--metrics=mono, they make Chrome's text widths equal to the engines' exactly,
and its line heights equal once `line-height: normal` is pinned to 1.143 (Blink
rounds a face's ascent and descent to whole pixels for `normal`; a numeric
line-height it computes precisely).

Outlines are a plain rectangle: layout never looks at them.

Coverage matters more than it looks: a character the face lacks falls back to
a system font in Chrome, with that font's advance, and every width downstream
of it stops being comparable. `--scan` adds every character the corpora
actually use (element text and CSS `content:` strings), and emoji get the
engines' own advances — 1.3em for the wide ranges, 1.0em for Dingbats, the
same allowlist as MonoFontMetrics (font_metrics.cpp / MonoFontMetrics.cs).

    python3 make_mono_font.py --out fonts/ --scan corpus/samples corpus/harvest ...

Requires fontTools.
"""

import argparse
import glob
import html as html_mod
import os
import re
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPM = 1000
ASCENT = 850
DESCENT = 293   # positive here; negative in hhea/OS2 where required


def codepoints(scan_dirs=()):
    # Basic Latin, Latin-1, general punctuation and the symbols the samples use.
    cps = list(range(0x20, 0x7F)) + list(range(0xA0, 0x100))
    cps += [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x2032, 0x2033,
            0x2190, 0x2191, 0x2192, 0x2193, 0x2194, 0x21B5, 0x2212, 0x2215, 0x2248, 0x2260,
            0x2264, 0x2265, 0x2713, 0x2714, 0x2717, 0x2605, 0x2606, 0x25B6, 0x25C0, 0x25CF,
            0x25CB, 0x2665, 0x2666, 0x2660, 0x2663, 0x00D7, 0x2039, 0x203A, 0x2116, 0x2122,
            0x00B7, 0x2009, 0x200A, 0x202F]
    for d in scan_dirs:
        cps += scan_codepoints(d)
    return sorted(cp for cp in set(cps) if cp >= 0x20 and not 0xD800 <= cp <= 0xDFFF)


def scan_codepoints(directory):
    """Every character a corpus directory's pages can put in a text run."""
    found = set()
    for path in glob.glob(os.path.join(directory, "*.html")) + glob.glob(os.path.join(directory, "*.css")):
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
        if path.endswith(".html"):
            text = re.sub(r"<style.*?</style>", " ", text, flags=re.S)
            text = re.sub(r"<script.*?</script>", " ", text, flags=re.S)
            text = html_mod.unescape(re.sub(r"<[^>]+>", " ", text))
        else:
            text = " ".join(re.findall(r"content\s*:\s*([^;}]+)", text))
        found.update(ord(ch) for ch in text)
    return found


# The engines' emoji advances (MonoFontMetrics): Chrome draws these from an
# emoji face at roughly 1.3em, the Dingbats block nearer 1.0em; everything
# else keeps the Latin advance.
def is_wide_emoji(cp):
    return (0x1F000 <= cp <= 0x1FAFF) or cp in (0x26A1, 0x26D4, 0x2600, 0x2614, 0x2615, 0x2618,
                                                 0x2620, 0x2705)


def is_medium_emoji(cp):
    return (0x2700 <= cp <= 0x27BF) or cp in (0x2699, 0x2298)


def advance_for(cp, base):
    if is_wide_emoji(cp):
        return 1300
    if is_medium_emoji(cp):
        return 1000
    return base


def build(advance, family, out_path, scan_dirs=()):
    cps = codepoints(scan_dirs)
    glyph_order = [".notdef"] + ["u%04X" % cp for cp in cps]
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap({cp: "u%04X" % cp for cp in cps})

    def box(adv=advance):
        pen = TTGlyphPen(None)
        pen.moveTo((50, 0))
        pen.lineTo((50, 700))
        pen.lineTo((max(60, adv - 50), 700))
        pen.lineTo((max(60, adv - 50), 0))
        pen.closePath()
        return pen.glyph()

    def empty():
        pen = TTGlyphPen(None)
        return pen.glyph()

    glyphs = {".notdef": box()}
    metrics = {".notdef": (advance, 50)}
    for cp in cps:
        name = "u%04X" % cp
        adv = advance_for(cp, advance)
        # Spaces have no ink; everything else a rectangle of its advance.
        glyphs[name] = empty() if cp in (0x20, 0xA0, 0x2009, 0x200A, 0x202F) else box(adv)
        metrics[name] = (adv, 50)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ASCENT, descent=-DESCENT, lineGap=0)
    fb.setupOS2(sTypoAscender=ASCENT, sTypoDescender=-DESCENT, sTypoLineGap=0,
                usWinAscent=ASCENT, usWinDescent=DESCENT, fsSelection=0x80 | 0x40,
                achVendID="WEVA")
    fb.setupNameTable({"familyName": family, "styleName": "Regular",
                       "fullName": family, "psName": family.replace(" ", "")})
    fb.setupPost()
    fb.save(out_path)
    return len(cps)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "fonts"))
    ap.add_argument("--scan", nargs="*", default=[],
                    help="corpus directories whose pages' characters the faces must cover")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    n = build(450, "WevaMonoSans", os.path.join(args.out, "WevaMonoSans.ttf"), args.scan)
    build(600, "WevaMonoMonospace", os.path.join(args.out, "WevaMonoMonospace.ttf"), args.scan)
    print(f"wrote WevaMonoSans.ttf (0.45em) and WevaMonoMonospace.ttf (0.6em), {n} glyphs each, to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
