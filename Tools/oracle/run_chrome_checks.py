#!/usr/bin/env python3
"""Run every scripted Chrome behaviour check and fail on any that fails.

Tools/oracle/check_*_chrome.{cjs,py} each drive a real Chrome through
puppeteer against a fixture and record what the browser does -- focus order,
popover chains, number-input stepping, table border junctions, selection,
animation clocks. They exist because layout geometry from a capture cannot
pin behaviour, and each one has a matching test_*.cpp sequence in the core
suite whose expectations came from it.

Both check.sh and CI invoke this runner. It runs every matching script and
turns the results into one exit code:

  * Scripts that exit nonzero on a mismatch are gates here.
  * Some .cjs only print what Chrome did. They are evidence, not verdicts; a
    human read them into a C++ test once. Here they must at least run to
    completion, and their output is kept as a receipt so a Chrome upgrade that
    changes an answer is visible.

    python Tools/oracle/run_chrome_checks.py [--only NAME] [--out DIR]

A script named in known-gaps/chrome-checks.txt (`name: reason`) may fail; the
run reports such an entry the moment it stops being needed, so the file cannot
quietly accumulate.

Needs node with puppeteer resolvable from Tools/oracle and a Chrome. One
Chrome is used for every script so receipts do not mix browser versions:
WEVA_CHROME, else the first installed candidate below, else puppeteer's
bundled build. Exit 1 on any unexcused failure.

Install the root package-lock.json with npm ci. Every .cjs uses the shared
chrome_test_browser.cjs launcher and that root Puppeteer dependency. CI pins
Windows Chrome 152 to match the retained editing receipts: older Chrome's
CR handling and Linux's decimal-comma handling differ. --no-sandbox (or
WEVA_CHROME_NO_SANDBOX=1) applies to both the CJS and Python checks.
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
    env = os.environ.get("WEVA_CHROME")
    if env:
        return env
    for c in CHROME_CANDIDATES:
        if os.path.exists(c):
            return c
    try:
        bundled = subprocess.run(['node', '-p', "require('puppeteer').executablePath()"],
                                 cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
        if os.path.exists(bundled):
            return bundled
    except (OSError, subprocess.CalledProcessError):
        pass
    return None


def argv_for(path, chrome, out_dir, no_sandbox=False):
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
        if no_sandbox:
            cmd.append("--no-sandbox")
        return cmd
    with open(path, encoding="utf-8", errors="replace") as f:
        src = f.read()
    chrome_first = "executablePath:process.argv[2]" in src.replace(" ", "")
    if chrome_first:
        return ["node", path] + ([chrome] if chrome else [])
    cmd = ["node", path, os.path.join(out_dir, stem + ".json")]
    # The shared launcher reads WEVA_CHROME. argv[3] is not universally a
    # browser path (the intrinsic-select probe uses it for a font file).
    return cmd


def read_known(path):
    known = {}
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#") and ":" in line:
                    k, v = line.split(":", 1)
                    known[k.strip()] = v.strip()
    return known


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", help="run only scripts whose name contains this")
    ap.add_argument("--out", default=os.path.join(ROOT, ".utmp", "chrome-checks"),
                    help="directory for per-script output receipts")
    ap.add_argument("--known-failing", default=os.path.join(HERE, "known-gaps", "chrome-checks.txt"),
                    help="`script-name: reason` lines allowed to fail")
    ap.add_argument("--no-sandbox", action="store_true",
                    default=os.environ.get('WEVA_CHROME_NO_SANDBOX') == '1',
                    help="disable the Chrome sandbox for isolated CI/root environments")
    a = ap.parse_args()
    # Children run in the receipt directory. Resolve the CLI path before
    # changing their cwd, so --out .utmp/checks is not joined to itself.
    a.out = os.path.abspath(a.out)

    scripts = sorted(glob.glob(os.path.join(HERE, "check_*_chrome.cjs"))
                     + glob.glob(os.path.join(HERE, "check_*_chrome.py")))
    if a.only:
        scripts = [s for s in scripts if a.only in os.path.basename(s)]
    if not scripts:
        print("no scripts matched", file=sys.stderr)
        return 2
    os.makedirs(a.out, exist_ok=True)
    known = read_known(a.known_failing)

    chrome = find_chrome()
    print("chrome:", chrome or "(puppeteer's bundled build)")
    child_env = os.environ.copy()
    if chrome:
        child_env['WEVA_CHROME'] = chrome
    if a.no_sandbox:
        child_env['WEVA_CHROME_NO_SANDBOX'] = '1'
    failed, excused, passed = [], [], 0
    for path in scripts:
        name = os.path.basename(path)
        cmd = argv_for(path, chrome, a.out, a.no_sandbox)
        t0 = time.time()
        r = subprocess.run(cmd, cwd=a.out, env=child_env, capture_output=True, text=True,
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
        elif name in known:
            excused.append(name)
            print("known %-46s %5.1fs  exit %d  %s" % (name, dt, r.returncode, known[name][:70]))
        else:
            failed.append(name)
            tail = (r.stderr.strip().splitlines() or r.stdout.strip().splitlines() or [""])[-1][:100]
            print("FAIL  %-46s %5.1fs  exit %d  %s" % (name, dt, r.returncode, tail))

    ran = {os.path.basename(p) for p in scripts}
    stale = [k for k in known if k in ran and k not in excused]
    print("\n%d scripts: %d passed, %d failed, %d known-failing%s; receipts in %s"
          % (len(scripts), passed, len(failed), len(excused),
             ("; %d known-failing entries no longer needed: %s" % (len(stale), ", ".join(stale))) if stale else "",
             os.path.relpath(a.out, ROOT)))
    for name in failed:
        print("FAIL", name)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
