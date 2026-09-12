#!/usr/bin/env python3
"""Compare retained canvas properties with the same library's baseline renderer."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
from PIL import Image, ImageChops
import build_metadata

PHASES = ["initial", "self-modulate", "redraw", "parent-modulate", "transform",
          "clip", "hide", "show", "empty", "reload", "light-included",
          "light-excluded", "visibility-excluded", "sdf-on", "sdf-off",
          "backdrop", "plain", "reparent", "material", "material-change", "material-clear",
          "texture-nearest", "texture-linear", "instance-uniform", "instance-change", "instance-default", "instance-reload",
          "inherited-material", "inherited-change", "inherited-clear", "own-material"]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', type=Path, required=True)
    parser.add_argument('--library', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='New staging directory')
    args = parser.parse_args()
    library, engine, out = args.library.resolve(), args.godot.resolve(), args.output.resolve()
    root = Path(__file__).resolve().parents[2]
    metadata = json.loads(Path(str(library) + '.build.json').read_text(encoding='utf-8'))
    if metadata['library_sha256'] != build_metadata.sha256(library) or metadata['source']['sha256'] != build_metadata.source_hash(root):
        raise RuntimeError('Library metadata does not match the binary and current source')
    out.mkdir(parents=True, exist_ok=False)
    project = out / 'project'
    shutil.copytree(Path(__file__).parent / 'project', project,
                    ignore=shutil.ignore_patterns('.godot', 'bin'))
    (project / 'addons/weva/bin').mkdir(exist_ok=True)
    shutil.copy2(library, project / 'addons/weva/bin' / library.name)
    env = {k: v for k, v in os.environ.items() if not k.startswith('WEVA_')}

    def run(label, options, environment):
        command = [str(engine), '--audio-driver', 'Dummy', '--path', str(project), *options]
        try:
            result = subprocess.run(command, env=environment, stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, encoding='utf-8', errors='replace', timeout=90)
        except subprocess.TimeoutExpired as error:
            output = error.stdout or b''
            (out / (label + '.log')).write_text(output.decode('utf-8', errors='replace') if isinstance(output, bytes) else output, encoding='utf-8')
            raise
        (out / (label + '.log')).write_text(result.stdout, encoding='utf-8')
        if result.returncode or 'ERROR:' in result.stdout:
            raise RuntimeError(label + ' failed; inspect its log')

    run('import', ['--headless', '--import'], env)
    comparisons = []
    for renderer in ['gl_compatibility', 'mobile']:
        for retained in [False, True]:
            label = renderer + ('-retained' if retained else '-baseline')
            scenario = dict(env, WEVA_RETAINED_TEST_OUT=str(out / label))
            if retained:
                scenario['WEVA_GODOT_RETAIN_BATCHES'] = '1'
            run(label, ['--quit-after', '180', '--rendering-method', renderer,
                '--position', '-10000,-10000', '--scene', 'res://retained_canvas_tests.tscn'], scenario)
        for phase in PHASES:
            a = Image.open(out / f'{renderer}-baseline-{phase}.png').convert('RGBA')
            b = Image.open(out / f'{renderer}-retained-{phase}.png').convert('RGBA')
            difference = ImageChops.difference(a, b)
            identical = not any(channel[1] for channel in difference.getextrema())
            comparisons.append(dict(renderer=renderer, phase=phase, identical=identical))
            if not identical:
                difference.save(out / f'{renderer}-{phase}-difference.png')
    receipt = dict(library_sha256=metadata['library_sha256'], source_sha256=metadata['source']['sha256'],
        comparisons=comparisons, passed=all(row['identical'] for row in comparisons))
    (out / 'verification.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(f"Retained canvas: {len(comparisons)} comparisons; {'PASS' if receipt['passed'] else 'FAIL'}")
    return 0 if receipt['passed'] else 1

if __name__ == '__main__':
    raise SystemExit(main())
