"""Compare first UI opening in explicit Agentic SDKs, using owned live sessions.

Generate the project with create_agentic_audit_project.py --defer-ui first.
Each --engine is LABEL=BUNDLE_PATH. Raw CLI responses and hashed profile/capture
artifacts are retained. No configured engine checkout is changed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--engine', action='append', required=True)
    parser.add_argument('--rounds', type=int, default=5)
    parser.add_argument('--reopens', type=int, default=0,
                        help='Measured hide/show cycles after each first opening')
    parser.add_argument('--prepare-hidden', action='store_true',
                        help='Measure key-8 hidden preparation, then key-5 opening')
    parser.add_argument('--prepare-engine', action='append', default=[], metavar='LABEL',
                        help='Use hidden preparation only for this engine label; repeatable')
    parser.add_argument('--check-actions', action='store_true',
                        help='Verify selection, equip and close after the opening profiles (720p fixture)')
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('--rounds must be positive')
    if args.reopens < 0:
        parser.error('--reopens must be nonnegative')
    engines = []
    for value in args.engine:
        label, separator, path = value.partition('=')
        if not separator or not label or not all(c.isalnum() or c in '-_' for c in label):
            parser.error('--engine requires a simple LABEL=BUNDLE_PATH')
        if any(name == label for name, _ in engines):
            parser.error('engine labels must be unique')
        engines.append((label, Path(path).resolve(strict=True)))
    project = args.project.resolve(strict=True)
    if set(args.prepare_engine) - {label for label, _ in engines}:
        parser.error('--prepare-engine must name an --engine label')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = os.environ.copy()
    env.update(AGENTIC_SESSION_HOME=str(output / 'registry'),
               AGENTIC_SAVE_DIR=str(output / 'saves'), AGENTIC_WINDOW_OFFSCREEN='1')
    rows = []
    for round_index in range(args.rounds):
        order = engines if round_index % 2 == 0 else list(reversed(engines))
        for label, engine in order:
            prefix = f'{label}-{round_index}'
            cli = engine / 'target/fast/agentic-tool.exe'
            runtime = engine / 'target/fast/dev_runtime.exe'
            if os.name != 'nt':
                cli = cli.with_suffix('')
                runtime = runtime.with_suffix('')

            def call(suffix, command):
                process = subprocess.run([str(cli), *command, '--json'], env=env,
                                         capture_output=True, text=True, timeout=120)
                (output / f'{prefix}-{suffix}.json').write_text(process.stdout, encoding='utf-8')
                (output / f'{prefix}-{suffix}.stderr').write_text(process.stderr, encoding='utf-8')
                response = json.loads(process.stdout)
                if process.returncode or response['status'] not in ('completed', 'accepted'):
                    raise RuntimeError(f'{prefix}-{suffix}: {response}')
                return response

            launch = call('launch', ['dev', '--runtime', str(runtime), '--engine', str(engine),
                                    '--project', str(project), '--session', prefix, '--timeout', '90'])
            session = launch['data']['session']['id']
            profile_process = None
            try:
                world = call('world', ['capture', '--session', session, '--ready', 'audit_mode',
                               '--ready-value', '0', '--timeout', '30'])
                prepare = args.prepare_hidden or label in args.prepare_engine
                phases = ['prepare', 'prepared-open'] if prepare else ['cold']
                phases += [f'retained-{n + 1}' for n in range(args.reopens)]
                for phase in phases:
                    if phase.startswith('retained-'):
                        call(phase + '-hide', ['session', 'input', '--session', session,
                             '--request-key', prefix + '-hide-' + phase, '--ticks', '2',
                             '--controls', json.dumps({'keys': ['Num1']})])
                    profile_path = output / f'{prefix}-{phase}-profile.json'
                    with profile_path.open('w', encoding='utf-8') as stdout, \
                            (output / f'{prefix}-{phase}-profile.stderr').open('w', encoding='utf-8') as stderr:
                        profile_process = subprocess.Popen([
                            str(cli), 'profile', '--session', session, '--sampling', 'cpu_only',
                            '--frames', '6000', '--warmup', '0', '--label', prefix,
                            '--timeout', '90', '--json'], env=env, stdout=stdout, stderr=stderr)
                        # Coverage is proved below against the actual applied input
                        # tick, not assumed from this scheduling delay.
                        time.sleep(0.5)
                        applied = call(phase + '-open', ['session', 'input', '--session', session,
                                               '--request-key', prefix + '-' + phase, '--ticks', '2', '--controls',
                                               json.dumps({'keys': ['Num8' if phase == 'prepare' else 'Num5']})])
                        if profile_process.wait(timeout=100):
                            raise RuntimeError(f'{prefix}: profile failed; see retained evidence')
                    response = json.loads(profile_path.read_text(encoding='utf-8'))
                    if response['status'] != 'completed':
                        raise RuntimeError(f'{prefix}: incomplete profile')
                    artifacts = {a['kind']: a for a in response['artifacts']}
                    for artifact in artifacts.values():
                        digest = hashlib.sha256(Path(artifact['path']).read_bytes()).hexdigest()
                        if digest != artifact['sha256']:
                            raise RuntimeError(f'Artifact hash mismatch: {artifact["path"]}')
                    report = json.loads(Path(artifacts['profile_report']['path']).read_text(encoding='utf-8'))
                    frames = [json.loads(line) for line in
                              Path(artifacts['profile_frames']['path']).read_text(encoding='utf-8').splitlines()]
                    if len(frames) != 6000 or report['summary']['frames'] != 6000:
                        raise RuntimeError('Incomplete cold profile')
                    if report['summary']['marked_frames'] or any(f['entities'] != 243 for f in frames):
                        raise RuntimeError('Marked profile or missing audit world')
                    tick = applied['data']['first_tick']
                    indices = [i for i, frame in enumerate(frames) if frame['tick'] == tick]
                    if len(indices) != 1 or not 0 < indices[0] < len(frames) - 8:
                        raise RuntimeError('Input opening tick is not inside the retained profile')
                    if not report['metadata']['gpu_timestamp_sampling']['cpu_only_contract_satisfied']:
                        raise RuntimeError('Normal renderer scheduling was not retained')
                    index = indices[0]
                    frame = frames[index]
                    ui_index = report['summary']['section_names'].index('ui')
                    sections = dict(zip(report['summary']['section_names'], frame['sections_ms']))
                    if phase == 'prepare' and sections.get('ui_prepare', 0) <= 0:
                        raise RuntimeError('Hidden preparation did not execute at the injected tick')
                    opened = call(phase + '-opened', ['capture', '--session', session, '--ready', 'audit_mode',
                                             '--ready-value', '0' if phase == 'prepare' else '4', '--timeout', '30'])
                    for artifact in opened['artifacts']:
                        if hashlib.sha256(Path(artifact['path']).read_bytes()).hexdigest() != artifact['sha256']:
                            raise RuntimeError('Opening capture hash mismatch')
                    if phase == 'prepare':
                        initial = next(a['sha256'] for a in world['artifacts'] if a['kind'] == 'image')
                        hidden = next(a['sha256'] for a in opened['artifacts'] if a['kind'] == 'image')
                        if initial != hidden:
                            raise RuntimeError('Hidden preparation changed the captured world image')
                    rows.append({
                        'variant': label, 'round': round_index, 'phase': phase, 'input_tick': tick,
                        'main_cpu_ms': frame['main_cpu_ms'],
                        'main_ui_ms': frame['sections_ms'][ui_index],
                        'sections_ms': sections,
                        'runtime_sha256': launch['data']['session']['executable_sha256'],
                        'worker_ui_ms': frame.get('worker_ms', {}).get('ui'),
                        'next_interval_ms': frames[index + 1]['interval_ms'],
                        # Include following worker/backpressure costs rather than
                        # presenting only the input tick as the opening latency.
                        'opening_window': [dict(tick_offset=f['tick'] - tick,
                            main_cpu_ms=f['main_cpu_ms'], interval_ms=f['interval_ms'],
                            worker_ui_ms=f.get('worker_ms', {}).get('ui'))
                            for f in frames[index:index + 8]],
                        'entities': frame['entities'],
                        'frames': report['summary']['frames'],
                        'metadata': report['metadata'],
                        'profile_artifacts': response['artifacts'],
                        'capture_artifacts': opened['artifacts'],
                    })
                    (output / 'results.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
                    print(prefix, phase, 'opening main CPU', round(frame['main_cpu_ms'], 3), 'ms', flush=True)
                if args.check_actions:
                    if report['metadata']['viewport'] != [1280, 720]:
                        raise RuntimeError('Action coordinates require the 720p fixture')
                    for action, point in [('select', [307, 295]), ('equip', [1025, 545]), ('close', [1135, 110])]:
                        call(action, ['session', 'input', '--session', session,
                             '--request-key', prefix + '-' + action, '--ticks', '2',
                             '--controls', json.dumps({'pointer': point, 'buttons': [True, False, False]})])
                    state = call('actions', ['snapshot', '--session', session, '--entity', 'probe',
                        '--global', 'audit_selected', '--global', 'audit_clicks',
                        '--global', 'audit_uses', '--global', 'audit_mode'])
                    artifact = next(a for a in state['artifacts'] if a['kind'] == 'cpu_world_snapshot')
                    values = json.loads(Path(artifact['path']).read_text(encoding='utf-8'))['globals']
                    if any(values[k] != 1 for k in ['audit_selected', 'audit_clicks', 'audit_uses', 'audit_mode']):
                        raise RuntimeError('Selection/equip/close did not reach the controller')
                    print(prefix, 'selection, equip and close passed', flush=True)
            finally:
                # Stop only this runner's session. A lost profile observation
                # must never trigger a replacement runtime launch.
                call('stop', ['session', 'stop', '--session', session])
                if profile_process is not None and profile_process.poll() is None:
                    profile_process.wait(timeout=100)


if __name__ == '__main__':
    main()
