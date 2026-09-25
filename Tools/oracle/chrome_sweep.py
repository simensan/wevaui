#!/usr/bin/env python3
"""Compare core layout with tracked Chrome captures.

--chrome-metrics uses browser box semantics and synthetic font extents.
--max-worst enables the gate: every case needs a capture, no unmatched visible
elements, and geometry within the ceiling (or an explicit known-gaps entry).
Without a ceiling this prints investigation leads. Chrome is the only reference.

    python3 chrome_sweep.py corpus/harvest --weva-dump <path> \
        --width 800 --height 600 --chrome-metrics --max-worst 1.5
"""

import argparse
import json
import re
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_oracle import align, chrome_agrees, load_chrome   # noqa: E402

KEYS = ("x", "y", "w", "h")


def _ident(e):
    return (e.get("tag"), e.get("id"), e.get("cls"))


def repair_runs(ours, theirs, pairs):
    """Re-pairs runs of same-identity siblings by position, in place.

    `align` pairs by index within a run of identical identities, and the two
    sides do not always list such a run in the same order -- the engines emit
    inline fragments InsertChildFirst while Chrome walks the DOM. Positional
    pairing then judges each element against the other's partner and reports
    both as wrong, twice over and by the distance between them.

    That is not a subtle effect. Two <code> siblings in 9slice-demo read as
    `ours 971 vs chrome 231` and `ours 231 vs chrome 971` -- an 826px
    disagreement that is entirely the pairing. Re-pair the matching sibling
    runs before measuring geometry differences.
    """
    runs = []
    start = 0
    for i in range(1, len(ours) + 1):
        if i == len(ours) or _ident(ours[i]) != _ident(ours[start]):
            if i - start > 1:
                runs.append(range(start, i))
            start = i
    for run in runs:
        mine = [i for i in run if i in pairs]
        if len(mine) < 2:
            continue
        theirs_idx = sorted(pairs[i] for i in mine)
        # Both sides sorted by where they actually sit, then zipped. A run whose
        # order already matches is unchanged by this.
        mine.sort(key=lambda i: (round(float(ours[i].get("y") or 0), 3),
                                 round(float(ours[i].get("x") or 0), 3)))
        theirs_idx.sort(key=lambda j: (round(float(theirs[j].get("y") or 0), 3),
                                       round(float(theirs[j].get("x") or 0), 3)))
        for i, j in zip(mine, theirs_idx):
            pairs[i] = j


