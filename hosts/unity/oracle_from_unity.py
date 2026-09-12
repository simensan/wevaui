#!/usr/bin/env python3
"""Layout dumps from the Unity host, compared with weva_dump on a corpus.

Step 7 of the shared-core plan: the same core hosted by Unity must lay every
page out exactly as the weva_dump tool does (it is the same core; any
difference is the Unity adapter's). Both sides use the oracle's synthetic
face (SyntheticFontBackend in Unity, MonoFontMetrics in weva_dump).

    python oracle_from_unity.py <corpus> --weva-dump <path or "wsl:~/weva/build-gcc/tools/weva_dump/weva_dump">
        [--unity <Unity.exe>] [--project <repo>] [--width 1280 --height 720] [--out .utmp/oracle-unity]

The Unity side runs once for the whole corpus: a manifest lists the cases
and the EditMode test NativeLayoutDumpTests.Manifest_DumpsEveryCaseForTheOracle
dumps each one. weva_dump runs per case (through WSL when the path starts
with "wsl:"). The comparison is run_oracle.py's own compare(): identity
first (depth, tag, id, class), then geometry at four decimals.
"""
import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / 'tools' / 'oracle'))
from run_oracle import compare, cases_in  # noqa: E402

DEFAULT_UNITY = r'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe'


def to_wsl(path: Path) -> str:
    p = str(path.resolve()).replace('\\', '/')
    if len(p) > 1 and p[1] == ':':
        p = '/mnt/' + p[0].lower() + p[2:]
    return p


def run_weva_dump(tool: str, html: Path, css, width, height, out: Path):
    if tool.startswith('wsl:'):
        binary = tool[4:]
        cmd = [binary, to_wsl(html), str(width), str(height), to_wsl(out)]
        if css:
            cmd.append(to_wsl(Path(css)))
        return subprocess.run(['wsl', '-e', 'bash', '-lc', ' '.join(f"'{c}'" for c in cmd)], capture_output=True, text=True)
    cmd = [tool, str(html), str(width), str(height), str(out)]
    if css:
        cmd.append(str(css))
    return subprocess.run(cmd, capture_output=True, text=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('corpus')
    ap.add_argument('--weva-dump', required=True)
    ap.add_argument('--unity', default=DEFAULT_UNITY)
    ap.add_argument('--project', default=str(HERE.parent.parent.parent))
    ap.add_argument('--width', type=int, default=1280)
    ap.add_argument('--height', type=int, default=720)
    ap.add_argument('--out', default='.utmp/oracle-unity')
    ap.add_argument('--skip-unity', action='store_true', help='reuse the Unity dumps already in --out')
    args = ap.parse_args()

    corpus = Path(args.corpus).resolve()
    out = Path(args.out).resolve()
    (out / 'unity').mkdir(parents=True, exist_ok=True)
    (out / 'core').mkdir(parents=True, exist_ok=True)
    cases = cases_in(str(corpus))
    if not cases:
        print('no cases in', corpus)
        return 2

    manifest = out / 'manifest.tsv'
    lines = []
    for name, html, css in cases:
        lines.append('\t'.join([str(html), str(css or ''), str(args.width), str(args.height), str(out / 'unity' / (name + '.json'))]))
    manifest.write_text('\n'.join(lines) + '\n', encoding='utf-8')

    if not args.skip_unity:
        env = dict(os.environ, WEVA_NATIVE_DUMP_MANIFEST=str(manifest))
        results = out / 'unity-results.xml'
        cmd = [args.unity, '-batchmode', '-nographics', '-projectPath', args.project, '-runTests', '-testPlatform', 'EditMode',
               '-testFilter', 'Weva.Tests.EditorTests.Native.NativeLayoutDumpTests.Manifest_DumpsEveryCaseForTheOracle',
               '-testResults', str(results), '-logFile', str(out / 'unity.log')]
        print('running Unity for', len(cases), 'cases ...')
        subprocess.run(cmd)
        if not results.exists():
            print('FAIL  Unity produced no results (see', out / 'unity.log', ')')
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
