"""Compare opening versus hidden preparation in fresh Unity editor processes.

Explicit opt-in diagnostic, not player FPS or a GPU execution benchmark.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unity', type=Path, required=True)
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--renderer-prepared', action='store_true',
                        help='Also measure explicit renderer synchronization during hidden loading')
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('rounds must be positive')
    unity, project, fixtures = (p.resolve(strict=True) for p in (args.unity, args.project, args.fixtures))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    rows = []
    for round_index in range(args.rounds):
        modes = ['cold', 'prepared', 'renderer-prepared'] if args.renderer_prepared else ['cold', 'prepared']
        # Rotate three modes so each occupies every position once.
        if args.renderer_prepared:
            offset = round_index % len(modes)
            modes = modes[offset:] + modes[:offset]
        elif round_index % 2:
            modes.reverse()
        for mode in modes:
            name = f'{mode}-{round_index}'
            destination = output / name
            env = dict(os.environ, WEVA_PREPARATION_FIXTURE=str(fixtures),
                       WEVA_PREPARATION_OUTPUT=str(destination), WEVA_PREPARATION_MODE=mode)
            completed = subprocess.run([str(unity), '-batchmode', '-projectPath', str(project),
                '-executeMethod', 'Weva.EditorTools.PreparationBenchmark.Run', '-quit',
                '-screen-width', '1280', '-screen-height', '720', '-logFile', str(output / (name + '.log'))],
                env=env, timeout=300, creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
            if completed.returncode:
                raise RuntimeError(f'{name}: Unity exited {completed.returncode}; retained log')
            result = json.loads((destination / 'result.json').read_text(encoding='utf-8'))
            if result['mode'] != mode or [result['width'], result['height']] != [1280, 720]:
                raise RuntimeError('Unexpected benchmark configuration')
            result.update(round=round_index, image_sha256=hashlib.sha256((destination / 'shown.png').read_bytes()).hexdigest())
            retained = destination / 'retained.png'
            if retained.exists() and hashlib.sha256(retained.read_bytes()).hexdigest() != result['image_sha256']:
                raise RuntimeError(f'{name}: first and retained render differ; inspect evidence')
            rows.append(result)
            (output / 'results.json').write_text(json.dumps(rows, indent=2) + '\n', encoding='utf-8')
            print(name, 'loading', result['loading_cpu_ms'], 'opening', result['opening_cpu_ms'], flush=True)
    if len({r['image_sha256'] for r in rows}) != 1:
        raise RuntimeError('Cold and prepared output images differ; inspect retained evidence')


if __name__ == '__main__':
    main()
