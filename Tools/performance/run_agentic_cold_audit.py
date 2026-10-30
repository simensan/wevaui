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
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('--rounds must be positive')
    engines = []
    for value in args.engine:
        label, separator, path = value.partition('=')
        if not separator or not label or not all(c.isalnum() or c in '-_' for c in label):
            parser.error('--engine requires a simple LABEL=BUNDLE_PATH')
        if any(name == label for name, _ in engines):
            parser.error('engine labels must be unique')
        engines.append((label, Path(path).resolve(strict=True)))
    project = args.project.resolve(strict=True)
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
                call('world', ['capture', '--session', session, '--ready', 'audit_mode',
                               '--ready-value', '0', '--timeout', '30'])
                profile_path = output / f'{prefix}-profile.json'
                with profile_path.open('w', encoding='utf-8') as stdout, \
                        (output / f'{prefix}-profile.stderr').open('w', encoding='utf-8') as stderr:
                    profile_process = subprocess.Popen([
                        str(cli), 'profile', '--session', session, '--sampling', 'cpu_only',
                        '--frames', '6000', '--warmup', '0', '--label', prefix,
                        '--timeout', '90', '--json'], env=env, stdout=stdout, stderr=stderr)
                    # Coverage is proved below against the actual applied input
                    # tick, not assumed from this scheduling delay.
                    time.sleep(0.5)
                    applied = call('open', ['session', 'input', '--session', session,
                                           '--request-key', prefix, '--ticks', '2', '--controls',
                                           json.dumps({'keys': ['Num5']})])
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
                tick = applied['data']['first_tick']
                indices = [i for i, frame in enumerate(frames) if frame['tick'] == tick]
                if len(indices) != 1 or not 0 < indices[0] < len(frames) - 2:
                    raise RuntimeError('Input opening tick is not inside the retained profile')
                if not report['metadata']['gpu_timestamp_sampling']['cpu_only_contract_satisfied']:
                    raise RuntimeError('Normal renderer scheduling was not retained')
                index = indices[0]
                frame = frames[index]
                ui_index = report['summary']['section_names'].index('ui')
                opened = call('opened', ['capture', '--session', session, '--ready', 'audit_mode',
                                         '--ready-value', '4', '--timeout', '30'])
                rows.append({
                    'variant': label, 'round': round_index, 'input_tick': tick,
                    'main_cpu_ms': frame['main_cpu_ms'],
                    'main_ui_ms': frame['sections_ms'][ui_index],
                    'worker_ui_ms': frame.get('worker_ms', {}).get('ui'),
                    'next_interval_ms': frames[index + 1]['interval_ms'],
                    'entities': frame['entities'],
                    'frames': report['summary']['frames'],
                    'metadata': report['metadata'],
                    'profile_artifacts': response['artifacts'],
                    'capture_artifacts': opened['artifacts'],
                })
                (output / 'results.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
                print(prefix, 'opening main CPU', round(frame['main_cpu_ms'], 3), 'ms', flush=True)
            finally:
                # Stop only this runner's session. A lost profile observation
                # must never trigger a replacement runtime launch.
                call('stop', ['session', 'stop', '--session', session])
                if profile_process is not None and profile_process.poll() is None:
                    profile_process.wait(timeout=100)


if __name__ == '__main__':
    main()
