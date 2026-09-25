#!/usr/bin/env python3
"""Harvest an oracle corpus from the C# test suite (docs/ORACLE.md).

The ~10,500 NUnit tests are hand-written C# asserts and don't translate. What
*does* translate is the HTML and CSS they exercise: run both engines over it and
diff the resulting box geometry. This script extracts those snippets.

It is deliberately conservative. A snippet that needs a test's surrounding
setup (an injected clock, a fake font metric, a mutated DOM) will not lay out
identically standalone, so it is better dropped than silently wrong -- a corpus
entry that diverges for harness reasons costs more than the coverage it adds.
Review what lands in corpus/ before trusting it.

Usage:  harvest.py <tests-root> <corpus-out> [--min-len N]
"""
import os, re, sys, json, hashlib

# C# string literals: verbatim @"..." ("" escapes a quote) and regular "..."
VERBATIM = re.compile(r'@"((?:[^"]|"")*)"', re.S)
REGULAR = re.compile(r'"((?:[^"\\\n]|\\.)*)"')

HTML_HINT = re.compile(r'<\s*(html|body|div|span|p|ul|li|table|section|main|header|button|input|template)\b', re.I)
CSS_HINT = re.compile(r'[.#a-zA-Z\[][^{}]{0,120}\{[^{}]*[a-z-]+\s*:', re.S)


def unescape(raw, verbatim):
    if verbatim:
        return raw.replace('""', '"')
    return (raw.replace('\\"', '"').replace('\\n', '\n')
               .replace('\\r', '\r').replace('\\t', '\t').replace('\\\\', '\\'))


def literals(src):
    for m in VERBATIM.finditer(src):
        yield unescape(m.group(1), True)
    # Strip verbatim strings before scanning regular ones so their bodies
    # aren't rescanned and split at embedded quotes.
    for m in REGULAR.finditer(VERBATIM.sub('@""', src)):
        yield unescape(m.group(1), False)


def classify(s):
    if HTML_HINT.search(s):
        return 'html'
    if CSS_HINT.search(s):
        return 'css'
    return None


def bucket(path):
    """Partition by feature so a phase can gate on its own subset."""
    p = path.replace('\\', '/').lower()
    for name in ('grid', 'flex', 'tables', 'multicol', 'floats', 'positioning',
                 'scrolling', 'text', 'inline', 'cascade', 'selectors'):
        if f'/{name}/' in p or name in os.path.basename(p):
            return {'tables': 'tables', 'positioning': 'positioning',
                    'scrolling': 'scrolling', 'selectors': 'cascade'}.get(name, name)
    return 'block'


def main(argv):
    args = [a for a in argv[1:] if not a.startswith('--')]
    if len(args) < 2:
        print(__doc__)
        return 2
    root, out = args[0], args[1]
    min_len = 40
    for a in argv[1:]:
        if a.startswith('--min-len='):
            min_len = int(a.split('=', 1)[1])

    seen, counts = set(), {}
    for dirpath, _, files in os.walk(root):
        for fn in files:
            if not fn.endswith('.cs'):
                continue
            full = os.path.join(dirpath, fn)
            try:
                src = open(full, encoding='utf-8', errors='replace').read()
            except OSError:
                continue
            for lit in literals(src):
                lit = lit.strip()
                if len(lit) < min_len:
                    continue
                kind = classify(lit)
                if not kind:
                    continue
                digest = hashlib.sha1(lit.encode()).hexdigest()[:12]
                if digest in seen:
                    continue
                seen.add(digest)
                b = bucket(full)
                d = os.path.join(out, b)
                os.makedirs(d, exist_ok=True)
                with open(os.path.join(d, f'{digest}.{kind}'), 'w') as f:
                    f.write(lit + '\n')
                # Provenance: when an entry diverges, the first question is
                # always "which test did this come from?"
                meta = os.path.join(d, f'{digest}.meta.json')
                if not os.path.exists(meta):
                    json.dump({'origin': os.path.relpath(full, root),
                               'kind': kind, 'width': 1280, 'height': 720},
                              open(meta, 'w'), indent=2)
                counts[b] = counts.get(b, 0) + 1

    total = sum(counts.values())
    for b in sorted(counts):
        print(f'  {b:<12} {counts[b]}')
    print(f'  {"TOTAL":<12} {total}')
    print('\nReview before trusting: snippets needing test-harness setup will not '
          'lay out identically standalone.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
