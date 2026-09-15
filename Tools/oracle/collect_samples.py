#!/usr/bin/env python3
"""Collect development demos, package samples and oracle cases for Chrome checks.

The corpus uses unique page names and sibling CSS files for the layout tools.
Inline styles remain in the HTML and are also mirrored to that CSS file. Local
images travel in a per-page assets directory, with references rewritten in both
HTML and CSS. A missing local image fails collection instead of silently making
the browser and native renderer agree on an empty box.

Usage: collect_samples.py <wevaui-root> --out <corpus-dir>
"""

import argparse
import hashlib
import os
import re
import io
import sys
from pathlib import Path
from urllib.parse import quote, unquote, urlsplit

STYLE = re.compile(r"<style[^>]*>(.*?)</style>", re.S | re.I)


def sources(root):
    dirs = [os.path.join(root, "Assets", "UI"),
            os.path.join(root, "Packages", "com.wevaui", "Samples~"),
            # Cases written for the ORACLE rather than to be shipped. The
            # sample pages are game UI and exercise what game UI uses, which
            # left 87 of the 334 registered properties with no case at all --
            # floats, the line-breaking controls, logical sizing, the writing
            # modes. These fill that in, and live here rather than in the
            # package because nobody wants them in Samples~.
            os.path.join(root, "Tools", "oracle", "cases")]
    for d in dirs:
        for dirpath, subdirs, names in os.walk(d):
            subdirs.sort()
            for name in sorted(names):
                if name.endswith(".html"):
                    yield os.path.join(dirpath, name)


# An image a document points at, from `url(...)` in CSS or `src="..."` in the
# markup. Deliberately loose: it only has to find files worth copying.
ASSET = re.compile(r'''url\(\s*['"]?([^)'"]+\.(?:png|jpg|jpeg|webp)(?:[?#][^)'"\s]*)?)['"]?\s*\)'''
                   r'''|\bsrc\s*=\s*["']([^"']+\.(?:png|jpg|jpeg|webp)(?:[?#][^"']*)?)["']''',
                   re.IGNORECASE)


def copy_assets(text, source_dir, out, stem):
    def replace(match):
        group = 1 if match.group(1) is not None else 2
        url = urlsplit(match.group(group))
        if url.scheme or url.netloc:
            return match.group(0)
        src = (Path(source_dir) / unquote(url.path)).resolve()
        data = src.read_bytes()  # Missing local assets are errors.
        # Content names prevent collisions between a/icon.png and b/icon.png,
        # and contain all writes even for references reaching outside the page.
        digest = hashlib.sha256(data).hexdigest()[:16]
        relative = Path("assets") / stem / (digest + "-" + src.name)
        dst = Path(out) / relative
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes(data)
        name = quote(relative.as_posix())
        if url.query:
            name += "?" + url.query
        if url.fragment:
            name += "#" + url.fragment
        start, end = match.span(group)
        original = match.group(0)
        return original[:start - match.start()] + name + original[end - match.start():]

    return ASSET.sub(replace, text)


# Any stylesheet link at all — used to decide whether a copied sample needs one
# injected, not to parse the document.
STYLESHEET_LINK = re.compile(r"<link[^>]+rel=[\"']?stylesheet", re.IGNORECASE)


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
        while stem in seen:
            stem = "sample-" + stem
        seen.add(stem)
        dst = os.path.join(args.out, stem + ".html")
        css_src = os.path.splitext(html)[0] + ".css"
        css_dst = os.path.join(args.out, stem + ".css")
        source_stem = os.path.splitext(os.path.basename(html))[0]

        # The corpus is FLAT, so a sample's own markup can end up pointing at a
        # different sample's stylesheet. Two ways that happened, and both made
        # the browser render one page while the engines rendered another —
        # which is worse than a missing case, because the comparison still
        # produces numbers:
        #
        #   * A renamed stem (`menu` -> `sample-menu`) left the original
        #     `menu.css` link in the copy, and menu.css in the output
        #     directory is the OTHER menu sample's 6KB stylesheet, not the
        #     500-byte one collected beside it. Chrome loaded that; the engines
        #     were passed sample-menu.css.
        #   * A sample with no `<link>` at all but a sibling .css relies on the
        #     host's pair-by-basename convention, which a browser knows nothing
        #     about — the engines styled the page and Chrome did not.
        #
        # Both are fixed by making the copied HTML link the stylesheet that
        # travels with it, by its new name.
        with io.open(html, encoding="utf-8", errors="replace") as f:
            markup = f.read()
        if os.path.exists(css_src):
            if stem != source_stem:
                markup = markup.replace(source_stem + ".css", stem + ".css")
            if not STYLESHEET_LINK.search(markup):
                markup = '<link rel="stylesheet" href="%s.css" />\n' % stem + markup
        if os.path.exists(css_src):
            with open(css_src, encoding="utf-8", errors="replace") as f:
                css = f.read()
        else:
            css = "\n".join(STYLE.findall(markup))
        markup = copy_assets(markup, os.path.dirname(html), args.out, stem)
        css = copy_assets(css, os.path.dirname(html), args.out, stem)
        with io.open(dst, "w", encoding="utf-8", newline="\n") as f:
            f.write(markup)
        if css:
            with open(css_dst, "w", encoding="utf-8", newline="\n") as f:
                f.write(css)
        elif os.path.exists(css_dst):
            os.remove(css_dst)

        count += 1
    print(f"collected {count} samples into {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
