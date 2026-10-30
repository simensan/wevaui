"""Matched attribute-mutation-to-layout benchmarks, excluding paint in both engines."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]


def positive(value):
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError('must be positive')
    return number


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def parse_native(output, geometry_only=False):
    if geometry_only and (not output.startswith('mutation-to-layout ') or '  0 draws  0 textures' not in output):
        raise ValueError('Expected geometry-only benchmark; rebuild weva_bench')
    matches = re.findall(r'best\s+([\d.]+) ms\s+mean\s+([\d.]+) ms', output)
    if len(matches) != 1:
        raise ValueError('Expected exactly one native timing row')
    best, mean = map(float, matches[0])
    if not all(math.isfinite(x) and x >= 0 for x in (best, mean)) or best > mean:
        raise ValueError('Invalid native timings')
    return {'best_ms': best, 'mean_ms': mean}


def fixtures(directory):
    """Geometry-only game UI shapes avoid unmatched host-owned font metrics."""
    common = ('html,body{margin:0;padding:0;width:1280px;min-height:720px}'
              '*{box-sizing:border-box}body{background:#171d22}'
              '#target{padding-left:10px}.cell{background:#495c65;border:1px solid #81959e}')
    shapes = {
        'hud': ('#target{display:flex;width:620px;gap:8px;padding:12px}'
                '.cell{width:90px;height:28px}', 6),
        'inventory': ('#target{display:grid;grid-template-columns:repeat(10,64px);gap:6px;'
                      'width:740px;padding:12px}.cell{height:64px}', 100),
        'settings': ('#target{display:flex;flex-direction:column;gap:8px;width:600px;'
                     'padding:12px}.cell{height:30px;width:100%}', 20),
    }
    jobs = []
    for name, (css, count) in shapes.items():
        html_path, css_path = directory / f'{name}.html', directory / f'{name}.css'
        html_path.write_text('<!doctype html><html><head></head><body><div id="target">' +
                             '<div class="cell"></div>' * count + '</div></body></html>', encoding='utf-8')
        css_path.write_text(common + css, encoding='utf-8')
        for mode, target in (('panel', '#target'), ('leaf', '.cell:last-child')):
            jobs.append({'name': name, 'mode': mode, 'target': target, 'html': str(html_path), 'css': str(css_path),
                         'html_sha256': digest(html_path), 'css_sha256': digest(css_path)})
    return jobs


def run(command, log):
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=600)
    log.write_text(result.stdout + '\n--- stderr ---\n' + result.stderr, encoding='utf-8')
    result.check_returncode()
    return result.stdout


def check_geometry(native, chrome):
    if len(native) != len(chrome) or not native:
        raise ValueError('Layout element counts differ')
    worst = max(abs(a[key] - b[key]) for a, b in zip(native, chrome) for key in ('x', 'y', 'w', 'h'))
    if not math.isfinite(worst) or worst > 0.1:
        raise ValueError(f'Layout differs by {worst} px; timing comparison rejected')
    return worst


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--weva-bench', required=True, type=Path)
    parser.add_argument('--weva-dump', required=True, type=Path, help='Same build as weva-bench; checks fixture geometry')
    parser.add_argument('--out', required=True, type=Path, help='New output directory; never overwrites evidence')
    parser.add_argument('--passes', type=positive, default=200)
    parser.add_argument('--runs', type=positive, default=5)
    parser.add_argument('--node', default='node')
    args = parser.parse_args()
    binary, out = args.weva_bench.resolve(), args.out.resolve()
    dump = args.weva_dump.resolve()
    if not binary.is_file() or not dump.is_file():
        parser.error('weva-bench and weva-dump must be existing executables')
    out.mkdir(parents=True, exist_ok=False)
    jobs = fixtures(out)
    geometry = {}
    for job in jobs:
        if job['name'] in geometry:
            continue
        path = out / (job['name'] + '-layout.json')
        run([str(dump), job['html'], '1280', '720', str(path), job['css']],
            out / (job['name'] + '-layout.log'))
        geometry[job['name']] = json.loads(path.read_text(encoding='utf-8'))['elements']
    manifest = out / 'manifest.json'
    manifest.write_text(json.dumps({'passes': args.passes, 'jobs': jobs}, indent=2), encoding='utf-8')
    receipt = {'schema': 2, 'boundary': 'attribute setter + synchronous style/layout + target bounds; no paint',
               'utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
               'platform': platform.platform(), 'cpu': platform.processor(), 'logical_cpus': os.cpu_count(),
               'git_revision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
               'git_status': subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True),
               'binary': str(binary), 'binary_sha256': digest(binary),
               'dump_binary': str(dump), 'dump_sha256': digest(dump),
               'raster_environment': {k: os.environ.get(k) for k in ('WEVA_RASTER_SERIAL', 'WEVA_RASTER_THREADS')},
               'passes': args.passes, 'runs': args.runs, 'jobs': jobs, 'samples': []}
    # Alternate engines between rounds. Both run on this OS, never concurrently.
    for repetition in range(args.runs):
        for engine in (('weva', 'chrome') if repetition % 2 == 0 else ('chrome', 'weva')):
            if engine == 'weva':
                for job in jobs:
                    command = [str(binary), job['html'], job['css'], str(args.passes)]
                    command += ['--geometry', '--mutate=layout', '--target=' + job['target']]
                    output = run(command, out / f"weva-{repetition}-{job['name']}-{job['mode']}.log")
                    receipt['samples'].append({'engine': engine, 'run': repetition, **job,
                                               **parse_native(output, geometry_only=True)})
            else:
                path = out / f'chrome-{repetition}.json'
                run([args.node, str(Path(__file__).with_name('chrome.cjs')), str(manifest), str(path)],
                    out / f'chrome-{repetition}.log')
                data = json.loads(path.read_text(encoding='utf-8'))
                if len(data['results']) != len(jobs):
                    raise ValueError('Missing Chrome workloads')
                receipt['chrome'] = data['browser']
                for expected, sample in zip(jobs, data['results']):
                    if any(sample[k] != expected[k] for k in ('name', 'mode', 'target', 'html_sha256', 'css_sha256')):
                        raise ValueError('Mismatched Chrome workload')
                    if not math.isfinite(sample['mean_ms']) or sample['mean_ms'] < 0:
                        raise ValueError('Invalid Chrome timing')
                    sample['layout_max_delta_px'] = check_geometry(geometry[sample['name']], sample['geometry'])
                    receipt['samples'].append({'engine': engine, 'run': repetition, **sample})
        print(f'Finished round {repetition + 1}/{args.runs}', flush=True)
    (out / 'results.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    lines = ['# Weva / Chrome matched mutation-to-layout', '',
             'Milliseconds: median of per-run means (not frame p95). Both include the attribute setter and exclude paint.', '',
             '| Workload | Weva mutation + layout | Chrome mutation + layout |',
             '|---|---:|---:|']
    for job in jobs:
        values = [statistics.median(s['mean_ms'] for s in receipt['samples']
                  if s['engine'] == engine and s['name'] == job['name'] and s['mode'] == job['mode'])
                  for engine in ('weva', 'chrome')]
        lines.append(f"| {job['name']} {job['mode']} | {values[0]:.6f} | {values[1]:.6f} |")
    lines += ['', 'Neither engine paints, rasterizes or composites in the measured region.',
              'No host rendering, frame pacing, process launch, network, text or image decoding is measured.',
              'Panel changes affect a container; leaf changes affect one cell. These are geometry-only fixtures.',
              'See Tools/performance/README.md for methodology and limits.']
    (out / 'summary.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')
    print('\n'.join(lines))


if __name__ == '__main__':
    main()
