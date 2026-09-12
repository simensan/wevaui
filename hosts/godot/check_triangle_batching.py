#!/usr/bin/env python3
"""Compare actual Godot pixels with consecutive triangle batching on/off.

Run under a real display (or xvfb-run), using --rendering-method gl_compatibility
or mobile. The project must already be imported with the library under test.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--rendering-method', default='gl_compatibility')
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    output = args.out or Path(tempfile.mkdtemp(prefix='weva-triangle-batch-'))
    output.mkdir(parents=True, exist_ok=True)
    profiles = {}
    for mode in ('single', 'batched'):
        folder = (output / mode).resolve()
        env = dict(os.environ, WEVA_BATCH_OUTPUT=str(folder), WEVA_GODOT_DRAW_LOG='1',
                   GODOT_SILENCE_ROOT_WARNING='1')
        env.pop('WEVA_GODOT_DISABLE_BATCHING', None)
        if mode == 'single':
            env['WEVA_GODOT_DISABLE_BATCHING'] = '1'
        command = [args.godot, '--path', str(args.project.resolve()),
                   '--script', 'res://triangle_batch_probe.gd', '--audio-driver', 'Dummy',
                   '--rendering-method', args.rendering_method, '--position', '-10000,-10000']
        log_path = output / (mode + '.log')
        with log_path.open('w', encoding='utf-8') as stream:
            result = subprocess.run(command, env=env, stdout=stream, stderr=subprocess.STDOUT,
                                    text=True, timeout=120)
        log = log_path.read_text(encoding='utf-8')
        if result.returncode or 'triangle batching: 6 images' not in log or 'ERROR:' in log:
            raise RuntimeError(log)
        profiles[mode] = [tuple(map(int, match)) for match in re.findall(
            r'; (\d+) vertices; (\d+) submissions, largest (\d+) vertices', log)]
    for name in ('overlap', 'clips', 'backdrop', 'sdf', 'capacity', 'empty'):
        # Same engine, image encoder and library: even PNG bytes must agree.
        a = (output / 'single' / (name + '.png')).read_bytes()
        b = (output / 'batched' / (name + '.png')).read_bytes()
        if a != b:
            raise AssertionError(f'Pixels changed: {name}; inspect {output}')
        print(f'{name}: identical', flush=True)
    single = max(profiles['single'])
    batched = max(profiles['batched'])
    if single[0] <= 65536 or single[0] != batched[0] or not 1 < batched[1] < single[1]:
        raise AssertionError(f'Capacity fixture did not exercise batching: {single} -> {batched}')
    if batched[2] > 65536:
        raise AssertionError(f'Batch exceeded upload bound: {batched}')
    print(f'triangle batching: 6 identical images; capacity {single[1]} -> {batched[1]} submissions')
    print(f'Artifacts: {output}')


if __name__ == '__main__':
    main()
