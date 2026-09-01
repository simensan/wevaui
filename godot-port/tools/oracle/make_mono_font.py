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

    python3 make_mono_font.py --out fonts/

Requires fontTools.
"""

import argparse
import os
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

UPM = 1000
ASCENT = 850
DESCENT = 293   # positive here; negative in hhea/OS2 where required


def codepoints():
    # Basic Latin, Latin-1, general punctuation and the symbols the samples use.
    cps = list(range(0x20, 0x7F)) + list(range(0xA0, 0x100))
    cps += [0x2013, 0x2014, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2026, 0x2032, 0x2033,
            0x2190, 0x2191, 0x2192, 0x2193, 0x2194, 0x21B5, 0x2212, 0x2215, 0x2248, 0x2260,
            0x2264, 0x2265, 0x2713, 0x2714, 0x2717, 0x2605, 0x2606, 0x25B6, 0x25C0, 0x25CF,
            0x25CB, 0x2665, 0x2666, 0x2660, 0x2663, 0x00D7, 0x2039, 0x203A, 0x2116, 0x2122,
            0x00B7, 0x2009, 0x200A, 0x202F]
    return sorted(set(cps))


def build(advance, family, out_path):
    cps = codepoints()
    glyph_order = [".notdef"] + ["u%04X" % cp for cp in cps]
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap({cp: "u%04X" % cp for cp in cps})

    def box():
        pen = TTGlyphPen(None)
        pen.moveTo((50, 0))
        pen.lineTo((50, 700))
        pen.lineTo((max(60, advance - 50), 700))
        pen.lineTo((max(60, advance - 50), 0))
        pen.closePath()
        return pen.glyph()

    def empty():
        pen = TTGlyphPen(None)
        return pen.glyph()

    glyphs = {".notdef": box()}
    for cp in cps:
        name = "u%04X" % cp
        # Spaces have no ink; everything else the same rectangle.
        glyphs[name] = empty() if cp in (0x20, 0xA0, 0x2009, 0x200A, 0x202F) else box()
    fb.setupGlyf(glyphs)
    metrics = {name: (advance, 50) for name in glyph_order}
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
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    n = build(450, "WevaMonoSans", os.path.join(args.out, "WevaMonoSans.ttf"))
    build(600, "WevaMonoMonospace", os.path.join(args.out, "WevaMonoMonospace.ttf"))
    print(f"wrote WevaMonoSans.ttf (0.45em) and WevaMonoMonospace.ttf (0.6em), {n} glyphs each, to {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
