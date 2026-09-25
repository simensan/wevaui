#!/usr/bin/env python3
"""Measure the standalone Frontier Camp UI in a native release export."""
import argparse
import configparser
from contextlib import contextmanager
import hashlib
import json
import os
import re
from pathlib import Path
import statistics
import subprocess
import sys
from frontier_perf_budget import evaluate as evaluate_budget


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()



@contextmanager
def release_template_override(project, preset, template):
    """Apply an explicit export template temporarily; never alter saved presets."""
    if template is None:
        yield {'selection': 'project_or_godot_default', 'sha256': None}
        return
    template = Path(template).resolve()
    if not template.is_file():
        raise ValueError('Missing release template: ' + str(template))
    preset_file = Path(project) / 'export_presets.cfg'
    original = preset_file.read_bytes()
    config = configparser.ConfigParser(interpolation=None)
    config.optionxform = str
    config.read_string(original.decode('utf-8'))
    sections = [name for name in config.sections() if re.fullmatch(r'preset\.\d+', name)
                and config[name].get('name') == json.dumps(preset)]
    if len(sections) != 1:
        raise ValueError('Expected one export preset named ' + preset)
    config[sections[0] + '.options']['custom_template/release'] = json.dumps(template.as_posix(), ensure_ascii=False)
    try:
        with preset_file.open('w', encoding='utf-8') as stream:
            config.write(stream)
        yield {'selection': 'explicit', 'path': str(template), 'sha256': sha(template)}
    finally:
        preset_file.write_bytes(original)


