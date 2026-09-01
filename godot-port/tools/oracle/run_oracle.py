#!/usr/bin/env python3
"""Runs the differential oracle: C# reference vs C++ candidate, per corpus case.

docs/ORACLE.md is the design. This is the harness it describes: for every case
it runs BaselineGen's `layout-dump` and `weva_dump` at the same viewport and
compares the two JSON dumps element by element.

Tolerance is zero, deliberately. Both sides round to 2dp with the same
away-from-zero rule before formatting, so a value that differs at all differs
because the engines disagree, not because a double drifted. A tolerance knob
here would be a way to stop seeing bugs.

Stdlib only, so it runs wherever the tests run.
"""

import argparse
import difflib
import json
import os
import subprocess
import sys


def load(path):
    with open(path) as f:
        return json.load(f)


def run_reference(dotnet, project, html, css, width, height, out):
    cmd = [dotnet, "run", "-c", "Release", "--no-build", "--project", project, "--",
           "layout-dump", html, str(width), str(height), out]
    # BaselineGen treats a missing css argument as "no author stylesheet", which
    # is not the same as an empty file — pass it only when there is one.
    if css:
        cmd.append(css)
    return subprocess.run(cmd, capture_output=True, text=True)


def run_candidate(weva_dump, html, css, width, height, out):
    cmd = [weva_dump, html, str(width), str(height), out]
    if css:
        cmd.append(css)
    return subprocess.run(cmd, capture_output=True, text=True)


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
                  for key in ("x", "y", "w", "h") if ea.get(key) != eb.get(key)]
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
    """Chrome's own getBoundingClientRect capture for a case, when the corpus has one.

    The reference is a reference, not ground truth. When the two implementations
    disagree, this is the tiebreak — and it has already been needed once, on
    `aspect-ratio` inside a grid track, where the C# reports a card 2px taller
    than the row it sits in AND a container height that contradicts its own
    cards. Chrome and the C++ agree; the C# is wrong.
    """
    for suffix in (".html.chrome-layout.json", ".chrome-layout.json"):
        path = os.path.join(corpus, name + suffix)
        if os.path.exists(path):
            with open(path) as f:
                return json.load(f)
    return None


# Chrome's LayoutNG snaps to 1/64 px and getBoundingClientRect reports the
# snapped value, so its numbers can sit up to 0.02 from an exact double the two
# engines share. This tolerance applies ONLY to reading Chrome's verdict; the
# reference-versus-candidate comparison stays exact (ORACLE.md).
CHROME_TOLERANCE = 0.02
# ...and the snapping accumulates down a page: every line box and every
# block edge is placed on the 1/64 grid, so a value 1500px down can sit
# 0.1–0.2px from the engines' exact double. One part in ten thousand of the
# value covers that without covering any real disagreement.
CHROME_RELATIVE_TOLERANCE = 2.5e-4
# When Chrome agrees with neither side exactly, it can still LEAN: within
# LEAN_NEAR of one side while the other is whole pixels away and at least
# LEAN_FAR_FACTOR times farther. That is the shape of Blink's own residue —
# an inline rect rounded to the pixel, a text width off by a hundredth of an
# em — sitting on top of a real disagreement. A lean is reported apart from
# exact agreement (REF~ rather than REF!) and never overrides one.
LEAN_NEAR = 1.5
LEAN_FAR_FACTOR = 4.0
LEAN_FAR_MIN = 1.0


def lean(chrome_value, near_value, far_value):
    try:
        c, n, f = float(chrome_value), float(near_value), float(far_value)
    except (TypeError, ValueError):
        return False
    dn, df = abs(c - n), abs(c - f)
    return dn <= LEAN_NEAR and df >= LEAN_FAR_MIN and df >= LEAN_FAR_FACTOR * dn


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

    The engines emit inline fragments in the C# InsertChildFirst order while
    Chrome walks the DOM, so a <b> can sit a few slots away in Chrome's list;
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


