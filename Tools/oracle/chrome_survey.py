"""Compares a render against Chrome, but only where BOTH images are flat.

The render gate in hosts/godot/compare_render.py holds the two BACKENDS to each
other. That is the right check for a rasteriser, and it is now saturated: every
sample in the corpus agrees to within edge noise. What it cannot do is tell you
that both backends are wrong in the same way, which is exactly what happens
when a feature is missing from the core.

Chrome can answer that, but not pixel for pixel. Two things get in the way, and
both are properties of the CORPUS rather than of the engine:

  - the screenshots are captured under a synthetic metrics font whose glyphs are
    SOLID BOXES, so every run of text is a filled rectangle in Chrome and real
    letterforms here;
  - that font's line heights are not the core stub's, so text-dependent
    positions drift down the page (measured on quests: 4px at y=228, 9px at
    y=552), and anything sized by its content -- an `auto` grid track, a
    shrink-to-fit box -- comes out a different WIDTH.

So this does not compare pixels. It splits the page into cells, keeps only the
cells that are nearly uniform in both images, and compares their mean colour.
A cell flat in both and still disagreeing is a region painted the wrong colour:
a filter that was never applied, a gradient interpolated in the wrong space, a
material not implemented. Each cell may also slide vertically to find itself,
so the drift above does not masquerade as a colour difference.

Read the output as leads, not as a gate. Two false positives survive by
construction and are worth recognising on sight:

  - a solid glyph box in Chrome against blank background here reads as a large
    difference, and is only the metrics font (vendor's item icons, and
    match3-endgame's gold ones, are entirely this);
  - a content-sized box lands at a different WIDTH, so its edge falls inside
    different cells (grid-playground's tracks).

Both show up as a handful of cells with a large difference. A real one looks
like glass: hundreds of cells, systematic, over a whole material.

Usage: chrome_survey.py <sample> [cell-size] [flatness]
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from imgio import read_png, read_ppm, SAMPLES, DIAG  # noqa: E402

# How far a cell may slide vertically to find itself. Wider than the largest
# drift measured across the corpus, so alignment is never the limit.
SEARCH = 24


def cell_stats(buf, w, h, x0, y0, cell):
    """Returns (spread, mean per channel) over one cell."""
    lo = [255, 255, 255]
    hi = [0, 0, 0]
    tot = [0, 0, 0]
    n = 0
    for y in range(y0, min(y0 + cell, h)):
        for x in range(x0, min(x0 + cell, w)):
            i = (y * w + x) * 3
            for c in range(3):
                v = buf[i + c]
                tot[c] += v
                lo[c] = min(lo[c], v)
                hi[c] = max(hi[c], v)
            n += 1
    return max(hi[c] - lo[c] for c in range(3)), [t / n for t in tot]


def survey(name, cell=16, flatness=6, report=8):
    cw, ch, chrome = read_png(os.path.join(SAMPLES, f"{name}.html.chrome.png"))
    sw, sh, engine = read_ppm(os.path.join(DIAG, name, "software.ppm"))
    if (cw, ch) != (sw, sh):
        print(f"{name}: size {cw}x{ch} vs {sw}x{sh}")
        return

    rows = []
    flat = 0
    for y0 in range(0, ch, cell):
        for x0 in range(0, cw, cell):
            spread, mean_c = cell_stats(chrome, cw, ch, x0, y0, cell)
            if spread > flatness:
                continue
            best = None
            for dy in range(-SEARCH, SEARCH + 1, 2):
                yy = y0 + dy
                if yy < 0 or yy + cell > ch:
                    continue
                spread_e, mean_e = cell_stats(engine, cw, ch, x0, yy, cell)
                if spread_e > flatness:
                    continue
                d = max(abs(mean_c[c] - mean_e[c]) for c in range(3))
                if best is None or d < best[0]:
                    best = (d, mean_e)
            if best is None:
                continue
            flat += 1
            if best[0] > 3:
                rows.append((best[0], x0, y0, mean_c, best[1]))

    rows.sort(reverse=True)
    pct = 100.0 * len(rows) / max(1, flat)
    worst = f"   worst {rows[0][0]:.0f}" if rows else ""
    print(f"{name:<20} flat cells {flat:>5}   disagreeing {len(rows):>5} ({pct:.1f}%){worst}")
    for d, x0, y0, mc, me in rows[:report]:
        print("    d=%3.0f at (%4d,%4d)  chrome %3.0f %3.0f %3.0f   engine %3.0f %3.0f %3.0f"
              % (d, x0, y0, mc[0], mc[1], mc[2], me[0], me[1], me[2]))


if __name__ == "__main__":
    survey(sys.argv[1],
           int(sys.argv[2]) if len(sys.argv) > 2 else 16,
           int(sys.argv[3]) if len(sys.argv) > 3 else 6)