def record_failed_run(report, output, renderer, index, error):
    """Keep the failed attempt separate from runs accepted for timing analysis."""
    label = renderer + '-' + str(index)
    failure = {'renderer': renderer, 'index': index, 'error': str(error),
               'log': label + '.log'}
    exit_status = output / (label + '.exit.json')
    if exit_status.is_file():
        try:
            failure['exit_status'] = json.loads(exit_status.read_text(encoding='utf-8'))
        except (OSError, ValueError) as parse_error:
            failure['exit_status_read_error'] = str(parse_error)
    results = output / label / 'results.json'
    if results.is_file():
        try:
            failure['result'] = json.loads(results.read_text(encoding='utf-8'))
        except (OSError, ValueError) as parse_error:
            failure['result_read_error'] = str(parse_error)
    report['passed'] = False
    report['functional_passed'] = False
    report['timing_passed'] = None
    report.setdefault('failed_runs', []).append(failure)
    (output / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='New output directory')
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--run-offset', type=int, default=0, help='Continue alternating order across paired invocations')
    parser.add_argument('--frames', type=int, default=600)
    parser.add_argument('--warmups', type=int, default=120)
    parser.add_argument('--run-timeout-seconds', type=int, default=180,
                        help='Wall-clock limit per benchmark process; increase for long sample runs (default: 180)')
    parser.add_argument('--renderers', nargs='+', default=['gl_compatibility', 'mobile'])
    parser.add_argument('--automatic', action='store_true', help='Keep the native update/deferred binding schedule')
    parser.add_argument('--resolution', default='1280x720', help='Actual window and UI viewport size')
    parser.add_argument('--window-position', default='-10000,-10000',
                        help='Requested window X,Y; use --window-position=0,0 for onscreen presentation diagnostics')
    parser.add_argument('--world', choices=['static', '3d'], default='static', help='Shared deterministic 3D load or original backdrop')
    parser.add_argument('--cases', help='Comma-separated workloads; defaults to every workload')
    parser.add_argument('--capture', action='store_true', help='Save validation PNGs outside the timing window')
    parser.add_argument('--frame-phases', action='store_true', help='Diagnostic rendering callback timestamps; adds instrumentation overhead')
    parser.add_argument('--background-input-isolation', action='store_true',
                        help='Request no-focus and mouse-passthrough flags; inspect actual focus coverage in results')
    parser.add_argument('--executable', type=Path, help='Existing matching performance export; skips export')
    parser.add_argument('--release-template', type=Path, help='Explicit matching Godot template when exporting; incompatible with --executable')
    parser.add_argument('--project', type=Path, help='Isolated Frontier Camp project for comparing frozen runtimes')
    parser.add_argument('--budget', type=Path, help='Explicit timing profile; every run must meet its CPU p95 limits')
    args = parser.parse_args()
    if not re.fullmatch(r'-?[0-9]+,-?[0-9]+', args.window_position):
        parser.error('--window-position must be X,Y integer coordinates')
    if args.run_timeout_seconds <= 0:
        parser.error('--run-timeout-seconds must be positive')
    if args.executable and args.release_template:
        parser.error('--release-template cannot be combined with --executable')
    if args.release_template and not args.release_template.is_file():
        parser.error('Missing release template: ' + str(args.release_template))
    budget_bytes = args.budget.read_bytes() if args.budget else None
    budget = json.loads(budget_bytes) if budget_bytes is not None else None
    if budget is not None:
        # Validate the profile before launching an export or creating output.
        evaluate_budget({}, budget)
    if not re.fullmatch(r'[1-9][0-9]*x[1-9][0-9]*', args.resolution):
        parser.error('resolution must be WIDTHxHEIGHT')
    width, height = map(int, args.resolution.split('x'))
    if budget is not None:
        requested_scope = {'resolution': [width, height], 'world': args.world,
                           'automatic': args.automatic}
        mismatches = [key for key, value in requested_scope.items()
                      if key in budget['scope'] and budget['scope'][key] != value]
        if mismatches:
            parser.error('Requested configuration does not match timing budget: ' + ', '.join(mismatches))
    if width < 1024 or height < 720 or width > 7680 or height > 4320:
        parser.error('resolution must be within 1024x720 and 7680x4320')
    if args.run_offset < 0 or args.runs < 1 or args.frames < 120 or args.frames % 60 or args.warmups < 60 or args.warmups % 60:
        parser.error('Use runs >= 1, frames >= 120 and warmups >= 60, both multiples of 60')
    out = args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    project = (args.project or (Path(__file__).resolve().parents[2] / 'examples/frontier_camp')).resolve()
    engine = args.godot.resolve()
    env = {key: value for key, value in os.environ.items() if not key.startswith('WEVA_')}

    def run(label, command, run_env=env, timeout_seconds=180):
        command = list(map(str, command))
        command[1:1] = ['--audio-driver', 'Dummy', '--log-file', str(out / (label + '.engine.log'))]
        (out / (label + '.command.json')).write_text(json.dumps(command, indent=2), encoding='utf-8')
        try:
            result = subprocess.run(command, env=run_env, cwd=out, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    encoding='utf-8', errors='replace', timeout=timeout_seconds)
        except subprocess.TimeoutExpired as error:
            partial = error.stdout or b''
            (out / (label + '.log')).write_text(partial.decode('utf-8', errors='replace') if isinstance(partial, bytes) else partial, encoding='utf-8')
            (out / (label + '.exit.json')).write_text(json.dumps({'timed_out': True, 'timeout_seconds': timeout_seconds}), encoding='utf-8')
            raise
        (out / (label + '.log')).write_text(result.stdout, encoding='utf-8')
        (out / (label + '.exit.json')).write_text(json.dumps({'timed_out': False, 'returncode': result.returncode}), encoding='utf-8')
        if result.returncode or 'ERROR:' in result.stdout or 'FAIL ' in result.stdout:
            raise RuntimeError(label + ' failed; see ' + str(out / (label + '.log')))
        print(label + ': process completed; timing budget evaluated after all runs', flush=True)
        return result.stdout

    template_identity = {'selection': 'existing_executable', 'sha256': None}
    if args.executable:
        executable = args.executable.resolve()
    else:
        exported = out / 'export'
        exported.mkdir()
        preset = 'Windows Desktop' if sys.platform == 'win32' else 'Linux'
        executable = exported / ('FrontierCamp.exe' if sys.platform == 'win32' else 'FrontierCamp.x86_64')
        run('import', [engine, '--headless', '--path', project, '--import'])
        with release_template_override(project, preset, args.release_template) as template_identity:
            run('export', [engine, '--headless', '--path', project, '--export-release', preset, executable])
    library_name = 'weva_godot.dll' if sys.platform == 'win32' else 'libweva_godot.so'
    shipped = list(executable.parent.rglob(library_name))
    if len(shipped) != 1 or sha(shipped[0]) != sha(project / 'addons/weva/bin' / library_name):
        raise RuntimeError('Export library differs from the installed sample')

    report = {'passed': False, 'functional_passed': False, 'timing_passed': None,
              'engine_sha256': sha(engine), 'executable_sha256': sha(executable),
              'release_template': template_identity,
              'library_sha256': sha(shipped[0]), 'frames': args.frames, 'warmups': args.warmups,
              'run_timeout_seconds': args.run_timeout_seconds,
              'background_input_isolation': args.background_input_isolation,
              'frame_phase_diagnostics': args.frame_phases,
              'requested_window_position': list(map(int, args.window_position.split(','))),
              'automatic': args.automatic, 'resolution': [width, height], 'world': args.world,
              'source_sha256': {path: sha(project / path) for path in ['boot.gd', 'boot.tscn', 'project.godot', 'main.tscn', 'game.gd', 'camp_state.gd', 'tests/performance.gd', 'tests/load_world.gd', 'ui/camp.html', 'ui/camp.css', 'addons/weva/weva_view.gd'] if (project / path).is_file()},
              'runs': []}
    if budget is not None:
        report['requested_timing_profile'] = {
            'sha256': hashlib.sha256(budget_bytes).hexdigest(), 'definition': budget}
    for index in range(args.run_offset, args.run_offset + args.runs):
        renderers = args.renderers if index % 2 == 0 else list(reversed(args.renderers))
        for renderer in renderers:
            label = renderer + '-' + str(index + 1)
            destination = out / label
            destination.mkdir()
            run_env = dict(env, WEVA_FRONTIER_PERF_OUT=str(destination), WEVA_FRONTIER_FRAMES=str(args.frames),
                           WEVA_FRONTIER_WARMUPS=str(args.warmups), WEVA_FRONTIER_AUTO='1' if args.automatic else '0',
                           WEVA_FRONTIER_REVERSE=str(index % 2), WEVA_FRONTIER_CAPTURE='1' if args.capture else '0')
            run_env.update(WEVA_FRONTIER_RESOLUTION=args.resolution, WEVA_FRONTIER_WORLD=args.world)
            run_env['WEVA_FRONTIER_BACKGROUND'] = '1' if args.background_input_isolation else '0'
            run_env['WEVA_FRONTIER_FRAME_PHASES'] = '1' if args.frame_phases else '0'
            if args.cases: run_env['WEVA_FRONTIER_CASES'] = args.cases
            try:
                stdout = run(label, [executable, '--rendering-method', renderer, '--fixed-fps', '60',
                                    '--position', args.window_position, '--', '--perf'], run_env,
                             timeout_seconds=args.run_timeout_seconds)
                result = json.loads((destination / 'results.json').read_text(encoding='utf-8'))
                if 'FRONTIER_PERF_COMPLETE true' not in stdout or not result['passed'] or result['debug_build']:
                    raise RuntimeError(label + ' is not a passing release benchmark')
                if result['viewport'] != [width, height] or result.get('world', 'static') != args.world:
                    raise RuntimeError(label + ' did not measure the requested resolution/world')
            except (RuntimeError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
                record_failed_run(report, out, renderer, index + 1, error)
                raise
            report['runs'].append({'renderer': renderer, 'index': index + 1, 'result': result})
            (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    metrics = ['api_cpu', 'changed_api_cpu', 'whole_frame', 'changed_whole_frame', 'binding_cpu', 'core_cpu', 'changed_core_cpu', 'core_update_count', 'viewport_render_cpu', 'viewport_render_gpu']
    report['medians'] = {}
    for renderer in args.renderers:
        runs = [{item['workload']: item for item in run_['result']['results']} for run_ in report['runs'] if run_['renderer'] == renderer]
        report['medians'][renderer] = {case: {metric: {
            key: statistics.median(run_[case][metric][key] for run_ in runs) for key in runs[0][case][metric]}
            for metric in metrics if all(metric in run_[case] for run_ in runs)} for case in runs[0]}
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
                (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
                raise RuntimeError('Direct and bound UI screenshots differ')
    report['functional_passed'] = True
    if budget is not None:
        report['timing_budget'] = evaluate_budget(report, budget)
        report['timing_budget']['profile_sha256'] = hashlib.sha256(budget_bytes).hexdigest()
        report['timing_budget']['definition'] = budget
        report['timing_passed'] = report['timing_budget']['passed']
    report['passed'] = report['functional_passed'] and report['timing_passed'] is not False
    (out / 'summary.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print('Functional checks: PASS; timing budget: ' + ('NOT CHECKED' if budget is None else 'PASS' if report['timing_passed'] else 'FAIL'), flush=True)
    print(out / 'summary.json', flush=True)
    if report['timing_passed'] is False:
        for error in report['timing_budget']['errors']:
            print(error, flush=True)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