def compare(case, corpus, weva_dump, width, height, out_dir, chrome_metrics=False):
    html = os.path.join(corpus, case + ".html")
    css = os.path.join(corpus, case + ".css")
    if not os.path.exists(css):
        css = ""
    chrome = load_chrome(corpus, case)
    if not chrome:
        return None
    out = os.path.join(out_dir, case + ".ours.json")
    cmd = [weva_dump, html, str(width), str(height), out]
    if css:
        cmd.append(css)
    if chrome_metrics:
        cmd.append('--chrome-metrics')
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        return (case, "CRASH", r.stderr.strip().splitlines()[-1:] or [""], 0)

    with open(out) as f:
        ours = json.load(f).get("elements", [])
    theirs = chrome.get("elements", [])
    # A <br> has no layout of its own to arbitrate, and Blink reports one
    # that sits in a later column of a multicol container at the x of the
    # paragraph's FIRST fragment (the 61-69 multicol probes in the hand
    # corpus): its rect is a quirk of getBoundingClientRect, not a
    # measurement, so it is left out on both sides.
    ours = [e for e in ours if e.get("tag") != "br"]
    theirs = [e for e in theirs if e.get("tag") != "br"]
    if chrome_metrics and all(e.get("path") for e in ours) and all(e.get("path") for e in theirs):
        paths = {e["path"]: j for j, e in enumerate(theirs)}
        pairs = {i: paths[e["path"]] for i, e in enumerate(ours) if e["path"] in paths}
    else:
        pairs, _ = align(ours, theirs)
        repair_runs(ours, theirs, pairs)
    # Sorted by how FAR apart they are, not by document order.
    #
    # The whole value of this tool is that a page's largest disagreement tells
    # you what kind of problem it has: 1 to 10px is the font-metrics gap, 100+
    # is a real divergence. Listing the first few in element order buries that
    # under whatever happened to be at the top of the page.
    scored = []
    for i, j in sorted(pairs.items()):
        for k in KEYS:
            if chrome_agrees(theirs[j].get(k), ours[i].get(k)):
                continue
            e = ours[i]
            try:
                delta = abs(float(ours[i].get(k)) - float(theirs[j].get(k)))
            except (TypeError, ValueError):
                delta = 0.0
            scored.append((delta, "%s%s%s .%s: ours %s, chrome %s  (%.1fpx)"
                           % (e.get("tag"), "#" + e["id"] if e.get("id") else "",
                              "." + e["cls"] if e.get("cls") else "", k,
                              ours[i].get(k), theirs[j].get(k), delta)))
    scored.sort(key=lambda t: -t[0])
    bad = [line for _, line in scored]
    matched = set(pairs.values())
    missing_ours = [i for i in range(len(ours)) if i not in pairs]
    # display:contents has no principal box. Hidden descendants likewise have
    # no observable geometry; every unmatched nonempty browser rect is a finding.
    missing_theirs = [j for j, e in enumerate(theirs) if j not in matched
                      and e.get("display") != "contents"
                      and (float(e.get("w") or 0) != 0 or float(e.get("h") or 0) != 0)]
    unpaired = len(missing_ours) + len(missing_theirs)
    for side, elements, indices in (("candidate", ours, missing_ours), ("Chrome", theirs, missing_theirs)):
        for i in indices:
            e = elements[i]
            bad.append("Unmatched %s element %s (%s)" % (side, e.get("path", ""), _ident(e)))
    return (case, "DIFF" if bad else "ok", bad, unpaired)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("corpus")
    ap.add_argument("--weva-dump", required=True)
    ap.add_argument("--width", type=int, default=1280)
    ap.add_argument("--height", type=int, default=720)
    ap.add_argument("--out-dir", default="/tmp/chrome-sweep")
    ap.add_argument("--only")
    ap.add_argument("--show", type=int, default=3, help="differing values to print per case")
    ap.add_argument("--chrome-metrics", action="store_true", help="Use browser-rounded synthetic font extents")
    # Gate mode. Without these the tool is a lead generator, as ORACLE.md
    # describes; with them it is the conformance gate, and Chrome is the only
    # side of the comparison. A case passes when its WORST value is within
    # --max-worst of Chrome and it has no unmatched element; anything else has
    # to be named in the known-gaps file with a cause, or the run fails.
    ap.add_argument("--max-worst", type=float, default=None,
                    help="per-case ceiling in px on the largest disagreement; enables the gate")
    ap.add_argument("--known-gaps", default=None,
                    help="text file of `case-name: reason` lines allowed to exceed the ceiling")
    a = ap.parse_args(argv)
    os.makedirs(a.out_dir, exist_ok=True)

    cases = sorted(f[:-5] for f in os.listdir(a.corpus) if f.endswith(".html"))
    if a.only:
        cases = [c for c in cases if a.only in c]
    if not cases:
        print("FAIL no matching HTML cases in", a.corpus)
        return 2

    diffs, crashes, clean, skipped = [], [], 0, 0
    for case in cases:
        r = compare(case, a.corpus, a.weva_dump, a.width, a.height, a.out_dir, a.chrome_metrics)
        if r is None:
            skipped += 1
            continue
        name, verdict, bad, unpaired = r
        if verdict == "CRASH":
            crashes.append((name, bad))
        elif verdict == "DIFF":
            diffs.append((name, bad, unpaired))
        else:
            clean += 1

    # Cases ordered by their WORST disagreement, for the same reason.
    def worst_of(bad):
        m = 0.0
        for line in bad:
            hit = re.search(r"\(([\d.]+)px\)$", line)
            if hit:
                m = max(m, float(hit.group(1)))
        return m

    diffs.sort(key=lambda d: -worst_of(d[1]))
    for name, bad, unpaired in diffs:
        print("DIFF %-40s worst %7.1fpx  %4d value(s)%s"
              % (name, worst_of(bad), len(bad),
                 ", %d unpaired" % unpaired if unpaired else ""))
        for line in bad[:a.show]:
            print("       ", line)
    for name, err in crashes:
        print("CRASH %-43s %s" % (name, err[0] if err else ""))
    print("\n%d cases: %d agree with chrome, %d differ, %d crash, %d without a capture"
          % (len(cases), clean, len(diffs), len(crashes), skipped))

    if a.max_worst is None:
        return 0
    known = {}
    if a.known_gaps and os.path.exists(a.known_gaps):
        with open(a.known_gaps) as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#") or ":" not in line:
                    continue
                name, reason = line.split(":", 1)
                known[name.strip()] = reason.strip()
    failed = []
    for name, bad, unpaired in diffs:
        over = worst_of(bad) > a.max_worst or unpaired > 0
        if over and name not in known:
            failed.append("%s (worst %.1fpx%s)" % (name, worst_of(bad),
                                                  ", %d unpaired" % unpaired if unpaired else ""))
    for name, _ in crashes:
        if name not in known:
            failed.append("%s (crash)" % name)
    flagged = set(d[0] for d in diffs) | set(c[0] for c in crashes)
    excused = [n for n in known if n in flagged]
    stale = [n for n in known if n in cases and n not in flagged]
    print("GATE ceiling %.1fpx: %d over, %d excused by known-gaps%s"
          % (a.max_worst, len(failed), len(excused),
             ("; %d known-gaps entries no longer needed: %s" % (len(stale), ", ".join(stale))) if stale else ""))
    if skipped:
        failed.append("%d case(s) without a capture" % skipped)
    if failed:
        for f in failed:
            print("FAIL", f)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
