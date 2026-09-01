#!/usr/bin/env python3
"""Harvests HTML+CSS pairs from the C# test suite into oracle corpus cases.

ORACLE.md names this as the first corpus source: "Harvest every inline HTML/CSS
string from the C# test suite. 291 test files across Layout and Css contain
them; extraction is a scripted pass, not manual work."

It is worth doing for a specific reason. The hand-built 47-case corpus now
passes completely, and that is a weaker statement than it sounds: a green corpus
means the cases do not distinguish the implementations, not that they agree
everywhere. Multicol proved it — a wrong balancing algorithm passed the corpus
because its one multicol case happened not to separate the two answers.

The tests embed their fixtures as C# verbatim strings:

    const string css = @"...";
    const string html = @"...";

Pairing is by position within a file: each `html` takes the nearest preceding
`css` that no other `html` has already claimed, which is how the tests are
written. A file's fixtures are emitted as `<file>-<n>.html` / `.css`.

Cases are NOT filtered by whether the port supports them. A case exercising an
unported feature fails loudly, which is correct — the harness reports it and the
plan already lists what is unported.
"""

import argparse
import os
import re
import sys

# Every C# verbatim string, with "" as the escaped quote. Not just
# `const string css = ...`: the suite writes `var css`, `string Css`, and passes
# markup straight into a call as an unnamed argument. Matching the string and
# classifying it by CONTENT rather than by variable name is what took the
# harvest from 53 cases to the whole suite.
VERBATIM = re.compile(r'@"((?:[^"]|"")*)"', re.S)


def unescape(text):
    return text.replace('""', '"')


def looks_like_html(text):
    return "<" in text and ">" in text


def looks_like_css(text):
    # A declaration block, and not markup.
    return "{" in text and "}" in text and not looks_like_html(text)


def harvest_file(path):
    """Returns [(html, css_or_None)] in source order."""
    with open(path, encoding="utf-8", errors="replace") as f:
        source = f.read()

    found = []
    for match in VERBATIM.finditer(source):
        body = unescape(match.group(1))
        if looks_like_html(body):
            found.append(("html", body, match.start()))
        elif looks_like_css(body):
            found.append(("css", body, match.start()))

    cases = []
    pending_css = None
    for kind, body, _ in found:
        if kind == "css":
            pending_css = body
        else:
            cases.append((body, pending_css))
            # A stylesheet is reused by following markup in some tests, so it
            # is deliberately not cleared.
    return cases


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("roots", nargs="+", help="directories of C# test files to scan")
    ap.add_argument("--out", required=True, help="corpus directory to write")
    ap.add_argument("--min-elements", type=int, default=1,
                    help="skip fixtures with fewer than this many tags")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    written = 0
    files = 0
    for root in args.roots:
        for dirpath, _, names in os.walk(root):
            for name in sorted(names):
                if not name.endswith(".cs"):
                    continue
                path = os.path.join(dirpath, name)
                cases = harvest_file(path)
                if not cases:
                    continue
                files += 1
                stem = os.path.splitext(name)[0]
                for index, (html, css) in enumerate(cases):
                    if html.count("<") < args.min_elements:
                        continue
                    base = os.path.join(args.out, f"{stem}-{index:02d}")
                    with open(base + ".html", "w") as f:
                        f.write(html)
                    if css:
                        with open(base + ".css", "w") as f:
                            f.write(css)
                    written += 1

    print(f"harvested {written} cases from {files} files into {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
