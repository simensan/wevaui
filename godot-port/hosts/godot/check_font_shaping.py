#!/usr/bin/env python3
"""Run the optional native font-adapter test extension in an isolated project.

Build the Godot host with -DWEVA_GODOT_FONT_TESTS=ON, then pass its separate
weva_font_tests.dll or libweva_font_tests.so. This test extension is not part
of the packaged addon. Artifacts are retained for inspection.
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--library', required=True, type=Path)
    parser.add_argument('--out', type=Path)
    parser.add_argument('--import-frames', type=int, default=60,
                        help='Editor warmup frames; instrumented builds may need longer initialization')
    args = parser.parse_args()
    if args.out:
        project = args.out.resolve()
        project.mkdir(parents=True, exist_ok=False)
    else:
        project = Path(tempfile.mkdtemp(prefix='weva-font-shaping-')).resolve()
    library = args.library.resolve()
    platform = {'.dll': 'windows', '.so': 'linux', '.dylib': 'macos'}[library.suffix]
    shutil.copy2(library, project / library.name)
    shutil.copy2(Path(__file__).resolve().parents[2] / 'tools/oracle/fonts/WevaMonoSans.ttf',
                 project / 'outline_fixture.ttf')
    (project / 'project.godot').write_text('[application]\nconfig/name="Weva font adapter checks"\n', encoding='utf-8')
    (project / 'font_tests.gdextension').write_text(
        '[configuration]\nentry_symbol="weva_font_tests_init"\ncompatibility_minimum="4.7"\n\n[libraries]\n' +
        ''.join(f'{platform}.{mode}.x86_64="res://{library.name}"\n' for mode in ('debug', 'release')),
        encoding='utf-8')
    (project / 'probe.gd').write_text(
        'extends SceneTree\nfunc _initialize():\n\tcall_deferred("run_checks")\n'
        'func run_checks():\n\tquit(WevaFontBackendTests.new().run_checks())\n', encoding='utf-8')
    env = dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1')
    for mode, extra in [('import', ['--editor', '--quit-after', str(args.import_frames)]),
                        ('check', ['--script', 'res://probe.gd'])]:
        command = [args.godot, '--headless', '--path', str(project)] + extra
        result = subprocess.run(command, env=env, capture_output=True, text=True, encoding='utf-8', timeout=120)
        log = result.stdout + result.stderr
        (project / (mode + '.log')).write_text(log, encoding='utf-8')
        if result.returncode or 'ERROR:' in log or 'FAIL font backend:' in log:
            raise RuntimeError(f'{mode} failed; artifacts: {project}\n{log}')
        if mode == 'check':
            match = re.search(r'godot font backend: (\d+) checks, 0 failures', log)
            if not match:
                raise RuntimeError(f'Missing native checks; artifacts: {project}\n{log}')
            print(match.group(0), flush=True)
    print(f'Artifacts: {project}')


if __name__ == '__main__':
    main()
