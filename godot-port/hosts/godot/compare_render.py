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


def compare(a_path, b_path, tolerance, coverage_tolerance):
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
    total = aw * ah
    a_page = modal_pixel(a, total)
    b_page = modal_pixel(b, total)
    differing = 0
    worst = 0
    error_sum = 0
    a_ink = b_ink = ink_agree = 0
    worst_at = None

    for p in range(total):
        i = p * 3
        d = max(abs(a[i] - b[i]), abs(a[i + 1] - b[i + 1]), abs(a[i + 2] - b[i + 2]))
        error_sum += d
        if d > worst:
            worst, worst_at = d, (p % aw, p // aw)
        if d > tolerance:
            differing += 1
        a_on = differs(a, i, a_page, tolerance)
        b_on = differs(b, i, b_page, tolerance)
        a_ink += a_on
        b_ink += b_on
        ink_agree += a_on == b_on

    mean = error_sum / total
    differing_pct = 100.0 * differing / total
    ink_pct = 100.0 * (total - ink_agree) / total

    print(f"  size            {aw}x{ah} ({total} px)")
    print(f"  mean channel Δ  {mean:.2f}/255")
    print(f"  worst channel Δ {worst}/255 at {worst_at}")
    print(f"  over tolerance  {differing} px ({differing_pct:.2f}%), tolerance {tolerance}")
    print(f"  page colour     software #{a_page[0]:02x}{a_page[1]:02x}{a_page[2]:02x}, "
          f"godot #{b_page[0]:02x}{b_page[1]:02x}{b_page[2]:02x}")
    print(f"  ink coverage    software {a_ink} px, godot {b_ink} px")
    print(f"  ink disagrees   {total - ink_agree} px ({ink_pct:.2f}%)")

    # A page colour the two disagree on means the clear colour or the whole
    # composite differs, and every derived number below is then measuring the
    # wrong thing — worth failing on its own rather than explaining away.
    if a_page != b_page:
        print("FAIL  the two images do not even share a page colour")
        return False

    # Only the coverage check gates. Channel error inside shared ink is a
    # rasteriser difference; ink in the wrong place is a bug.
    if ink_pct > coverage_tolerance:
        print(f"FAIL  ink disagrees on {ink_pct:.2f}% of pixels (limit {coverage_tolerance}%)")
        return False
    print("OK    the two backends agree on where the geometry lands")
    return True


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("html")
    ap.add_argument("css", nargs="?", default="-", help="'-' for no author stylesheet")
    ap.add_argument("--size", default="300x200")
    ap.add_argument("--weva-render", default="build/tools/weva_render/weva_render")
    ap.add_argument("--godot", default="godot")
    ap.add_argument("--project", default="hosts/godot/project")
    ap.add_argument("--out-dir", default=None, help="keep the images here instead of a temp dir")
    ap.add_argument("--tolerance", type=int, default=8,
                    help="per-channel difference treated as rasteriser noise")
    ap.add_argument("--engine-font", action="store_true",
                    help="let Godot use its own font; the two sides then render "
                         "different text and only the non-text geometry is comparable")
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
    subprocess.run([args.weva_render, args.html, args.css, width, height, soft], check=True)

    print("godot backend:")
    css_arg = "" if args.css == "-" else os.path.abspath(args.css)
    result = subprocess.run(
        [args.godot, "--path", args.project, "--rendering-driver", "opengl3",
         "--scene", "res://capture.tscn", "--",
         "--html", os.path.abspath(args.html), "--css", css_arg,
         "--size", f"{width}x{height}", "--out", godot, "--png", png]
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
    ok = compare(soft, godot, args.tolerance, args.coverage_tolerance)
    print(f"images in {tmp}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
