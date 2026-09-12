#!/usr/bin/env python3
"""Measure ordinary game UI work in isolated Godot projects, optionally A/B."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import statistics
import subprocess


WORKLOADS = ['empty', 'hud_idle', 'hud_active', 'hud_redundant', 'hud_bound',
             'menu_hover', 'menu_animation', 'inventory_scroll', 'chat_typing']


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(command, env, log):
    try:
        result = subprocess.run(command, env=env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, encoding='utf-8',
                                errors='replace', timeout=180)
    except subprocess.TimeoutExpired as error:
        output = error.stdout or b''
        log.write_text(output.decode('utf-8', errors='replace') if isinstance(output, bytes) else output,
                       encoding='utf-8')
        raise RuntimeError(f'Engine timed out; see {log}') from error
    log.write_text(result.stdout, encoding='utf-8')
    if result.returncode or 'ERROR:' in result.stdout or 'FAIL ' in result.stdout:
        raise RuntimeError(f'Engine failed ({result.returncode}); see {log}')
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True, type=Path)
    parser.add_argument('--library', required=True, type=Path)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--out', required=True, type=Path)
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--frames', type=int, default=600)
    parser.add_argument('--warmups', type=int, default=120)
    parser.add_argument('--headless', action='store_true', help='No native rendering; separate CPU diagnostic')
    parser.add_argument('--draw-profile', action='store_true', help='Log draw CPU phases; instrumented timings')
    args = parser.parse_args()
    if args.runs < 1 or args.frames < 120 or args.warmups < 0:
        parser.error('Use runs >= 1, frames >= 120 and warmups >= 0')
    for path in [args.godot, args.library, args.baseline]:
        if path is not None and not path.is_file():
            parser.error(f'Missing file: {path}')
    if args.out.exists():
        parser.error('Output directory must be new; previous evidence is never overwritten')
    work = args.out.resolve()
    work.mkdir(parents=True)
    script = Path(__file__).with_name('project') / 'game_ui_bench.gd'
    env = {k: v for k, v in os.environ.items() if not k.startswith('WEVA_')}
    env.update(WEVA_GAME_BENCH_FRAMES=str(args.frames), WEVA_GAME_BENCH_WARMUPS=str(args.warmups),
               GODOT_SILENCE_ROOT_WARNING='1')
    if args.draw_profile:
        env['WEVA_GODOT_DRAW_LOG'] = '1'
    libraries = {'candidate': args.library.resolve()}
    if args.baseline:
        libraries = {'baseline': args.baseline.resolve(), **libraries}
    engine = str(args.godot.resolve())
    report = {'engine_sha256': digest(args.godot), 'script_sha256': digest(script),
              'headless': args.headless, 'draw_profile': args.draw_profile,
              'frames': args.frames, 'warmups': args.warmups,
              'libraries': {name: {'path': str(path), 'sha256': digest(path)} for name, path in libraries.items()},
              'runs': [], 'passed': False}
    receipt = work / 'summary.json'
    receipt.write_text(json.dumps(report, indent=2) + '\n')
    projects = {}
    for name, library in libraries.items():
        project = work / (name + '-project')
        addon = project / 'addons/weva'
        addon.mkdir(parents=True)
        shutil.copy2(library, addon / library.name)
        shutil.copy2(script, project / script.name)
        platform = 'windows' if library.suffix.lower() == '.dll' else 'linux'
        if library.suffix.lower() not in ('.dll', '.so'):
            raise RuntimeError('Use a Windows .dll or Linux .so extension')
        (addon / 'weva.gdextension').write_text(
            '[configuration]\nentry_symbol="weva_library_init"\ncompatibility_minimum="4.7"\n'
            '[libraries]\n' + platform + '.x86_64="res://addons/weva/' + library.name + '"\n')
        (project / 'project.godot').write_text(
            'config_version=5\n[application]\nconfig/name="Weva runtime benchmark"\n'
            '[display]\nwindow/size/viewport_width=1280\nwindow/size/viewport_height=720\n'
            '[rendering]\nrenderer/rendering_method="mobile"\n')
        run([engine, '--headless', '--path', str(project), '--editor', '--import', '--quit-after', '60'],
            env, work / (name + '-import.log'))
        projects[name] = project
    for pair in range(args.runs):
        order = list(libraries)
        if pair % 2:
            order.reverse()
        for name in order:
            output = work / f'{name}-{pair}'
            output.mkdir()
            run_env = dict(env, WEVA_GAME_BENCH_OUT=str(output))
            command = [engine, '--path', str(projects[name]), '--script', 'res://game_ui_bench.gd',
                       '--fixed-fps', '60', '--resolution', '1280x720']
            if args.headless:
                command.append('--headless')
            stdout = run(command, run_env, output / 'engine.log')
            result = json.loads((output / 'results.json').read_text())
            if ('GAME_UI_COMPLETE true' not in stdout or not result.get('passed') or
                    [r['workload'] for r in result['results']] != WORKLOADS):
                raise RuntimeError(f'Incomplete benchmark: {output}')
            report['runs'].append({'variant': name, 'pair': pair, 'result': result})
            receipt.write_text(json.dumps(report, indent=2) + '\n')
            print(f'{name} {pair + 1}/{args.runs}: PASS', flush=True)
    report['medians'] = {}
    for name in libraries:
        values = [r['result']['results'] for r in report['runs'] if r['variant'] == name]
        report['medians'][name] = {workload: {
            metric: {key: statistics.median(run_[index][metric][key] for run_ in values)
                     for key in values[0][index][metric]}
            for metric in ['api_cpu', 'last_core_update', 'whole_frame', 'sixth_frame_api_cpu', 'other_frame_api_cpu']}
            for index, workload in enumerate(WORKLOADS)}
    if args.baseline and not args.headless:
        report['pixels_identical'] = all(
            digest(work / f'baseline-{pair}' / (case + '.png')) ==
            digest(work / f'candidate-{pair}' / (case + '.png'))
            for pair in range(args.runs) for case in WORKLOADS if case != 'empty')
    report['passed'] = report.get('pixels_identical', True)
    receipt.write_text(json.dumps(report, indent=2) + '\n')
    print(receipt, flush=True)
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
