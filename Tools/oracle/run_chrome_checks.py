#!/usr/bin/env python3
"""Run every scripted Chrome behaviour check and fail on any that fails.

Tools/oracle/check_*_chrome.{cjs,py} each drive a real Chrome through
puppeteer against a fixture and record what the browser does -- focus order,
popover chains, number-input stepping, table border junctions, selection,
animation clocks. They exist because layout geometry from a capture cannot
pin behaviour, and each one has a matching test_*.cpp sequence in the core
suite whose expectations came from it.

Nothing ran them. 57 scripts, referenced by neither check.sh nor CI, run by
hand when someone remembered. This runs all of them and turns the result into
one exit code:

  * 33 of them exit nonzero on a mismatch (29 .cjs, all 4 .py). Those are
    gates here.
  * 24 .cjs only print what Chrome did. They are evidence, not verdicts; a
    human read them into a C++ test once. Here they must at least run to
    completion, and their output is kept as a receipt so a Chrome upgrade that
    changes an answer is visible.

    python Tools/oracle/run_chrome_checks.py [--only NAME] [--out DIR]

Needs node with puppeteer resolvable from Tools/oracle (the repo root's
node_modules) and a Chrome that puppeteer can launch. Exit 1 on any failure.
"""
import argparse
import glob
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

CHROME_CANDIDATES = [
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
    "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
]


def find_chrome():
    """One Chrome for every script, so the receipts do not mix browser versions.
    WEVA_CHROME wins; otherwise the first installed candidate; otherwise None
    and the .cjs scripts fall back to whatever puppeteer bundles."""
    env = os.environ.get("WEVA_CHROME")
    if env:
        return env
    for c in CHROME_CANDIDATES:
        if os.path.exists(c):
            return c
    return None


def argv_for(path, chrome, out_dir):
    """The scripts grew one at a time and disagree about argv. Two shapes exist
    for the .cjs: `argv[2]` is the Chrome path, or `argv[2]` is an output file
    with the Chrome path optional in `argv[3]`. Which one is read off the
    script's own source rather than maintained in a table here."""
    name = os.path.basename(path)
    stem = name.rsplit(".", 1)[0]
    if path.endswith(".py"):
        cmd = [sys.executable, path, "--output", os.path.join(out_dir, stem + ".json")]
        if chrome:
            cmd += ["--chrome", chrome]
        return cmd
    with open(path, encoding="utf-8", errors="replace") as f:
        src = f.read()
    chrome_first = "executablePath:process.argv[2]" in src.replace(" ", "")
    if chrome_first:
        return ["node", path] + ([chrome] if chrome else [])
    cmd = ["node", path, os.path.join(out_dir, stem + ".json")]
    if chrome and "process.argv[3]" in src:
        cmd.append(chrome)
    return cmd


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", help="run only scripts whose name contains this")
    ap.add_argument("--out", default=os.path.join(ROOT, ".utmp", "chrome-checks"),
                    help="directory for per-script output receipts")
    a = ap.parse_args()

    scripts = sorted(glob.glob(os.path.join(HERE, "check_*_chrome.cjs"))
                     + glob.glob(os.path.join(HERE, "check_*_chrome.py")))
    if a.only:
        scripts = [s for s in scripts if a.only in os.path.basename(s)]
    if not scripts:
        print("no scripts matched", file=sys.stderr)
        return 2
    os.makedirs(a.out, exist_ok=True)

    chrome = find_chrome()
    print("chrome:", chrome or "(puppeteer's bundled build)")
    failed, passed = [], 0
    for path in scripts:
        name = os.path.basename(path)
        cmd = argv_for(path, chrome, a.out)
        t0 = time.time()
        r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        dt = time.time() - t0
        with open(os.path.join(a.out, name + ".log"), "w", encoding="utf-8", newline="\n") as f:
            f.write(r.stdout)
            if r.stderr:
                f.write("\n--- stderr ---\n" + r.stderr)
        last = (r.stdout.strip().splitlines() or [""])[-1][:100]
        if r.returncode == 0:
            passed += 1
            print("ok    %-46s %5.1fs  %s" % (name, dt, last))
        else:
            failed.append(name)
            tail = (r.stderr.strip().splitlines() or r.stdout.strip().splitlines() or [""])[-1][:100]
            print("FAIL  %-46s %5.1fs  exit %d  %s" % (name, dt, r.returncode, tail))

    print("\n%d scripts: %d passed, %d failed; receipts in %s"
          % (len(scripts), passed, len(failed), os.path.relpath(a.out, ROOT)))
    for name in failed:
        print("FAIL", name)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
