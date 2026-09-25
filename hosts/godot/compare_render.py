#!/usr/bin/env python3
"""Compares libweva's software backend against Godot's rasteriser.

Both sides consume the identical draw list from the same libweva build, so a
difference in the output is a difference between the two backends rather than
anywhere upstream. That is the check ARCHITECTURE.md §1 asks for: the render
interface sits at triangle altitude precisely so that a real engine and a
reference rasteriser can be held to the same pixels.

Exact equality is not the bar and never will be — llvmpipe and a scanline
rasteriser disagree at edges, and sRGB conversion rounds differently — so the
report is about how much and where, with a coverage check that catches the
failure that actually matters: geometry landing in the wrong place, or not at
all.

Stdlib only, on purpose: this has to run wherever the tests run.
"""

import argparse
import os
import subprocess
import sys
import tempfile


def read_ppm(path):
    """Reads a binary PPM (P6). Returns (width, height, bytes)."""
    with open(path, "rb") as f:
        data = f.read()
    if not data.startswith(b"P6"):
        raise ValueError(f"{path}: not a binary PPM")

    # The header is three whitespace-separated integers after the magic, with
    # '#' comments legal between any of them.
    fields, i = [], 2
    while len(fields) < 3:
        while i < len(data) and data[i : i + 1].isspace():
            i += 1
        if data[i : i + 1] == b"#":
            while i < len(data) and data[i : i + 1] != b"\n":
                i += 1
            continue
        start = i
        while i < len(data) and not data[i : i + 1].isspace():
            i += 1
        fields.append(int(data[start:i]))
    i += 1  # the single whitespace byte that ends the header

    width, height, maxval = fields
    if maxval != 255:
        raise ValueError(f"{path}: only 8-bit PPM is supported (maxval {maxval})")
    pixels = data[i : i + width * height * 3]
    if len(pixels) != width * height * 3:
        raise ValueError(f"{path}: truncated ({len(pixels)} bytes for {width}x{height})")
    return width, height, pixels


def modal_pixel(buf, total):
    """The most common pixel value, taken as the page colour behind the content."""
    counts = {}
    for p in range(total):
        i = p * 3
        key = buf[i] << 16 | buf[i + 1] << 8 | buf[i + 2]
        counts[key] = counts.get(key, 0) + 1
    key = max(counts, key=counts.get)
    return (key >> 16, (key >> 8) & 0xFF, key & 0xFF)


def differs(buf, i, page, tolerance):
    return (abs(buf[i] - page[0]) > tolerance
            or abs(buf[i + 1] - page[1]) > tolerance
            or abs(buf[i + 2] - page[2]) > tolerance)


