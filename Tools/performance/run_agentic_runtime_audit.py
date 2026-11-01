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



def gpu_memory(pid):
    """Windows per-process GPU allocations, summed across reported adapters."""
    command = ("Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory "
               "-ErrorAction Stop | Where-Object { $_.Name -like 'pid_" + str(int(pid)) +
               "_*' } | Select-Object Name,DedicatedUsage,SharedUsage | ConvertTo-Json -Compress")
    result = subprocess.run(['powershell', '-NoProfile', '-NonInteractive', '-Command', command],
                            capture_output=True, text=True, check=True, timeout=30)
    if not result.stdout.strip():
        raise RuntimeError('No GPU memory counters for the live runtime process')
    counters = json.loads(result.stdout)
    if isinstance(counters, dict):
        counters = [counters]
    return dict(dedicated_bytes=sum(int(row['DedicatedUsage']) for row in counters),
                shared_bytes=sum(int(row['SharedUsage']) for row in counters), counters=counters)


def check_lifecycle_state(current, mode, last_reload, last_progress, now):
    """Reject mode changes, unexpected control reloads and stalled workloads."""
    expected_mode = 5 if mode == 'reload' else 4
    count = current['audit_reloads']
    if current['audit_mode'] != expected_mode or count < last_reload:
        raise RuntimeError('Lifecycle mode changed or reload counter moved backwards')
    if mode == 'idle' and count != last_reload:
        raise RuntimeError('Idle control unexpectedly reloaded')
    if count > last_reload:
        return count, now
    if mode == 'reload' and now - last_progress > 30:
        raise RuntimeError('Reload soak stopped making progress for 30 seconds')
    return last_reload, last_progress