def refine_runs(a, b, c, rc, ac):
    """Re-pairs runs of same-identity siblings against Chrome.

    Two bare <code>s in one line can come out in a different order on the two
    sides — the engines list fragments InsertChildFirst, Chrome walks the DOM
    — and positional pairing then judges each against the other's partner.
    For such a run, three pairings are tried: positional, nearest by the
    reference's geometry, nearest by the candidate's; the one under which
    Chrome agrees with EITHER side on the most values wins, positional on a
    tie. Neither side's geometry alone decides, so a wrong reference cannot
    drag Chrome's partners onto itself.
    """
    ia = [_identity(e) for e in a]

    def score(mapping):
        n = 0
        for k, t in mapping.items():
            for key in ("x", "y", "w", "h"):
                if chrome_agrees(c[t].get(key), a[k].get(key)):
                    n += 1
                elif k in rc and chrome_agrees(c[t].get(key), b[rc[k]].get(key)):
                    n += 1
        return n

    def nearest(run, targets, coords):
        cands = sorted((abs(coords[k][0] - float(c[t].get("x", 0))) +
                        abs(coords[k][1] - float(c[t].get("y", 0))), k, t)
                       for k in run for t in targets)
        m, taken_k, taken_t = {}, set(), set()
        for _, k, t in cands:
            if k in taken_k or t in taken_t:
                continue
            m[k] = t
            taken_k.add(k)
            taken_t.add(t)
        return m

    i = 0
    while i < len(ia):
        j = i + 1
        while j < len(ia) and ia[j] == ia[i]:
            j += 1
        run = [k for k in range(i, j) if k in ac]
        if len(run) > 1:
            targets = [ac[k] for k in run]
            options = [{k: ac[k] for k in run},
                       nearest(run, targets, {k: (float(a[k].get("x", 0)), float(a[k].get("y", 0)))
                                              for k in run})]
            if all(k in rc for k in run):
                options.append(nearest(run, targets,
                                       {k: (float(b[rc[k]].get("x", 0)), float(b[rc[k]].get("y", 0)))
                                        for k in run}))
            ac.update(max(options, key=score))
        i = j
    return ac


def arbitrate(reference, candidate, chrome):
    """Splits differences into ones the third source blames on each side.

    Returns (reference_bugs, leans, real) — lists of description strings. A
    difference counts as a reference bug only when Chrome's geometry matches
    the candidate (within CHROME_TOLERANCE) and differs from the reference, on
    an element all three agree on the identity of; it counts as a lean when
    Chrome is within LEAN_NEAR of the candidate and whole pixels from the
    reference (see lean). Anything else stays a real failure, including every
    difference on an element Chrome's capture has no partner for. Elements are
    paired by identity (see align), not by position; an element that merely
    sits elsewhere in the walk is not a difference.
    """
    a, b = reference.get("elements", []), candidate.get("elements", [])
    c = chrome.get("elements", []) if chrome else []
    same_order = len(a) == len(b) and all(_identity(x) == _identity(y) for x, y in zip(a, b))
    rc, moved = ({i: i for i in range(len(a))}, set()) if same_order else align(a, b)
    ac = refine_runs(a, b, c, rc, align(a, c)[0]) if chrome else {}

    reference_bugs, leans, real = [], [], []
    if len(a) != len(b):
        real.append(f"element count: reference {len(a)}, candidate {len(b)}")
    for i, ea in enumerate(a):
        if i not in rc:
            real.append(f"[{i}] {_identity(ea)}: only in the reference")
            continue
        eb = b[rc[i]]
        for key in ("depth", "tag", "id", "cls"):
            if ea.get(key) != eb.get(key):
                real.append(f"[{i}] {key}: reference {ea.get(key)!r}, candidate {eb.get(key)!r}")
        ec = c[ac[i]] if i in ac else None
        label = f"{ea.get('tag')}#{ea.get('id')}.{ea.get('cls')}".rstrip("#.")
        for key in ("x", "y", "w", "h"):
            if ea.get(key) == eb.get(key):
                continue
            line = f"[{i}] {label}: {key} reference {ea.get(key)} vs candidate {eb.get(key)}"
            if ec is None:
                real.append(line + (", no chrome partner" if chrome else ""))
            elif chrome_agrees(ec.get(key), eb.get(key)):
                reference_bugs.append(line + f", chrome {ec.get(key)} — chrome agrees with us")
            elif chrome_agrees(ec.get(key), ea.get(key)):
                real.append(line + f", chrome {ec.get(key)} — chrome agrees with the REFERENCE")
            elif lean(ec.get(key), eb.get(key), ea.get(key)):
                leans.append(line + f", chrome {ec.get(key)} — chrome leans to us")
            elif lean(ec.get(key), ea.get(key), eb.get(key)):
                real.append(line + f", chrome {ec.get(key)} — chrome leans to the REFERENCE")
            else:
                real.append(line + f", chrome {ec.get(key)} — chrome agrees with neither")
    paired_b = set(rc.values())
    for j, eb in enumerate(b):
        if j not in paired_b:
            real.append(f"[cand {j}] {_identity(eb)}: only in the candidate")
    return reference_bugs, leans, real


