#!/usr/bin/env python3
"""Compare the Unity host's render of a page with the Godot host's.

Both hosts draw the identical draw list from libweva, so a difference between
their images is a difference between the two engines' rasterisers, their font
engines (FontEngine vs TextServer) and their texture uploads, not between two
layouts. The metric is the Godot host's compare_render.py one: pixels past the
ordinary tolerance are counted, only pixels past the STRUCTURAL tolerance gate.

A page directory holds <name>.html, <name>.css, godot.ppm (from the Godot
host's capture.tscn) and unity.ppm (from the Native EditMode test
Parity_RendersPagesForComparison, driven by WEVA_NATIVE_PARITY):

    python compare_hosts.py .utmp/parity/camp .utmp/parity/randhtml
"""
import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'godot'))
from compare_render import compare  # noqa: E402


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('pages', nargs='+', type=Path, help='directories holding godot.ppm and unity.ppm')
    parser.add_argument('--tolerance', type=int, default=8, help='per-channel difference counted as differing')
    parser.add_argument('--structural-tolerance', type=int, default=64, help='per-channel difference counted as structural')
    parser.add_argument('--coverage-tolerance', type=float, default=1.0, help='structural pixels allowed, percent of the image')
    args = parser.parse_args()
    failures = 0
    for page in args.pages:
        godot = page / 'godot.ppm'
        unity = page / 'unity.ppm'
        print(f'== {page.name}')
        if not godot.exists() or not unity.exists():
            print(f'FAIL  missing {"godot.ppm" if not godot.exists() else "unity.ppm"}')
            failures += 1
            continue
        if not compare(str(unity), str(godot), args.tolerance, args.coverage_tolerance, args.structural_tolerance):
            failures += 1
    print(f'\n{len(args.pages) - failures}/{len(args.pages)} pages agree')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
