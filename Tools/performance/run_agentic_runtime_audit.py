"""Profile prepared survival UI in explicit SDKs and verify clicks/reloads.

Uses projects from create_agentic_audit_project.py. Owns and stops every session
it launches; never changes the user's configured engine. Requires psutil.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

import psutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--engine', action='append', required=True)
    parser.add_argument('--rounds', type=int, default=3)
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('rounds must be positive')
    engines = []
    for item in args.engine:
        label, separator, path = item.partition('=')
        if not separator or not label or not all(c.isalnum() or c in '-_' for c in label):
            parser.error('engine must be LABEL=BUNDLE_PATH')
        if any(label == old for old, _ in engines):
            parser.error('engine labels must be unique')
        engines.append((label, Path(path).resolve(strict=True)))
    project = args.project.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, AGENTIC_SESSION_HOME=str(output / 'registry'),
               AGENTIC_SAVE_DIR=str(output / 'saves'), AGENTIC_WINDOW_OFFSCREEN='1')
    rows, lifecycle = [], []
    for round_index in range(args.rounds):
        for label, engine in engines if round_index % 2 == 0 else reversed(engines):
            prefix = f'{label}-{round_index}'
            suffix = '.exe' if os.name == 'nt' else ''
            cli = engine / ('target/fast/agentic-tool' + suffix)
            runtime = engine / ('target/fast/dev_runtime' + suffix)
            session = None

            def call(name, arguments):
                command = [str(cli), *arguments]
                if session is not None:
                    command += ['--session', session]
                process = subprocess.run([*command, '--json'], env=env, capture_output=True,
                                         text=True, timeout=120)
                (output / f'{prefix}-{name}.json').write_text(process.stdout, encoding='utf-8')
                (output / f'{prefix}-{name}.stderr').write_text(process.stderr, encoding='utf-8')
                response = json.loads(process.stdout)
                if process.returncode or response['status'] not in ('completed', 'accepted'):
                    raise RuntimeError((name, response))
                for artifact in response.get('artifacts', []):
                    # Runtime logs keep growing after the launch response.
                    if artifact['kind'] == 'log':
                        continue
                    digest = hashlib.sha256(Path(artifact['path']).read_bytes()).hexdigest()
                    if digest != artifact['sha256']:
                        raise RuntimeError('Artifact hash mismatch: ' + artifact['path'])
                return response

            def controls(name, value):
                return call(name, ['session', 'input', '--request-key', prefix + '-' + name,
                                   '--ticks', '2', '--controls', json.dumps(value)])

            def state(name):
                fields = ['audit_mode', 'audit_selected', 'audit_clicks', 'audit_uses', 'audit_reloads']
                arguments = ['snapshot', '--entity', 'probe']
                for field in fields:
                    arguments += ['--global', field]
                response = call(name, arguments)
                artifact = next(a for a in response['artifacts'] if a['kind'] == 'cpu_world_snapshot')
                return json.loads(Path(artifact['path']).read_text(encoding='utf-8'))['globals']

            launch = call('launch', ['dev', '--runtime', str(runtime), '--engine', str(engine),
                                    '--project', str(project), '--session', prefix, '--timeout', '90'])
            session = launch['data']['session']['id']
            try:
                process = psutil.Process(launch['data']['session']['pid'])
                call('ready', ['capture', '--ready', 'audit_mode', '--ready-value', '4', '--timeout', '30'])
                for mode in ([0, 2, 4, 3, 1] if round_index % 2 == 0 else [1, 3, 4, 2, 0]):
                    name = f'mode-{mode}'
                    controls(name, {'keys': ['Num' + str(mode + 1)]})
                    if state(name + '-state')['audit_mode'] != mode:
                        raise RuntimeError('Wrong profile mode')
                    response = call(name + '-profile', ['profile', '--sampling', 'cpu_only',
                        '--frames', '600', '--warmup', '120', '--label', prefix + '-' + name, '--timeout', '90'])
                    summary = response['data']['summary']
                    report_path = next(a['path'] for a in response['artifacts'] if a['kind'] == 'profile_report')
                    report = json.loads(Path(report_path).read_text(encoding='utf-8'))
                    if summary['frames'] != 600 or summary['marked_frames'] != 0:
                        raise RuntimeError('Incomplete or marked profile')
                    if summary['configuration']['measured'] != 1 or summary['configuration']['missing_frames']:
                        raise RuntimeError('Mixed or missing runtime configuration')
                    frames_path = next(a['path'] for a in response['artifacts'] if a['kind'] == 'profile_frames')
                    frames = [json.loads(line) for line in Path(frames_path).read_text(encoding='utf-8').splitlines()]
                    if len(frames) != 600 or any(frame['entities'] != 243 for frame in frames):
                        raise RuntimeError('Generated 240-cube scene was not present throughout the profile')
                    if not report['metadata']['gpu_timestamp_sampling']['cpu_only_contract_satisfied']:
                        raise RuntimeError('Normal renderer scheduling was not retained')
                    rows.append(dict(variant=label, round=round_index, mode=mode,
                                     summary=summary, metadata=report['metadata'], artifacts=response['artifacts']))
                    (output / 'profiles.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
                controls('interactive', {'keys': ['Num5']})
                before = state('before-clicks')
                controls('select-tool', {'pointer': [307, 295], 'buttons': [True, False, False]})
                selected = state('selected')
                if selected['audit_selected'] != 1 or selected['audit_clicks'] != before['audit_clicks'] + 1:
                    raise RuntimeError('Item selection did not reach the controller')
                capture = call('selected-capture', ['capture'])
                controls('use-tool', {'pointer': [1025, 545], 'buttons': [True, False, False]})
                controls('close', {'pointer': [1135, 110], 'buttons': [True, False, False]})
                actions = state('actions')
                if actions['audit_uses'] != before['audit_uses'] + 1 or actions['audit_mode'] != 1:
                    raise RuntimeError('Use/close actions did not reach the controller')
                controls('reload-start', {'keys': ['Num6'], 'pointer': [20, 20]})
                memory_before = process.memory_info()._asdict()
                deadline = time.monotonic() + 30
                for poll in range(120):
                    current = state(f'reload-{poll}')
                    if current['audit_reloads'] >= actions['audit_reloads'] + 100:
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError('Reload deadline')
                    time.sleep(.25)
                else:
                    raise RuntimeError('Reload count not reached')
                controls('reload-stop', {'keys': ['Num5']})
                settled = state('reload-final')
                controls('after-reload-click', {'pointer': [307, 295], 'buttons': [True, False, False]})
                final = state('final')
                if final['audit_clicks'] != settled['audit_clicks'] + 1 or final['audit_selected'] != 1:
                    raise RuntimeError('Post-reload click failed')
                lifecycle.append(dict(variant=label, round=round_index, before=before, actions=actions,
                    final=final, memory_before=memory_before, memory_after=process.memory_info()._asdict(),
                    capture_artifacts=capture['artifacts'], launch=launch['data']['session']))
                (output / 'lifecycle.json').write_text(json.dumps(lifecycle, indent=2), encoding='utf-8')
                print(prefix, 'five modes, clicks, actions and reloads passed', flush=True)
            finally:
                call('stop', ['session', 'stop'])


if __name__ == '__main__':
    main()
