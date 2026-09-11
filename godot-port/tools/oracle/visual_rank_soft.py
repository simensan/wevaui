#!/usr/bin/env python3
"""Rank sample renders by how far the SOFTWARE renderer is from Chrome.

The companion to visual_rank.py, which compares the Godot host and needs both
Pillow and a Godot that can actually open a window. This one compares
weva_render's own output, decodes PNG with nothing but the standard library,
and so runs anywhere the core builds -- including a headless container, which
is where this port is usually tested.

Same caveat as the other tool, and it matters: this is NOT a pass/fail gate.
Two rasterisers never agree pixel for pixel, and the built-in 5x7 face does not
even try to match Chrome's text, so every page with words in it differs
everywhere. The number is good for ORDERING only: a page missing a whole
effect lands far above one that merely draws its glyphs differently. Read it as
"look at these first".

Two columns, because the first one on its own is misleading:

  all       every pixel. Dominated by text on any page with words in it,
            which is most of them, and so says more about the built-in face
            than about the renderer.
  non-text  the same measurement with the text masked out -- rendered twice,
            once with `color: transparent`, and the pixels that changed
            between the two (dilated, because Chrome sets the same line in a
            wider face) are excluded. THIS is the column that moves when a
            background, border, gradient, shadow or image is wrong, and the
            one to read first.

A page marked `animated` runs an `infinite` animation. Its screenshot was
taken at a phase we have no way to reproduce, so neither column means anything
for it -- the two images are of different moments, not different renderers.

Read `non-text` as an upper bound rather than a measurement. The corpus
screenshots were taken with text rendered as SOLID BLOCKS, which cover far more
area than the built-in face's thin strokes, so Chrome's glyph pixels spill past
any mask derived from our own render and land in the non-text column. A page in
the single digits there is almost certainly clean; the column earns its keep by
separating one page from another, not by its absolute value.

Two second-order effects worth recognising before calling one a bug, both found
by looking at the images rather than the numbers: a heavier face makes our line
boxes taller, which can push a container into overflow and raise a SCROLLBAR
Chrome never drew; and a block face makes a button read as though it had a
gradient. Neither is a rendering difference.

    python3 visual_rank_soft.py <weva_render> [corpus] [width] [height]
"""

import os
import struct
import subprocess
import sys
import tempfile
import zlib

# A channel difference this large is a real difference rather than a rounding
# or antialiasing one.
NOISE_FLOOR = 24


