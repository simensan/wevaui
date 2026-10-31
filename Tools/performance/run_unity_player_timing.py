"""Build/run the opt-in presented-frame audit. Whole-player timings, not isolated UI GPU time."""
import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import statistics
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from unity_player_processes import stop_owned_players

def summarize_measurements(output):
    summaries = []
    for path in sorted((output/'measurements').glob('*.json')):
        raw = json.loads(path.read_text())
        samples = raw['samples']
        if len(samples) != 600:
            raise RuntimeError(f'{path}: incomplete frame sample set')
        item = {k: raw[k] for k in ('scenario', 'round', 'width', 'height', 'target_fps', 'graphics_api',
                                    'device', 'unity', 'gpu_timer_frequency', 'cpu_timer_frequency', 'duplicates', 'missing')}
        item.update(world=raw.get('world', 'none'), world_renderers=raw.get('world_renderers', 0),
                    world_animated=raw.get('world_animated', 0))
        gpu = [s['gpu_frame_ms'] for s in samples if s['gpu_available']]
        item.update(samples=len(samples), gpu_available=len(gpu), gpu_complete=len(gpu) == len(samples),
                    focused_samples=sum(s['focused'] for s in samples))
        for key in ('cpu_frame_ms', 'cpu_main_ms', 'cpu_render_ms', 'present_wait_ms', 'gpu_frame_ms'):
            values = sorted(gpu if key == 'gpu_frame_ms' else [s[key] for s in samples])
            if not values or not all(math.isfinite(x) and x >= 0 for x in values):
                raise RuntimeError(f'{path}: invalid {key}')
            item[key] = {'median': statistics.median(values), 'p95': values[math.ceil(len(values)*.95)-1], 'max': max(values)}
        summaries.append(item)
    expected = {(scenario, round_index) for scenario in ('background', 'idle-hud', 'updating-hud', 'inventory', 'updating-inventory') for round_index in range(3)}
    if {(s['scenario'], s['round']) for s in summaries} != expected or len(summaries) != len(expected):
        raise RuntimeError('Missing or duplicate timing phases')
    (output/'summary.json').write_text(json.dumps(summaries, indent=2)+'\n')
    return summaries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--fps', type=int, default=60, help='Target rate; 0 is uncapped')
    parser.add_argument('--world', choices=('none', 'cubes'), default='none', help='Optional deterministic moving 3D scene')
    args = parser.parse_args()
    if args.fps < 0:
        parser.error('fps must be nonnegative')
    root = Path(__file__).resolve().parents[2]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    env = dict(os.environ, WEVA_TEST_PLAYER='1', WEVA_PLAYER_TIMING_FIXTURES=str(args.fixtures.resolve(strict=True)),
               WEVA_PLAYER_TIMING_OUTPUT=str(output/'measurements'), WEVA_PLAYER_TIMING_FPS=str(args.fps),
               WEVA_PLAYER_TIMING_WORLD=args.world)
    command = [str(args.unity.resolve(strict=True)), '-batchmode', '-projectPath', str(root), '-runTests',
               '-testPlatform', 'StandaloneWindows64', '-testFilter', 'Weva.Tests.Rendering.PlayerTimingAudit',
               '-buildPlayerPath', str(output/'player'), '-testResults', str(output/'results.xml'),
               '-logFile', str(output/'build.log')]
    settings_path = root / 'ProjectSettings/ProjectSettings.asset'
    original_settings = settings_path.read_bytes()
    try:
        result = subprocess.run(command, env=env, timeout=600,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    finally:
        try:
            stopped = stop_owned_players(output)
        finally:
            settings_path.write_bytes(original_settings)
    receipt = {'command': command, 'exit_code': result.returncode,
               'stopped_players': stopped, 'passed': False}
    if (output/'results.xml').exists():
        xml = ET.parse(output/'results.xml').getroot()
        receipt['tests'] = xml.attrib
        cases = xml.findall('.//test-case')
        receipt['passed'] = result.returncode == 0 and len(cases) == 1 and all(c.get('result') == 'Passed' for c in cases)
    (output/'receipt.json').write_text(json.dumps(receipt, indent=2)+'\n')
    if not receipt['passed']:
        raise RuntimeError('Player timing audit failed; retained build log, XML and any partial measurements')
    summaries = summarize_measurements(output)
    available = sum(s['gpu_available'] for s in summaries)
    print(f'Player audit passed: {available}/9000 GPU timings available; see per-phase coverage', flush=True)


if __name__ == '__main__':
    main()