def memory_snapshot(call, name):
    """Require an allocator observation, not merely a successful command."""
    response = call(name, ['memory', 'snapshot', '--request-key', name])
    artifacts = [a for a in response['artifacts'] if a['kind'] == 'memory_snapshot']
    if len(artifacts) != 1:
        raise RuntimeError('Memory snapshot must publish one immutable artifact')
    snapshot = json.loads(Path(artifacts[0]['path']).read_text(encoding='utf-8'))
    if snapshot.get('rust_allocations', {}).get('available') is not True:
        raise RuntimeError('Memory ownership requires the diagnostic allocator')
    return artifacts[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--engine', action='append', required=True)
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--reload-seconds', type=float, default=0,
                        help='Minimum lifecycle duration per process (reload default: 100 reloads only)')
    parser.add_argument('--lifecycle-mode', choices=['reload', 'idle'], default='reload',
                        help='Matched inventory control: reload or leave the loaded UI idle')
    parser.add_argument('--lifecycle-observation', choices=['snapshots', 'endpoints'], default='snapshots',
                        help='Endpoints avoids runtime query allocations during process-memory sampling; progress verified at endpoints only')
    parser.add_argument('--lifecycle-warmup', type=float, default=0,
                        help='Seconds in the selected lifecycle mode before memory sampling')
    parser.add_argument('--memory-snapshots', action='store_true',
                        help='Collect native ownership artifacts at warm, active-end and cooldown boundaries')
    parser.add_argument('--lifecycle-cooldown', type=float, default=0,
                        help='Seconds to hold the same UI idle after the active interval, recording process memory')
    parser.add_argument('--gpu-memory', action='store_true',
                        help='Record Windows per-process GPU allocation counters during reloads')
    parser.add_argument('--skip-profiles', action='store_true',
                        help='Run interactions and lifecycle checks without timing profiles')
    parser.add_argument('--viewport', type=int, nargs=2, default=[1280, 720],
                        metavar=('WIDTH', 'HEIGHT'))
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('rounds must be positive')
    if not 0 <= args.reload_seconds <= 86400:
        parser.error('reload-seconds must be between 0 and 86400')
    if not 0 <= args.lifecycle_warmup <= 3600:
        parser.error('lifecycle-warmup must be between 0 and 3600')
    if not 0 <= args.lifecycle_cooldown <= 3600:
        parser.error('lifecycle-cooldown must be between 0 and 3600')
    if args.lifecycle_observation == 'endpoints' and args.reload_seconds <= 0:
        parser.error('endpoints requires a positive lifecycle duration')
    if args.gpu_memory and os.name != 'nt':
        parser.error('gpu-memory requires Windows GPU performance counters')
    if args.viewport[0] < 1280 or args.viewport[1] < 720:
        parser.error('survival fixture requires at least 1280x720')
    # The fixture's fixed-size satchel is centered, not scaled with the window.
    def satchel_point(x, y):
        return [x + (args.viewport[0] - 1280) / 2,
                y + (args.viewport[1] - 720) / 2]
    import psutil

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
                fields = ['audit_mode', 'audit_selected', 'audit_clicks', 'audit_uses', 'audit_reloads', 'audit_ticks']
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
                    if args.skip_profiles:
                        continue
                    response = call(name + '-profile', ['profile', '--sampling', 'cpu_only',
                        '--frames', '600', '--warmup', '120', '--label', prefix + '-' + name, '--timeout', '90'])
                    summary = response['data']['summary']
                    report_path = next(a['path'] for a in response['artifacts'] if a['kind'] == 'profile_report')
                    report = json.loads(Path(report_path).read_text(encoding='utf-8'))
                    if report['metadata']['viewport'] != args.viewport:
                        raise RuntimeError('Profile viewport differs from requested fixture size')
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
                controls('select-tool', {'pointer': satchel_point(307, 295), 'buttons': [True, False, False]})
                selected = state('selected')
                if selected['audit_selected'] != 1 or selected['audit_clicks'] != before['audit_clicks'] + 1:
                    raise RuntimeError('Item selection did not reach the controller')
                capture = call('selected-capture', ['capture'])
                controls('use-tool', {'pointer': satchel_point(1025, 545), 'buttons': [True, False, False]})
                controls('close', {'pointer': satchel_point(1135, 110), 'buttons': [True, False, False]})
                actions = state('actions')
                if actions['audit_uses'] != before['audit_uses'] + 1 or actions['audit_mode'] != 1:
                    raise RuntimeError('Use/close actions did not reach the controller')
                controls('reload-start', {'keys': ['Num6' if args.lifecycle_mode == 'reload' else 'Num5'], 'pointer': [20, 20]})
                if args.lifecycle_warmup:
                    time.sleep(args.lifecycle_warmup)
                ownership = {}
                if args.memory_snapshots:
                    ownership['warm'] = memory_snapshot(call, prefix + '-memory-warm')
                memory_before = process.memory_info()._asdict()
                started = time.monotonic()
                deadline = started + args.reload_seconds + 30
                samples = []
                poll = 0
                last_reload = actions['audit_reloads']
                last_progress = started
                last_state = None
                while True:
                    observed = args.lifecycle_observation == 'snapshots' or last_state is None
                    if observed:
                        last_state = state(f'reload-{poll}')
                        last_reload, last_progress = check_lifecycle_state(
                            last_state, args.lifecycle_mode, last_reload, last_progress, time.monotonic())
                    current = last_state
                    elapsed = time.monotonic() - started
                    sample = dict(elapsed_seconds=elapsed, globals=current, globals_sampled=observed,
                                  memory=process.memory_info()._asdict())
                    if args.gpu_memory:
                        sample['gpu_memory'] = gpu_memory(process.pid)
                    samples.append(sample)
                    (output / f'{prefix}-reload-memory.json').write_text(
                        json.dumps(samples, indent=2), encoding='utf-8')
                    if ((args.lifecycle_observation == 'endpoints' or args.lifecycle_mode == 'idle' or current['audit_reloads'] >= actions['audit_reloads'] + 100)
                            and elapsed >= args.reload_seconds):
                        break
                    if time.monotonic() >= deadline:
                        raise RuntimeError('Reload deadline')
                    time.sleep(5 if args.reload_seconds else .25)
                    poll += 1
                endpoint = state('lifecycle-endpoint')
                check_lifecycle_state(endpoint, args.lifecycle_mode, last_reload, last_progress, time.monotonic())
                if args.lifecycle_mode == 'reload' and endpoint['audit_reloads'] < actions['audit_reloads'] + 100:
                    raise RuntimeError('Lifecycle endpoint did not reach 100 reloads')
                if args.memory_snapshots:
                    ownership['active_end'] = memory_snapshot(call, prefix + '-memory-active-end')
                controls('reload-stop', {'keys': ['Num5']})
                settled = state('reload-final')
                cooldown_samples = []
                cooldown_started = time.monotonic()
                if args.lifecycle_cooldown:
                    while True:
                        cooldown_elapsed = time.monotonic() - cooldown_started
                        cooldown_samples.append(dict(elapsed_seconds=cooldown_elapsed,
                            memory=process.memory_info()._asdict()))
                        (output / f'{prefix}-cooldown-memory.json').write_text(
                            json.dumps(cooldown_samples, indent=2), encoding='utf-8')
                        if cooldown_elapsed >= args.lifecycle_cooldown:
                            break
                        time.sleep(min(5, args.lifecycle_cooldown - cooldown_elapsed))
                    cooled = state('cooldown-final')
                    check_lifecycle_state(cooled, 'idle', settled['audit_reloads'], 0, time.monotonic())
                if args.memory_snapshots:
                    ownership['cooldown_end'] = memory_snapshot(call, prefix + '-memory-cooldown-end')
                controls('after-reload-click', {'pointer': satchel_point(307, 295), 'buttons': [True, False, False]})
                final = state('final')
                if final['audit_clicks'] != settled['audit_clicks'] + 1 or final['audit_selected'] != 1:
                    raise RuntimeError('Post-reload click failed')
                lifecycle.append(dict(variant=label, round=round_index, before=before, actions=actions,
                    final=final, memory_before=memory_before, memory_after=process.memory_info()._asdict(),
                    reload_seconds=elapsed, lifecycle_mode=args.lifecycle_mode,
                    lifecycle_observation=args.lifecycle_observation, lifecycle_endpoint=endpoint,
                    lifecycle_warmup=args.lifecycle_warmup, memory_samples=len(samples),
                    lifecycle_cooldown=args.lifecycle_cooldown, cooldown_samples=len(cooldown_samples),
                    ownership_snapshots=ownership,
                    capture_artifacts=capture['artifacts'], launch=launch['data']['session']))
                (output / 'lifecycle.json').write_text(json.dumps(lifecycle, indent=2), encoding='utf-8')
                print(prefix, 'five modes, clicks, actions and', args.lifecycle_mode, 'lifecycle passed',
                      '(profiles skipped)' if args.skip_profiles else '(profiles recorded)', flush=True)
            finally:
                call('stop', ['session', 'stop'])


if __name__ == '__main__':
    main()