def compare(a_path, b_path, tolerance, coverage_tolerance, structural_tolerance):
    aw, ah, a = read_ppm(a_path)
    bw, bh, b = read_ppm(b_path)
    if (aw, ah) != (bw, bh):
        print(f"FAIL  size {aw}x{ah} vs {bw}x{bh}")
        return False

    # "Ink" is any pixel that differs from the page behind it. The first version
    # of this defined ink as "not white", which is vacuous on a dark document —
    # every pixel counts, and the gate reports perfect agreement while measuring
    # nothing. The page colour is taken as each image's own modal pixel, so the
    # two are compared on the same footing whatever the design.
    #
    # Comparing ink coverage separately from channel error is what distinguishes
    # a box drawn in the wrong place (catastrophic, and what this is for) from
    # anti-aliasing along its edges (expected, and uninteresting).
    #
    # There is deliberately no "page colour" here any more, and no ink.
    #
    # Ink was "pixels differing from the page behind them", which needs a page
    # colour, and no choice of one survives these documents. Each image's own
    # modal pixel breaks on a gradient — the two backends land on different
    # points along it, and inventory reported 58.8% disagreement when the images
    # differ almost nowhere. One shared modal pixel fixes that but moves when
    # the RENDERER changes: fixing the outer box-shadow shifted it and
    # inventory's ink went 3.4% -> 9.6% while its over-tolerance stayed at
    # exactly 12.66%, i.e. not one pixel had changed. The corner pixel is stable
    # but calls most of a gradient page "ink", so vendor read 18% while looking
    # identical.
    #
    # What the ink number was FOR is worth keeping: separating geometry landing
    # in the wrong place from two rasterisers disagreeing along an edge. The
    # difference image gives that directly, with no notion of a page at all — an
    # edge pixel differs a little, a missing or misplaced shape differs a lot.
    # So: `differing` counts pixels past the ordinary tolerance, `structural`
    # counts pixels past a much larger one, and only the second gates.
    total = aw * ah
    differing = 0
    structural = 0
    worst = 0
    error_sum = 0
    worst_at = None

    for p in range(total):
        i = p * 3
        d = max(abs(a[i] - b[i]), abs(a[i + 1] - b[i + 1]), abs(a[i + 2] - b[i + 2]))
        error_sum += d
        if d > worst:
            worst, worst_at = d, (p % aw, p // aw)
        if d > tolerance:
            differing += 1
        if d > structural_tolerance:
            structural += 1

    mean = error_sum / total
    differing_pct = 100.0 * differing / total
    structural_pct = 100.0 * structural / total

    print(f"  size            {aw}x{ah} ({total} px)")
    print(f"  mean channel Δ  {mean:.2f}/255")
    print(f"  worst channel Δ {worst}/255 at {worst_at}")
    print(f"  over tolerance  {differing} px ({differing_pct:.2f}%), tolerance {tolerance}")
    print(f"  structural      {structural} px ({structural_pct:.2f}%), tolerance {structural_tolerance}")

    # A clear colour the two disagree on means the whole composite differs.
    # Read from the CORNER, which is background in every document here — the
    # modal pixel is not, on a page with a gradient — and compared at the same
    # tolerance as everything else, since this once demanded exact equality and
    # failed a document on #181228 against #181229.
    a_corner = (a[0], a[1], a[2])
    b_corner = (b[0], b[1], b[2])
    if max(abs(x - y) for x, y in zip(a_corner, b_corner)) > tolerance:
        print("FAIL  the two images do not share a page colour: "
              f"software #{a_corner[0]:02x}{a_corner[1]:02x}{a_corner[2]:02x} vs "
              f"godot #{b_corner[0]:02x}{b_corner[1]:02x}{b_corner[2]:02x}")
        return False

    # Only the structural count gates. A pixel differing a LITTLE is two
    # rasterisers disagreeing along an edge, which they are entitled to do; a
    # pixel differing a LOT is a shape drawn wrongly or not at all.
    if structural_pct > coverage_tolerance:
        print(f"FAIL  {structural_pct:.2f}% of pixels differ structurally "
              f"(limit {coverage_tolerance}%)")
        return False
    print("OK    the two backends agree on where the geometry lands")
    return True


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("html")
    ap.add_argument("css", nargs="?", default="-", help="'-' for no author stylesheet")
    ap.add_argument("--size", default="300x200")
    ap.add_argument("--weva-render", default="build/Tools/weva_render/weva_render")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--project", default="hosts/godot/project")
    ap.add_argument("--out-dir", default=None, help="keep the images here instead of a temp dir")
    ap.add_argument("--tolerance", type=int, default=8,
                    help="per-channel difference treated as rasteriser noise")
    ap.add_argument("--engine-font", action="store_true",
                    help="let Godot use its own font; the two sides then render "
                         "different text and only the non-text geometry is comparable")
    ap.add_argument("--structural-tolerance", type=int, default=64,
                    help="a per-channel difference past this is a shape drawn "
                         "wrongly rather than an edge two rasterisers round "
                         "differently")
    # The same interaction flags weva_render takes, forwarded to both sides so
    # the caret, the selection band and the open dropdown are compared as the
    # rest of the document is. They are the paths a corpus of static pages
    # never reaches.
    ap.add_argument("--focus", default=None, help="selector to focus")
    ap.add_argument("--selection", default=None, help="A,B on the focused field")
    ap.add_argument("--scroll", default=None, help="pixels to scroll the focused element")
    ap.add_argument("--hover", default=None, help="selector to put the pointer over")
    ap.add_argument("--press", default=None, help="selector to hold the pointer down on")
    ap.add_argument("--dialog", default=None, help="selector of a <dialog> to show modally")
    ap.add_argument("--popover", default=None, help="selector of a popover to open")
    ap.add_argument("--tooltip", default=None, help="selector to rest on until its title shows")
    ap.add_argument("--open", dest="open_select", default=None, help="a <select> to open")
    ap.add_argument("--coverage-tolerance", type=float, default=2.0,
                    help="percentage of pixels allowed to disagree on ink at all")
    args = ap.parse_args()

    width, _, height = args.size.partition("x")
    height = height or width

    tmp = args.out_dir or tempfile.mkdtemp(prefix="weva-render-")
    os.makedirs(tmp, exist_ok=True)
    soft = os.path.join(tmp, "software.ppm")
    godot = os.path.join(tmp, "godot.ppm")
    png = os.path.join(tmp, "godot.png")

    print("software backend:")
    # One list of flags, spelled the way each side spells it.
    soft_flags, godot_flags = [], []
    if args.focus:
        soft_flags.append("--focus=" + args.focus)
        godot_flags += ["--focus", args.focus]
    if args.selection:
        soft_flags.append("--selection=" + args.selection)
        godot_flags += ["--selection", args.selection]
    if args.scroll:
        soft_flags.append("--scroll=" + args.scroll)
        godot_flags += ["--scroll", args.scroll]
    if args.hover:
        soft_flags.append("--hover=" + args.hover)
        godot_flags += ["--hover", args.hover]
    if args.open_select:
        soft_flags.append("--open=" + args.open_select)
        godot_flags += ["--open", args.open_select]
    for name, value in (("press", args.press), ("dialog", args.dialog),
                        ("popover", args.popover), ("tooltip", args.tooltip)):
        if value:
            soft_flags.append("--%s=%s" % (name, value))
            godot_flags += ["--" + name, value]

    subprocess.run([args.weva_render, args.html, args.css, width, height, soft] + soft_flags,
                   check=True)

    print("godot backend:")
    css_arg = "" if args.css == "-" else os.path.abspath(args.css)
    result = subprocess.run(
        [args.godot, "--path", args.project, "--rendering-driver", "opengl3",
         "--scene", "res://capture.tscn", "--",
         "--html", os.path.abspath(args.html), "--css", css_arg,
         "--size", f"{width}x{height}", "--out", godot, "--png", png] + godot_flags
        # The reference rasteriser has no access to the engine's fonts, so the
        # comparison holds the font fixed on the core's built-in face. Without
        # this the two sides render different text and every glyph differs.
        + ([] if args.engine_font else ["--stub-font", "1"]),
        capture_output=True, text=True)
    # Godot logs audio and vsync failures on a headless machine that have
    # nothing to do with rendering, so only the capture's own line is echoed.
    for line in (result.stdout + result.stderr).splitlines():
        if line.startswith("capture:") or "ERROR: capture" in line:
            print(" ", line)
    if result.returncode != 0 or not os.path.exists(godot):
        print("FAIL  the godot capture did not produce an image")
        print(result.stdout[-2000:])
        print(result.stderr[-2000:])
        return 1

    print("comparison:")
    ok = compare(soft, godot, args.tolerance, args.coverage_tolerance,
                 args.structural_tolerance)
    print(f"images in {tmp}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
