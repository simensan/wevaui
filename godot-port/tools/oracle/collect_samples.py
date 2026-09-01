#!/usr/bin/env python3
"""Collects the Unity samples into an oracle corpus: the pages a Godot host has
to render before the port can be said to run the same UIs Unity does.

Sources are the dev demos under Assets/UI and the shipped package samples under
Packages/com.wevaui/Samples~. Each is a full document with a
`<link rel="stylesheet">` to a sibling .css, which is the contract both
BaselineGen and weva_dump already follow (sibling .css by basename; <link> is
ignored). A page that carries its stylesheet inline in a <style> element gets
that extracted to the sibling file, because neither dumper reads <style> —
the real engine does, in UIDocumentBuilder — and a page laid out unstyled on
both sides would agree for a worthless reason.

Usage: collect_samples.py <wevaui-root> --out <corpus-dir>
"""

import argparse
import os
import re
import shutil
import sys

STYLE = re.compile(r"<style[^>]*>(.*?)</style>", re.S | re.I)


def sources(root):
    dirs = [os.path.join(root, "Assets", "UI"),
            os.path.join(root, "Packages", "com.wevaui", "Samples~")]
    for d in dirs:
        for dirpath, _, names in os.walk(d):
            for name in sorted(names):
                if name.endswith(".html"):
                    yield os.path.join(dirpath, name)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("root", help="wevaui repository root")
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    count = 0
    seen = set()
    for html in sources(args.root):
        stem = os.path.splitext(os.path.basename(html))[0]
        # Two samples share a name (Assets/UI/menu.html and the package's
        # menu.html); the package one is prefixed so both survive.
        if stem in seen:
            stem = "sample-" + stem
        seen.add(stem)
        dst = os.path.join(args.out, stem + ".html")
        shutil.copyfile(html, dst)
        css_src = os.path.splitext(html)[0] + ".css"
        css_dst = os.path.join(args.out, stem + ".css")
        if os.path.exists(css_src):
            shutil.copyfile(css_src, css_dst)
        else:
            with open(html, encoding="utf-8", errors="replace") as f:
                blocks = STYLE.findall(f.read())
            if blocks:
                with open(css_dst, "w", encoding="utf-8") as f:
                    f.write("\n".join(blocks))
            elif os.path.exists(css_dst):
                os.remove(css_dst)
        count += 1
    print(f"collected {count} samples into {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
