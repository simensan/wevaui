#!/usr/bin/env python3
"""Check packed draw/glyph preparation cache pixels and reuse in native Godot."""
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
    parser.add_argument('--cache-kind', choices=('packing', 'glyphs'), default='packing')
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    output = args.out or Path(tempfile.mkdtemp(prefix='weva-packed-draw-'))
    output.mkdir(parents=True, exist_ok=True)
    total_hits = 0
    for script, marker, expected in [('triangle_batch_probe', 'triangle batching', 6),
                                      ('packed_draw_probe', 'packed draws', 20)]:
        profiles = {}
        for mode in ('uncached', 'cached'):
            folder = (output / script / mode).resolve()
            folder.mkdir(parents=True, exist_ok=True)
            env = dict(os.environ, WEVA_BATCH_OUTPUT=str(folder), WEVA_GODOT_DRAW_LOG='1',
                       GODOT_SILENCE_ROOT_WARNING='1')
            env.pop('WEVA_GODOT_DISABLE_BATCHING', None)
            env.pop('WEVA_GODOT_DISABLE_PACK_CACHE', None)
            env.pop('WEVA_DISABLE_GLYPH_REUSE', None)
            if args.cache_kind == 'glyphs':
                env['WEVA_PAINT_LOG'] = '1'
            if mode == 'uncached':
                env['WEVA_GODOT_DISABLE_PACK_CACHE' if args.cache_kind == 'packing'
                    else 'WEVA_DISABLE_GLYPH_REUSE'] = '1'
            command = [args.godot, '--path', str(args.project.resolve()), '--script',
                       f'res://{script}.gd', '--audio-driver', 'Dummy', '--rendering-method',
                       args.rendering_method, '--position', '-10000,-10000']
            log_path = folder / 'godot.log'
            with log_path.open('w', encoding='utf-8') as stream:
                result = subprocess.run(command, env=env, stdout=stream,
                                        stderr=subprocess.STDOUT, text=True, timeout=120)
            log = log_path.read_text(encoding='utf-8')
            if result.returncode or f'{marker}: {expected} images' not in log or 'ERROR:' in log:
                raise RuntimeError(log)
            profiles[mode] = [tuple(map(int, match)) for match in re.findall(
                r'; (\d+) vertices; (\d+) submissions, largest \d+ vertices; frame \d+'
                r'; packed (\d+) vertices, (\d+) batches reused', log)]
            if args.cache_kind == 'glyphs':
                profiles[mode + '_glyphs'] = [int(v) for v in re.findall(
                    r'\[(\d+) glyph subtrees reused\]', log)]
        images = sorted((output / script / 'uncached').glob('*.png'))
        assert len(images) == expected, images
        for path in images:
            assert path.read_bytes() == (output / script / 'cached' / path.name).read_bytes(), path
        cold, hot = profiles['uncached'], profiles['cached']
        assert cold and hot, 'Missing draw profiles'
        if args.cache_kind == 'packing':
            hits = sum(v[3] for v in hot)
            assert sum(v[3] for v in cold) == 0 and hits > 0
            assert all(v[0] == v[2] for v in cold), 'Bypass did not repack all vertices'
            assert any(v[0] > 0 and v[2] == 0 and v[3] > 0 for v in hot), 'No complete cache hit'
        else:
            assert profiles['uncached_glyphs'] and profiles['cached_glyphs'], 'Missing glyph profiles'
            assert sum(profiles['uncached_glyphs']) == 0
            hits = sum(profiles['cached_glyphs'])
        # Engine startup can schedule an extra draw; compare the distinct geometry
        # submissions, not the number of startup callbacks.
        assert {v[:2] for v in cold} == {v[:2] for v in hot}, 'Submissions changed'
        total_hits += hits
        print(f'{script}: {expected} identical images; {hits} {args.cache_kind} hits', flush=True)
    assert total_hits > 0, 'No reuse exercised'
    print(f'Artifacts: {output}')


if __name__ == '__main__':
    main()
