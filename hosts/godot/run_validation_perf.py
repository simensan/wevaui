#!/usr/bin/env python3
"""Measure explicit validation calls on mixed settings forms; no timing gate."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import build_metadata

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', type=Path, required=True)
    parser.add_argument('--library', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--renderers', nargs='+', default=['gl_compatibility', 'mobile'])
    parser.add_argument('--disable-native-input', action='store_true', help='Diagnostic only: disable native pointer/focus/IME integration while retaining explicit core focus requests')
    parser.add_argument('--profile-ime', action='store_true', help='Diagnostic stage logging; perturbs overall timings')
    args = parser.parse_args()
    if args.runs < 1: parser.error('runs must be positive')
    root = Path(__file__).resolve().parents[2]
    library, engine, out = args.library.resolve(), args.godot.resolve(), args.output.resolve()
    metadata = json.loads(Path(str(library) + '.build.json').read_text(encoding='utf-8'))
    if metadata['library_sha256'] != build_metadata.sha256(library) or metadata['source']['sha256'] != build_metadata.source_hash(root):
        raise RuntimeError('Library/source metadata mismatch')
    out.mkdir(parents=True, exist_ok=False)
    project = out / 'project'
    shutil.copytree(Path(__file__).parent / 'project', project, ignore=shutil.ignore_patterns('.godot', 'bin'))
    (project / 'addons/weva/bin').mkdir(exist_ok=True)
    shutil.copy2(library, project / 'addons/weva/bin' / library.name)
    env = {k: v for k, v in os.environ.items() if not k.startswith('WEVA_')}
    if args.disable_native_input: env['WEVA_VALIDATION_DISABLE_NATIVE_INPUT'] = '1'
    if args.profile_ime: env['WEVA_GODOT_IME_PROFILE'] = '1'
    def run(label, options, environment):
        command = [str(engine), '--audio-driver', 'Dummy', '--path', str(project), *options]
        try:
            result = subprocess.run(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                encoding='utf-8', errors='replace', timeout=150)
        except subprocess.TimeoutExpired as error:
            output = error.stdout or b''
            (out / (label + '.log')).write_text(output.decode('utf-8', errors='replace') if isinstance(output, bytes) else output, encoding='utf-8')
            raise
        (out / (label + '.log')).write_text(result.stdout, encoding='utf-8')
        if result.returncode or 'ERROR:' in result.stdout or 'FAIL' in result.stdout:
            raise RuntimeError(label + ' failed; inspect log')
    run('import', ['--headless', '--import'], env)
    rows = []
    for repeat in range(args.runs):
        renderers = args.renderers if repeat % 2 == 0 else list(reversed(args.renderers))
        for renderer in renderers:
            label = str(repeat) + '-' + renderer
            destination = out / (label + '.json')
            run(label, ['--rendering-method', renderer, '--fixed-fps', '60', '--position', '-10000,-10000',
                '--quit-after', '10000', '--scene', 'res://validation_performance.tscn'], dict(env, WEVA_VALIDATION_PERF_OUT=str(destination)))
            result = json.loads(destination.read_text(encoding='utf-8'))
            if not result['passed'] or len(result['rows']) != 12: raise RuntimeError(label + ' incomplete')
            rows.append(dict(repeat=repeat, renderer=renderer, result=result))
            print(label + ': PASS', flush=True)
    report = dict(metadata=metadata, engine_sha256=build_metadata.sha256(engine),
        fixture_sha256=build_metadata.sha256(project / 'validation_performance.gd'),
        passed=True, timing_gate=False, runs=rows)
    (out / 'summary.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
if __name__ == '__main__': main()
