#!/usr/bin/env python3
"""Measure the standalone Frontier Camp UI in a native release export."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='New output directory')
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--run-offset', type=int, default=0, help='Continue alternating order across paired invocations')
    parser.add_argument('--frames', type=int, default=600)
    parser.add_argument('--warmups', type=int, default=120)
    parser.add_argument('--renderers', nargs='+', default=['gl_compatibility', 'mobile'])
    parser.add_argument('--automatic', action='store_true', help='Keep the native update/deferred binding schedule')
    parser.add_argument('--cases', help='Comma-separated workloads; defaults to every workload')
    parser.add_argument('--capture', action='store_true', help='Save validation PNGs outside the timing window')
    parser.add_argument('--executable', type=Path, help='Existing matching performance export; skips export')
    parser.add_argument('--project', type=Path, help='Isolated Frontier Camp project for comparing frozen runtimes')
    args = parser.parse_args()
    if args.run_offset < 0 or args.runs < 1 or args.frames < 120 or args.frames % 60 or args.warmups < 60 or args.warmups % 60:
        parser.error('Use runs >= 1, frames >= 120 and warmups >= 60, both multiples of 60')
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    project = (args.project or (Path(__file__).resolve().parents[2] / 'examples/frontier_camp')).resolve()
    engine = args.godot.resolve()
    env = {key: value for key, value in os.environ.items() if not key.startswith('WEVA_')}

    def run(label, command, run_env=env):
        command = list(map(str, command))
        command[1:1] = ['--audio-driver', 'Dummy', '--log-file', str(out / (label + '.engine.log'))]
        (out / (label + '.command.json')).write_text(json.dumps(command, indent=2))
        try:
            result = subprocess.run(command, env=run_env, cwd=out, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    encoding='utf-8', errors='replace', timeout=180)
        except subprocess.TimeoutExpired as error:
            partial = error.stdout or b''
            (out / (label + '.log')).write_text(partial.decode('utf-8', errors='replace') if isinstance(partial, bytes) else partial)
            raise
        (out / (label + '.log')).write_text(result.stdout, encoding='utf-8')
        if result.returncode or 'ERROR:' in result.stdout or 'FAIL ' in result.stdout:
            raise RuntimeError(label + ' failed; see ' + str(out / (label + '.log')))
        print(label + ': passed', flush=True)
        return result.stdout

    if args.executable:
        executable = args.executable.resolve()
    else:
        exported = out / 'export'
        exported.mkdir()
        preset = 'Windows Desktop' if sys.platform == 'win32' else 'Linux'
        executable = exported / ('FrontierCamp.exe' if sys.platform == 'win32' else 'FrontierCamp.x86_64')
        run('import', [engine, '--headless', '--path', project, '--editor', '--quit-after', '60'])
        run('export', [engine, '--headless', '--path', project, '--export-release', preset, executable])
    library_name = 'weva_godot.dll' if sys.platform == 'win32' else 'libweva_godot.so'
    shipped = list(executable.parent.rglob(library_name))
    if len(shipped) != 1 or sha(shipped[0]) != sha(project / 'addons/weva/bin' / library_name):
        raise RuntimeError('Export library differs from the installed sample')

    report = {'passed': False, 'engine_sha256': sha(engine), 'executable_sha256': sha(executable),
              'library_sha256': sha(shipped[0]), 'frames': args.frames, 'warmups': args.warmups,
              'automatic': args.automatic,
              'source_sha256': {path: sha(project / path) for path in ['game.gd', 'camp_state.gd', 'tests/performance.gd', 'ui/camp.html', 'ui/camp.css', 'addons/weva/weva_view.gd']},
              'runs': []}
    for index in range(args.run_offset, args.run_offset + args.runs):
        renderers = args.renderers if index % 2 == 0 else list(reversed(args.renderers))
        for renderer in renderers:
            label = renderer + '-' + str(index + 1)
            destination = out / label
            destination.mkdir()
            run_env = dict(env, WEVA_FRONTIER_PERF_OUT=str(destination), WEVA_FRONTIER_FRAMES=str(args.frames),
                           WEVA_FRONTIER_WARMUPS=str(args.warmups), WEVA_FRONTIER_AUTO='1' if args.automatic else '0',
                           WEVA_FRONTIER_REVERSE=str(index % 2), WEVA_FRONTIER_CAPTURE='1' if args.capture else '0')
            if args.cases: run_env['WEVA_FRONTIER_CASES'] = args.cases
            stdout = run(label, [executable, '--rendering-method', renderer, '--fixed-fps', '60',
                                '--position', '-10000,-10000', '--', '--perf'], run_env)
            result = json.loads((destination / 'results.json').read_text())
            if 'FRONTIER_PERF_COMPLETE true' not in stdout or not result['passed'] or result['debug_build']:
                raise RuntimeError(label + ' is not a passing release benchmark')
            report['runs'].append({'renderer': renderer, 'index': index + 1, 'result': result})
            (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    metrics = ['api_cpu', 'changed_api_cpu', 'whole_frame', 'changed_whole_frame', 'binding_cpu', 'core_cpu', 'viewport_render_cpu', 'viewport_render_gpu']
    report['medians'] = {}
    for renderer in args.renderers:
        runs = [{item['workload']: item for item in run_['result']['results']} for run_ in report['runs'] if run_['renderer'] == renderer]
        report['medians'][renderer] = {case: {metric: {
            key: statistics.median(run_[case][metric][key] for run_ in runs) for key in runs[0][case][metric]}
            for metric in metrics} for case in runs[0]}
    if args.capture:
        report['pixel_checks'] = []
        for run_ in report['runs']:
            destination = out / (run_['renderer'] + '-' + str(run_['index']))
            workloads = {item['workload'] for item in run_['result']['results']}
            for case in ('clock', 'vitals'):
                if {case + '_update', case + '_direct'}.issubset(workloads):
                    identical = sha(destination / (case + '_update.png')) == sha(destination / (case + '_direct.png'))
                    report['pixel_checks'].append({'run': destination.name, 'case': case, 'identical': identical})
        if report['pixel_checks']:
            report['direct_pixels_match_binding'] = all(item['identical'] for item in report['pixel_checks'])
            if not report['direct_pixels_match_binding']:
                (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
                raise RuntimeError('Direct and bound UI screenshots differ')
    report['passed'] = True
    (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n')
    print(out / 'summary.json', flush=True)


if __name__ == '__main__':
    main()
