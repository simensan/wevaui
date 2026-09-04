#!/usr/bin/env python3
"""Compares the C++ engine against Chrome ALONE, with no C# reference.

The gate (run_oracle.py) is a three-way: reference versus candidate, with
Chrome arbitrating. That needs BaselineGen, and it therefore only ever runs
over the 47 cases in corpus/samples. corpus/harvest holds 210 more, each with
a Chrome capture already beside it, and nothing looks at them.

This does the cheap two-way instead: lay the case out and see whether Chrome
agrees. It cannot tell a port bug from a place where the C# reference and
Chrome differ by design -- only the three-way can -- so its output is a list
of LEADS, not a gate. It is for finding cases worth promoting into samples.

    python3 chrome_sweep.py corpus/harvest --weva-dump <path> --width 1280
"""

import argparse
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_oracle import align, chrome_agrees, load_chrome   # noqa: E402

KEYS = ("x", "y", "w", "h")


def compare(case, corpus, weva_dump, width, height, out_dir):
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
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        return (case, "CRASH", r.stderr.strip().splitlines()[-1:] or [""], 0)

    with open(out) as f:
        ours = json.load(f).get("elements", [])
    theirs = chrome.get("elements", [])
    pairs, _ = align(ours, theirs)
    bad = []
    for i, j in sorted(pairs.items()):
        for k in KEYS:
            if not chrome_agrees(theirs[j].get(k), ours[i].get(k)):
                e = ours[i]
                bad.append("%s%s%s .%s: ours %s, chrome %s"
                           % (e.get("tag"), "#" + e["id"] if e.get("id") else "",
                              "." + e["cls"] if e.get("cls") else "", k,
                              ours[i].get(k), theirs[j].get(k)))
    unpaired = len(ours) - len(pairs)
    return (case, "DIFF" if bad else "ok", bad, unpaired)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("corpus")
    ap.add_argument("--weva-dump", required=True)
    ap.add_argument("--width", type=int, default=1280)
    ap.add_argument("--height", type=int, default=720)
    ap.add_argument("--out-dir", default="/tmp/chrome-sweep")
    ap.add_argument("--only")
    ap.add_argument("--show", type=int, default=3, help="differing values to print per case")
    a = ap.parse_args()
    os.makedirs(a.out_dir, exist_ok=True)

    cases = sorted(f[:-5] for f in os.listdir(a.corpus) if f.endswith(".html"))
    if a.only:
        cases = [c for c in cases if a.only in c]

    diffs, crashes, clean, skipped = [], [], 0, 0
    for case in cases:
        r = compare(case, a.corpus, a.weva_dump, a.width, a.height, a.out_dir)
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

    diffs.sort(key=lambda d: -len(d[1]))
    for name, bad, unpaired in diffs:
        print("DIFF %-44s %4d value(s)%s" % (name, len(bad),
              ", %d unpaired" % unpaired if unpaired else ""))
        for line in bad[:a.show]:
            print("       ", line)
    for name, err in crashes:
        print("CRASH %-43s %s" % (name, err[0] if err else ""))
    print("\n%d cases: %d agree with chrome, %d differ, %d crash, %d without a capture"
          % (len(cases), clean, len(diffs), len(crashes), skipped))


if __name__ == "__main__":
    main()
