#!/usr/bin/env python3
"""Layout dumps from the Unity host, compared with weva_dump on a corpus.

Step 7 of the shared-core plan: the same core hosted by Unity must lay every
page out exactly as the weva_dump tool does (it is the same core; any
difference is the Unity adapter's). Both sides use the oracle's synthetic
face (SyntheticFontBackend in Unity, MonoFontMetrics in weva_dump).
Animations and transitions are disabled on both sides for a stable snapshot.

    python oracle_from_unity.py <corpus> --weva-dump <path or "wsl:~/weva/build-gcc/Tools/weva_dump/weva_dump">
        [--unity <Unity.exe>] [--project <repo>] [--width 1280 --height 720] [--out .utmp/oracle-unity]

The Unity side runs once for the whole corpus: a manifest lists the cases
and the EditMode test NativeLayoutDumpTests.Manifest_DumpsEveryCaseForTheOracle
dumps each one. weva_dump runs per case (through WSL when the path starts
with "wsl:"). The comparison is run_oracle.py's own compare(): identity
first (depth, tag, id, class), then geometry at four decimals.
This checks host translation only. Chrome conformance is checked separately by
Tools/oracle/chrome_sweep.py --chrome-metrics --max-worst 1.5.
"""
import argparse
import json
import subprocess
import sys
from pathlib import Path
from oracle_support import (DEFAULT_UNITY, REPO, frozen_stylesheet, hidden_process_options,
                            preserve_previous, run_unity_manifest)

sys.path.insert(0, str(REPO / 'Tools' / 'oracle'))
from run_oracle import compare, cases_in  # noqa: E402

def to_wsl(path: Path) -> str:
    p = str(path.resolve()).replace('\\', '/')
    if len(p) > 1 and p[1] == ':':
        p = '/mnt/' + p[0].lower() + p[2:]
    return p


def run_weva_dump(tool: str, html: Path, css, width, height, out: Path):
    preserve_previous(out)
    if tool.startswith('wsl:'):
        binary = tool[4:]
        if binary.startswith('~/'):
            home_dir = subprocess.check_output(['wsl', '--exec', 'printenv', 'HOME'],
                                               text=True, **hidden_process_options()).strip()
            binary = home_dir + binary[1:]
        cmd = ['wsl', '--exec', binary, to_wsl(html), str(width), str(height), to_wsl(out)]
        if css:
            cmd.append(to_wsl(Path(css)))
    else:
        cmd = [tool, str(html), str(width), str(height), str(out)]
        if css:
            cmd.append(str(css))
    return subprocess.run(cmd, capture_output=True, text=True, **hidden_process_options())


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('corpus')
    ap.add_argument('--weva-dump', required=True)
    ap.add_argument('--unity', default=DEFAULT_UNITY)
    ap.add_argument('--project', default=str(REPO))
    ap.add_argument('--width', type=int, default=1280)
    ap.add_argument('--height', type=int, default=720)
    ap.add_argument('--out', default='.utmp/oracle-unity')
    ap.add_argument('--skip-unity', action='store_true', help='reuse the Unity dumps already in --out')
    args = ap.parse_args(argv)
    if args.width <= 0 or args.height <= 0:
        ap.error('viewport dimensions must be positive')

    corpus = Path(args.corpus).resolve()
    out = Path(args.out).resolve()
    (out / 'unity').mkdir(parents=True, exist_ok=True)
    (out / 'core').mkdir(parents=True, exist_ok=True)
    (out / 'inputs').mkdir(parents=True, exist_ok=True)
    cases = cases_in(str(corpus))
    if not cases:
        print('no cases in', corpus)
        return 2

    manifest = out / 'manifest.tsv'
    lines = []
    staged_cases = []
    for name, html, css in cases:
        staged_css = out / 'inputs' / (name + '.css')
        css_text = frozen_stylesheet(css)
        if args.skip_unity:
            if not staged_css.exists() or staged_css.read_text(encoding='utf-8') != css_text:
                print('FAIL --skip-unity requires unchanged stylesheets:', name)
                return 1
        else:
            preserve_previous(staged_css)
            staged_css.write_text(css_text, encoding='utf-8')
        staged_cases.append((name, html, staged_css))
        lines.append('\t'.join([str(html), str(staged_css), str(args.width), str(args.height), str(out / 'unity' / (name + '.json'))]))
    cases = staged_cases
    manifest_text = '\n'.join(lines) + '\n'
    if args.skip_unity:
        if not manifest.exists() or manifest.read_text(encoding='utf-8') != manifest_text:
            print('FAIL --skip-unity requires the same saved corpus, viewport and output paths')
            return 1
        print('reusing the saved Unity dumps (--skip-unity); inputs must be unchanged')
    else:
        preserve_previous(manifest)
        manifest.write_text(manifest_text, encoding='utf-8')

    if not args.skip_unity:
        results = out / 'unity-results.xml'
        print('running Unity for', len(cases), 'cases ...')
        try:
            run_unity_manifest(args.unity, Path(args.project).resolve(), manifest,
                               results, out / 'unity.log')
        except (OSError, RuntimeError) as error:
            print('FAIL', error)
            return 1

    agreeing, differing, missing = 0, 0, 0
    for name, html, css in cases:
        unity_json = out / 'unity' / (name + '.json')
        core_json = out / 'core' / (name + '.json')
        r = run_weva_dump(args.weva_dump, Path(html), css, args.width, args.height, core_json)
        if r.returncode != 0 or not core_json.exists():
            print(f'{name}: weva_dump failed: {r.stderr.strip()[:200]}')
            missing += 1
            continue
        if not unity_json.exists():
            print(f'{name}: no Unity dump')
            missing += 1
            continue
        with open(core_json, encoding='utf-8') as f:
            reference = json.load(f)
        with open(unity_json, encoding='utf-8') as f:
            candidate = json.load(f)
        problems = compare(reference, candidate)
        if problems:
            differing += 1
            print(f'{name}: {len(problems)} difference(s)')
            for p in problems[:6]:
                print('   ', p)
        else:
            agreeing += 1
    print(f'\n{agreeing} agree, {differing} differ, {missing} missing of {len(cases)} cases')
    return 0 if differing == 0 and missing == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
