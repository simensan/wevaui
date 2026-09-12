#!/usr/bin/env python3
"""The C# engine's goldens, run through the core hosted by Unity.

Phase 3 step 2 of the shared-core plan. Two comparisons over the package's
golden snippets (Packages/com.wevaui/Tests/Runtime/Goldens/Snippets):

  layout  the core's layout dump against Chrome's getBoundingClientRect
          capture (<name>.html.chrome-layout.json), paired by index and judged
          with LayoutDiffTests' rule: |dx|,|dy| <= absPx and |dw|,|dh| <=
          max(absPx, rel * |chrome|), absPx 2 / rel 5% unless a
          <name>.html.tolerance.json sidecar says otherwise;
  paint   the core's render (Hidden/Weva/NativeMesh, Inter through FontEngine)
          against the C# engine's baseline PNG (Baselines/<name>.png), with
          compare_render.py's metric (structural pixels gate).

One Unity editor run dumps and renders every snippet (the manifest-driven
EditMode test; a graphics device is needed, so no -nographics).

    python goldens_from_unity.py [--out .utmp/goldens-unity] [--skip-unity]
"""
import argparse
import json
import re
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent
sys.path.insert(0, str(HERE.parent / 'godot'))
from compare_render import compare as compare_ppm  # noqa: E402

DEFAULT_UNITY = r'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe'
GOLDENS = REPO / 'Packages' / 'com.wevaui' / 'Tests' / 'Runtime' / 'Goldens'


def png_to_ppm(png: Path, ppm: Path):
    from PIL import Image
    im = Image.open(png).convert('RGB')
    w, h = im.size
    with open(ppm, 'wb') as f:
        f.write(f'P6\n{w} {h}\n255\n'.encode('ascii'))
        f.write(im.tobytes())


