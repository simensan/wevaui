#!/usr/bin/env python3
"""Capture current sample pages in Unity and Chrome for visual review.

One Unity EditMode run renders every case with the package's Inter font backend.
One Chrome run captures matching viewports with the bundled fonts available.
Both freeze CSS animation and transitions. The output contains fresh image pairs
and a report linking them; it does not claim pixel equality or CSS conformance.
Use Tools/oracle/chrome_sweep.py for the automated Chrome layout gate.

    python hosts/unity/goldens_from_unity.py [corpus] [--only 9slice-demo]
        [--out .utmp/goldens-unity] [--unity <Unity.exe>] [--node <node>]

The old C# engine's deleted snippets and baseline images are no longer used.
WEVA_CHROME selects the browser. --skip-unity reuses an existing Unity run when
its HTML, CSS and viewport inputs are unchanged; Chrome is still captured fresh.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

from oracle_support import (DEFAULT_UNITY, REPO, frozen_stylesheet, hidden_process_options,
                            preserve_previous, run_unity_manifest)


def collect_cases(corpus, only=None):
    return sorted(p for p in corpus.glob('*.html') if only is None or only in p.stem)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('corpus', nargs='?', default=str(REPO / 'Tools/oracle/corpus/samples'))
    ap.add_argument('--unity', default=DEFAULT_UNITY)
    ap.add_argument('--project', default=str(REPO))
    ap.add_argument('--node', default='node')
    ap.add_argument('--out', default='.utmp/goldens-unity')
    ap.add_argument('--only', help='capture names containing this text')
    ap.add_argument('--skip-unity', action='store_true')
    args = ap.parse_args(argv)

    corpus = Path(args.corpus).resolve()
    cases = collect_cases(corpus, args.only)
    if not cases:
        print('FAIL no HTML cases in', corpus)
        return 2
    out = Path(args.out).resolve()
    for directory in ('inputs', 'dump', 'unity', 'chrome'):
        (out / directory).mkdir(parents=True, exist_ok=True)
    lines, browser_rows, inputs = [], [], []
    for html in cases:
        css = html.with_suffix('.css')
        text = frozen_stylesheet(css if css.exists() else None)
        capture = Path(str(html) + '.chrome-layout.json')
        size = json.loads(capture.read_text(encoding='utf-8')) if capture.exists() else {}
        width, height = int(size.get('width', 1280)), int(size.get('height', 720))
        if width <= 0 or height <= 0:
            print('FAIL invalid viewport in', capture)
            return 2
        staged_css = out / 'inputs' / (html.stem + '.css')
        if not args.skip_unity:
            preserve_previous(staged_css)
            staged_css.write_text(text, encoding='utf-8')
        inputs.append({'html': str(html), 'html_sha256': hashlib.sha256(html.read_bytes()).hexdigest(),
                       'css_sha256': hashlib.sha256(text.encode('utf-8')).hexdigest(),
                       'width': width, 'height': height, 'project': str(Path(args.project).resolve())})
        lines.append('\t'.join([str(html), str(staged_css), str(width), str(height),
                                str(out / 'dump' / (html.stem + '.json')), 'inter',
                                str(out / 'unity' / (html.stem + '.png'))]))
        browser_rows.append({'html': str(html), 'width': width, 'height': height,
                             'png': str(out / 'chrome' / (html.stem + '.png'))})

    manifest = out / 'manifest.tsv'
    input_path = out / 'unity-inputs.json'
    manifest_text = '\n'.join(lines) + '\n'
    if args.skip_unity:
        if (not input_path.exists() or json.loads(input_path.read_text(encoding='utf-8')) != inputs or
                not manifest.exists() or manifest.read_text(encoding='utf-8') != manifest_text):
            print('FAIL --skip-unity requires the same saved HTML, CSS, viewport and project')
            return 1
        print('reusing saved Unity images; font, asset and plugin inputs must also be unchanged')
    else:
        preserve_previous(manifest)
        preserve_previous(input_path)
        manifest.write_text(manifest_text, encoding='utf-8')
        input_path.write_text(json.dumps(inputs, indent=2) + '\n', encoding='utf-8')
        print('rendering', len(cases), 'cases in Unity ...', flush=True)
        try:
            run_unity_manifest(args.unity, Path(args.project).resolve(), manifest,
                               out / 'unity-results.xml', out / 'unity.log')
        except (OSError, RuntimeError) as error:
            print('FAIL', error)
            return 1

    browser_manifest = out / 'chrome-manifest.json'
    receipt = out / 'chrome-receipt.json'
    for path in (browser_manifest, receipt, out / 'chrome.log', out / 'report.json'):
        preserve_previous(path)
    for row in browser_rows:
        preserve_previous(row['png'])
    browser_manifest.write_text(json.dumps(browser_rows, indent=2) + '\n', encoding='utf-8')
    result = subprocess.run([args.node, str(REPO / 'Tools/oracle/capture_render_manifest.cjs'),
                             str(browser_manifest), str(receipt)], capture_output=True, text=True,
                            **hidden_process_options())
    (out / 'chrome.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode != 0 or not receipt.exists():
        print('FAIL Chrome capture; see', out / 'chrome.log')
        return 1
    pairs = [{'case': html.stem, 'unity': str(out / 'unity' / (html.stem + '.png')),
              'chrome': str(out / 'chrome' / (html.stem + '.png'))} for html in cases]
    if any(not Path(pair[key]).is_file() for pair in pairs for key in ('unity', 'chrome')):
        print('FAIL a render is missing')
        return 1
    (out / 'report.json').write_text(json.dumps({
        'comparison': 'visual review required', 'browser': json.loads(receipt.read_text())['browser'],
        'pairs': pairs,
    }, indent=2) + '\n', encoding='utf-8')
    print(f'Captured {len(pairs)} Unity/Chrome image pairs for visual review: {out / "report.json"}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