def reference_is_fresh(ref_out, html, css):
    """A reference dump counts as reusable when it is newer than its inputs.

    Deliberately NOT keyed on the BaselineGen binary: a C# change should be
    followed by a run without --reuse-reference, and the flag's help says so.
    """
    if not os.path.exists(ref_out):
        return False
    newest_input = os.path.getmtime(html)
    if css:
        newest_input = max(newest_input, os.path.getmtime(css))
    return os.path.getmtime(ref_out) >= newest_input


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


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("corpus", help="directory of .html cases, each with an optional .css")
    ap.add_argument("--width", type=int, default=800)
    ap.add_argument("--height", type=int, default=600)
    ap.add_argument("--dotnet", default="dotnet")
    ap.add_argument("--baselinegen", default="Tools/BaselineGen")
    ap.add_argument("--weva-dump", default="godot-port/build/tools/weva_dump/weva_dump")
    ap.add_argument("--out-dir", default="/tmp/oracle-run")
    ap.add_argument("--only", help="run just the cases whose name contains this")
    ap.add_argument("--quiet", action="store_true", help="only list failures")
    ap.add_argument("--reuse-reference", action="store_true",
                    help="skip BaselineGen when a reference dump newer than the case exists; "
                         "the reference only changes when the corpus or the C# does, and the "
                         ".NET start-up per case is most of a run")
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    cases = cases_in(args.corpus)
    if args.only:
        cases = [c for c in cases if args.only in c[0]]
    if not cases:
        print(f"no cases in {args.corpus}")
        return 1

    passed, failed, errored, arbitrated = [], [], [], []
    for name, html, css in cases:
        ref_out = os.path.join(args.out_dir, name + ".ref.json")
        cand_out = os.path.join(args.out_dir, name + ".cand.json")

        if args.reuse_reference and reference_is_fresh(ref_out, html, css):
            r = None
        else:
            r = run_reference(args.dotnet, args.baselinegen, html, css, args.width, args.height,
                              ref_out)
        c = run_candidate(args.weva_dump, html, css, args.width, args.height, cand_out)
        if r is not None and (r.returncode != 0 or not os.path.exists(ref_out)):
            errored.append((name, "reference: " + (r.stderr or r.stdout).strip()[:300]))
            continue
        if c.returncode != 0 or not os.path.exists(cand_out):
            errored.append((name, "candidate: " + (c.stderr or c.stdout).strip()[:300]))
            continue

        chrome = load_chrome(args.corpus, name)
        reference_bugs, leans, problems = arbitrate(load(ref_out), load(cand_out), chrome)
        if (reference_bugs or leans) and not problems:
            arbitrated.append((name, reference_bugs + leans))
            if leans:
                print(f"REF~ {name}  ({len(reference_bugs)} difference(s) chrome sides with us on, "
                      f"{len(leans)} it leans to us on)")
            else:
                print(f"REF! {name}  ({len(reference_bugs)} difference(s), chrome sides with us)")
            for line in (reference_bugs + leans)[:6]:
                print(f"       {line}")
        elif problems:
            failed.append((name, problems))
            # With a Chrome capture the differences split by whose side Chrome
            # takes: the ones where it agrees with the reference are the port's
            # bugs, the ones where it agrees with the port are already listed
            # above as reference bugs, and the rest are undecided.
            with_reference = sum(1 for p in problems if "agrees with the REFERENCE" in p)
            leans_reference = sum(1 for p in problems if "leans to the REFERENCE" in p)
            neither = sum(1 for p in problems if "agrees with neither" in p)
            breakdown = ""
            if chrome:
                breakdown = (f"; chrome sides with the reference on {with_reference}"
                             f" (+{leans_reference} leaning), with neither on {neither}, "
                             f"with us on {len(reference_bugs)} (+{len(leans)} leaning)")
            print(f"FAIL {name}  ({len(problems)} difference(s){breakdown})")
            for line in problems[:12]:
                print(f"       {line}")
            if len(problems) > 12:
                print(f"       ... and {len(problems) - 12} more")
        else:
            passed.append(name)
            if not args.quiet:
                print(f"ok   {name}")

    total = len(cases)
    print(f"\n{len(passed)}/{total} agree, {len(failed)} differ, "
          f"{len(arbitrated)} reference bugs, {len(errored)} errored")
    for name, why in arbitrated:
        print(f"  REFERENCE BUG {name}: {len(why)} value(s) where chrome matches the candidate")
    for name, why in errored:
        print(f"  ERROR {name}: {why}")
    # An arbitrated difference is not a failure: the candidate matches the third
    # source. It is still printed, so it cannot be forgotten.
    return 0 if not failed and not errored else 1


if __name__ == "__main__":
    sys.exit(main())
