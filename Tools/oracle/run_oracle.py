#!/usr/bin/env python3
"""Chrome-only layout gate and shared layout comparison helpers.

The CLI delegates to chrome_sweep.py with browser metrics and a 1.5px ceiling.
It no longer invokes the deleted C# engine or accepts --reuse-reference.
The exact compare() helper remains available for same-core host parity checks.

    python run_oracle.py <corpus> --weva-dump <binary> --width 1280 --height 720
"""
import difflib
import json
import os
import sys


# The dump prints four decimals, so one unit in the last place is the finest
# difference it can express. Two independent implementations computing the same
# value can still land on opposite sides of a decimal midpoint: the half-leading
# of a 22.95px line is exactly 3.75975, and whether that prints 3.7597 or 3.7598
# depends on the last bit of a double, not on layout. A one-last-place gap is
# therefore below this instrument's resolution and is read as agreement;
# anything larger is a finding.
#
# This is the only tolerance between reference and candidate, and it is two
# orders of magnitude TIGHTER than the exact 2dp comparison it replaces — that
# one reported every tie as a 0.01 difference, and on weva-landing a single such
# tie at `.stats` cascaded into 80 reported differences that buried a real one.
LAST_PLACE = 1e-4


def same_value(x, y):
    """True when two dumped numbers agree to within the dump's resolution."""
    if x == y:
        return True
    try:
        return abs(float(x) - float(y)) <= LAST_PLACE * 1.0001
    except (TypeError, ValueError):
        return False


def compare(reference, candidate):
    """Returns a list of human-readable differences, empty when they agree."""
    problems = []
    a = reference.get("elements", [])
    b = candidate.get("elements", [])
    if len(a) != len(b):
        problems.append(f"element count: reference {len(a)}, candidate {len(b)}")

    for i in range(min(len(a), len(b))):
        ea, eb = a[i], b[i]
        # Identity first: a mismatch here means the two walks diverged, and
        # every geometry difference after it is a consequence rather than a
        # finding of its own.
        for key in ("depth", "tag", "id", "cls"):
            if ea.get(key) != eb.get(key):
                problems.append(
                    f"[{i}] {key}: reference {ea.get(key)!r}, candidate {eb.get(key)!r}")
        deltas = [f"{key} {ea.get(key)} vs {eb.get(key)}"
                  for key in ("x", "y", "w", "h")
                  if not same_value(ea.get(key), eb.get(key))]
        if deltas:
            label = f"{ea.get('tag')}#{ea.get('id')}.{ea.get('cls')}".rstrip("#.")
            problems.append(f"[{i}] {label}: " + ", ".join(deltas))

    # Elements only one side produced. Listed explicitly, because "count
    # differs" alone does not say which box went missing.
    for i in range(min(len(a), len(b)), max(len(a), len(b))):
        side, extra = ("reference", a[i]) if len(a) > len(b) else ("candidate", b[i])
        problems.append(f"[{i}] only in {side}: {extra.get('tag')}#{extra.get('id')}")
    return problems


def load_chrome(corpus, name):
    """Load the tracked getBoundingClientRect capture for a case."""
    for suffix in (".html.chrome-layout.json", ".chrome-layout.json"):
        path = os.path.join(corpus, name + suffix)
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                return json.load(f)
    return None


# Browser snapping and accumulated layout rounding; shared by the Chrome gate.
CHROME_TOLERANCE = 0.02
CHROME_RELATIVE_TOLERANCE = 2.5e-4


def chrome_agrees(chrome_value, value):
    try:
        c, v = float(chrome_value), float(value)
        return abs(c - v) <= CHROME_TOLERANCE + CHROME_RELATIVE_TOLERANCE * abs(v)
    except (TypeError, ValueError):
        return chrome_value == value


def _identity(e):
    return (e.get("tag"), e.get("id"), e.get("cls"))


def align(a, b):
    """Pairs elements of two dumps by identity, tolerating moves and gaps.

    The core box walk can emit inline fragments in a different order from
    Chrome's DOM walk, so a <b> can sit a few slots away in Chrome's list;
    Chrome also omits `display: none` elements (a hidden <input>) that the
    engines still dump. Neither is a reason to leave a whole page unjudged.
    Longest-common-subsequence pairs first; then any element left over on both
    sides with the same identity, in order, pairs up as a move. Returns
    ({index_in_a: index_in_b}, {index_in_a that paired as a move}) — a shift
    caused by an inserted neighbour is not a move, a real reorder is.
    """
    ia, ib = [_identity(e) for e in a], [_identity(e) for e in b]
    pairs = {}
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(a=ia, b=ib, autojunk=False).get_opcodes():
        if tag == "equal":
            for k in range(i2 - i1):
                pairs[i1 + k] = j1 + k
    used = set(pairs.values())
    spare = {}
    for j, ident in enumerate(ib):
        if j not in used:
            spare.setdefault(ident, []).append(j)
    moved = set()
    for i, ident in enumerate(ia):
        if i not in pairs and spare.get(ident):
            pairs[i] = spare[ident].pop(0)
            moved.add(i)
    return pairs, moved


def cases_in(corpus):
    """Every .html in the corpus, paired with its .css when one sits beside it."""
    found = []
    for name in sorted(os.listdir(corpus)):
        if not name.endswith(".html"):
            continue
        html = os.path.join(corpus, name)
        css = os.path.join(corpus, name[:-5] + ".css")
        found.append((name[:-5], html, css if os.path.exists(css) else None))
    return found


def main(argv=None):
    from chrome_sweep import main as chrome_main
    arguments = sys.argv[1:] if argv is None else argv
    return chrome_main(['--chrome-metrics', '--max-worst', '1.5', *arguments])


if __name__ == '__main__':
    sys.exit(main())