def read_png(path):
    """(width, height, RGB bytes) from a PNG, using only the stdlib."""
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    pos = 8
    width = height = depth = color = 0
    idat = []
    while pos + 8 <= len(data):
        length = struct.unpack(">I", data[pos : pos + 4])[0]
        kind = data[pos + 4 : pos + 8]
        body = data[pos + 8 : pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, depth, color = struct.unpack(">IIBB", body[:10])
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
    if depth != 8 or color not in (2, 6) or not idat:
        return None
    channels = 3 if color == 2 else 4
    raw = zlib.decompress(b"".join(idat))
    stride = width * channels
    out = bytearray(width * height * 3)
    previous = bytearray(stride)
    at = 0
    for y in range(height):
        filter_type = raw[at]
        at += 1
        line = bytearray(raw[at : at + stride])
        at += stride
        # PNG filters, per the spec's reconstruction rules.
        if filter_type == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filter_type == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 0xFF
        elif filter_type == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filter_type == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = previous[i]
                c = previous[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF
        previous = line
        for x in range(width):
            src = x * channels
            dst = (y * width + x) * 3
            out[dst : dst + 3] = line[src : src + 3]
    return width, height, bytes(out)


def read_ppm(path):
    with open(path, "rb") as f:
        if f.readline().strip() != b"P6":
            return None
        width, height = map(int, f.readline().split())
        f.readline()
        return width, height, f.read()


# Chrome sets the same line in a real face, which is wider and taller than the
# built-in 5x7 one, so its glyphs reach past ours. The mask is grown by this
# much before being trusted -- more across the line than down it, because that
# is the direction the difference accumulates in.
DILATE_X = 6
DILATE_Y = 3


def text_mask(with_text, without_text, width, height):
    """Pixels that are text, from the two renders that differ only in it."""
    _, _, a = with_text
    _, _, b = without_text
    mask = bytearray(width * height)
    n = min(len(a), len(b))
    for i in range(0, n, 3):
        if a[i] != b[i] or a[i + 1] != b[i + 1] or a[i + 2] != b[i + 2]:
            mask[i // 3] = 1

    # Grow it, separably: along each row, then down each column.
    grown = bytearray(width * height)
    for y in range(height):
        row = y * width
        run = 0
        for x in range(width):
            if mask[row + x]:
                run = DILATE_X + 1
            if run:
                grown[row + x] = 1
                run -= 1
        run = 0
        for x in range(width - 1, -1, -1):
            if mask[row + x]:
                run = DILATE_X + 1
            if run:
                grown[row + x] = 1
                run -= 1
    out = bytearray(width * height)
    for y in range(height):
        lo = max(0, y - DILATE_Y)
        hi = min(height - 1, y + DILATE_Y)
        row = y * width
        for yy in range(lo, hi + 1):
            src = yy * width
            for x in range(width):
                if grown[src + x]:
                    out[row + x] = 1
    return out


def compare(a, b, mask=None):
    """(fraction differing hard, mean channel delta)."""
    _, _, pa = a
    _, _, pb = b
    n = min(len(pa), len(pb))
    hard = 0
    total = 0
    pixels = 0
    for i in range(0, n, 3):
        if mask is not None and mask[i // 3]:
            continue
        pixels += 1
        d = max(abs(pa[i] - pb[i]), abs(pa[i + 1] - pb[i + 1]), abs(pa[i + 2] - pb[i + 2]))
        total += d
        if d >= NOISE_FLOOR:
            hard += 1
    if not pixels:
        return 0.0, 0.0
    return hard / pixels, total / pixels


def main(argv):
    if len(argv) < 2:
        sys.exit(__doc__)
    render = argv[1]
    corpus = argv[2] if len(argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "corpus", "samples")
    width = argv[3] if len(argv) > 3 else "1280"
    height = argv[4] if len(argv) > 4 else "720"

    rows = []
    with tempfile.TemporaryDirectory() as tmp:
        for name in sorted(os.listdir(corpus)):
            if not name.endswith(".html.chrome.png"):
                continue
            base = name[: -len(".html.chrome.png")]
            css = os.path.join(corpus, base + ".css")
            author = ""
            if os.path.exists(css):
                author = open(css, encoding="utf-8", errors="replace").read()
            else:
                css = os.devnull

            # The same document twice, differing only in whether text paints.
            # Concatenating the override onto the author sheet keeps this
            # entirely on the tool's side: the engine needs no flag for it, and
            # layout is untouched because transparent text still takes its box.
            blank = os.path.join(tmp, base + ".notext.css")
            with open(blank, "w", encoding="utf-8") as f:
                f.write(author)
                f.write("\n*,*::before,*::after,*::marker"
                        "{color:transparent!important;text-shadow:none!important;}\n")

            def run(stylesheet, suffix):
                out = os.path.join(tmp, base + suffix + ".ppm")
                # The clock is run before capturing. A page with an entrance
                # animation renders at its START at t=0 while a browser's
                # screenshot is taken after it has settled, and comparing
                # those measures the clock rather than the renderer.
                subprocess.run([render, os.path.join(corpus, base + ".html"), stylesheet,
                                width, height, out, "--advance=3"],
                               check=True, capture_output=True)
                return read_ppm(out)

            try:
                ours = run(css, "")
                ours_blank = run(blank, ".notext")
            except Exception:
                rows.append((float("inf"), base, "NO RENDER"))
                continue
            chrome = read_png(os.path.join(corpus, name))
            if not chrome or not ours or not ours_blank:
                rows.append((float("inf"), base, "unreadable"))
                continue
            if chrome[0] != ours[0] or chrome[1] != ours[1]:
                rows.append((float("inf"), base,
                             "size %dx%d vs %dx%d -- not comparable"
                             % (chrome[0], chrome[1], ours[0], ours[1])))
                continue

            w, h = ours[0], ours[1]
            mask = text_mask(ours, ours_blank, w, h)
            covered = sum(mask) / float(w * h)
            hard, mean = compare(chrome, ours)
            rest, rest_mean = compare(chrome, ours, mask)
            note = ("all %5.1f%% (delta %4.1f)   non-text %5.1f%% (delta %4.1f)   "
                    "text covers %4.1f%%"
                    % (hard * 100, mean, rest * 100, rest_mean, covered * 100))
            if "infinite" in author:
                note += "   [animated -- phase differs, not comparable]"
            rows.append((rest, base, note))

    rows.sort(key=lambda r: -r[0])
    for _, base, note in rows:
        print("%-20s %s" % (base, note))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