def layout_diff(chrome_path: Path, dump_path: Path, abs_px: float, rel: float):
    with open(chrome_path, encoding='utf-8') as f:
        chrome = json.load(f)['elements']
    with open(dump_path, encoding='utf-8') as f:
        core = json.load(f)['elements']
    n = min(len(chrome), len(core))
    out_of_tol, worst, lines = 0, 0.0, []
    for i in range(n):
        c, u = chrome[i], core[i]
        dx, dy = u['x'] - c['x'], u['y'] - c['y']
        dw, dh = u['w'] - c['w'], u['h'] - c['h']
        allow_w = max(abs_px, abs(c['w']) * rel)
        allow_h = max(abs_px, abs(c['h']) * rel)
        m = max(abs(dx), abs(dy), abs(dw), abs(dh))
        worst = max(worst, m)
        if abs(dx) <= abs_px and abs(dy) <= abs_px and abs(dw) <= allow_w and abs(dh) <= allow_h:
            continue
        out_of_tol += 1
        if len(lines) < 6:
            sig = f"<{c['tag']}{'#' + c['id'] if c['id'] else ''}{'.' + c['cls'].replace(' ', '.') if c['cls'] else ''}>"
            lines.append(f"    [{i:3}] {sig:<32} chrome=({c['x']:.2f},{c['y']:.2f},{c['w']:.2f}x{c['h']:.2f}) core=({u['x']:.2f},{u['y']:.2f},{u['w']:.2f}x{u['h']:.2f})")
    return out_of_tol, n, len(chrome) - len(core), worst, lines


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--unity', default=DEFAULT_UNITY)
    ap.add_argument('--out', default='.utmp/goldens-unity')
    ap.add_argument('--skip-unity', action='store_true')
    ap.add_argument('--paint-tolerance', type=int, default=8)
    ap.add_argument('--paint-structural', type=int, default=64)
    ap.add_argument('--paint-coverage', type=float, default=1.0, help='structural pixels allowed, percent')
    args = ap.parse_args()

    out = Path(args.out).resolve()
    (out / 'dump').mkdir(parents=True, exist_ok=True)
    (out / 'render').mkdir(parents=True, exist_ok=True)
    snippets = sorted(p for p in (GOLDENS / 'Snippets').glob('*.html'))
    if not snippets:
        print('no snippets under', GOLDENS)
        return 2
    # The image goldens name their viewport per snippet (GoldenSuiteTests.cs);
    # the Chrome captures were taken at the same sizes.
    suite = (GOLDENS / 'GoldenSuiteTests.cs').read_text(encoding='utf-8')
    sizes = {m.group(1): (int(m.group(2)), int(m.group(3)))
             for m in re.finditer(r'SnippetPath\("([^"]+)"\)[^;]*?width:\s*(\d+),\s*height:\s*(\d+)', suite, re.S)}
    manifest = out / 'manifest.tsv'
    lines = []
    for html in snippets:
        css = html.with_suffix('.css')
        width, height = sizes.get(html.name, (800, 600))
        lines.append('\t'.join([str(html), str(css) if css.exists() else '', str(width), str(height),
                                str(out / 'dump' / (html.stem + '.json')), 'inter', str(out / 'render' / (html.stem + '.png'))]))
    manifest.write_text('\n'.join(lines) + '\n', encoding='utf-8')

    if not args.skip_unity:
        os.environ['WEVA_NATIVE_DUMP_MANIFEST'] = str(manifest)
        results = out / 'unity-results.xml'
        cmd = [args.unity, '-batchmode', '-projectPath', str(REPO), '-runTests', '-testPlatform', 'EditMode',
               '-testFilter', 'Weva.Tests.EditorTests.Native.NativeLayoutDumpTests.Manifest_DumpsEveryCaseForTheOracle',
               '-testResults', str(results), '-logFile', str(out / 'unity.log')]
        print('running Unity for', len(snippets), 'snippets ...')
        subprocess.run(cmd, env=dict(os.environ))
        if not results.exists():
            print('FAIL  Unity produced no results (see', out / 'unity.log', ')')
            return 1

    layout_pass = layout_fail = paint_pass = paint_fail = 0
    report = []
    for html in snippets:
        name = html.stem
        entry = {'snippet': name}
        chrome = Path(str(html) + '.chrome-layout.json')
        dump = out / 'dump' / (name + '.json')
        abs_px, rel = 2.0, 0.05
        tol = Path(str(html) + '.tolerance.json')
        if tol.exists():
            with open(tol, encoding='utf-8') as f:
                t = json.load(f)
            abs_px = float(t.get('absPx', abs_px))
            rel = float(t.get('relPct', rel))
        if chrome.exists() and dump.exists():
            out_of_tol, n, missing, worst, detail = layout_diff(chrome, dump, abs_px, rel)
            ok = out_of_tol == 0 and missing == 0
            layout_pass += ok
            layout_fail += not ok
            entry['layout'] = {'ok': ok, 'out_of_tolerance': out_of_tol, 'compared': n, 'count_delta': missing, 'worst_px': round(worst, 2)}
            print(f"{name}: layout {'ok' if ok else 'FAIL'} ({out_of_tol}/{n} out of tolerance, worst {worst:.2f}px, count delta {missing})")
            for d in detail:
                print(d)
        else:
            entry['layout'] = {'ok': None, 'reason': 'no chrome capture' if not chrome.exists() else 'no dump'}
            print(f'{name}: layout skipped ({entry["layout"]["reason"]})')
        baseline = GOLDENS / 'Baselines' / (name + '.png')
        render = out / 'render' / (name + '.ppm')
        if baseline.exists() and render.exists():
            base_ppm = out / 'render' / (name + '.baseline.ppm')
            png_to_ppm(baseline, base_ppm)
            print(f'{name}: paint vs C# baseline')
            ok = compare_ppm(str(render), str(base_ppm), args.paint_tolerance, args.paint_coverage, args.paint_structural)
            paint_pass += ok
            paint_fail += not ok
            entry['paint'] = {'ok': ok}
        else:
            entry['paint'] = {'ok': None, 'reason': 'no baseline' if not baseline.exists() else 'no render'}
        report.append(entry)
    (out / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'\nlayout vs Chrome: {layout_pass} pass, {layout_fail} fail; paint vs C# baselines: {paint_pass} pass, {paint_fail} fail; report in {out / "report.json"}')
    return 0 if layout_fail == 0 and paint_fail == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
