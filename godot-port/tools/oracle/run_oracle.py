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
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    cases = cases_in(args.corpus)
    if args.only:
        cases = [c for c in cases if args.only in c[0]]
    if not cases:
        print(f"no cases in {args.corpus}")
        return 1

    passed, failed, errored = [], [], []
    for name, html, css in cases:
        ref_out = os.path.join(args.out_dir, name + ".ref.json")
        cand_out = os.path.join(args.out_dir, name + ".cand.json")

        r = run_reference(args.dotnet, args.baselinegen, html, css, args.width, args.height,
                          ref_out)
        c = run_candidate(args.weva_dump, html, css, args.width, args.height, cand_out)
        if r.returncode != 0 or not os.path.exists(ref_out):
            errored.append((name, "reference: " + (r.stderr or r.stdout).strip()[:300]))
            continue
        if c.returncode != 0 or not os.path.exists(cand_out):
            errored.append((name, "candidate: " + (c.stderr or c.stdout).strip()[:300]))
            continue

        problems = compare(load(ref_out), load(cand_out))
        if problems:
            failed.append((name, problems))
            print(f"FAIL {name}  ({len(problems)} difference(s))")
            for line in problems[:12]:
                print(f"       {line}")
            if len(problems) > 12:
                print(f"       ... and {len(problems) - 12} more")
        else:
            passed.append(name)
            if not args.quiet:
                print(f"ok   {name}")

    total = len(cases)
    print(f"\n{len(passed)}/{total} agree, {len(failed)} differ, {len(errored)} errored")
    for name, why in errored:
        print(f"  ERROR {name}: {why}")
    return 0 if not failed and not errored else 1


if __name__ == "__main__":
    sys.exit(main())
