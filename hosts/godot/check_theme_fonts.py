#!/usr/bin/env python3
"""Check live theme fonts and font-size contexts, optionally with pixels."""
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
    parser.add_argument('--render', action='store_true')
    args = parser.parse_args()
    if args.out:
        project = args.out.resolve()
        project.mkdir(parents=True, exist_ok=False)
    else:
        project = Path(tempfile.mkdtemp(prefix='weva-theme-font-')).resolve()
    library = args.library.resolve()
    platform = {'.dll': 'windows', '.so': 'linux'}[library.suffix]
    shutil.copy2(library, project / library.name)
    for name in ('theme_font_tests.gd', 'theme_font_tests.tscn', 'font_size_tests.gd', 'font_size_tests.tscn'):
        shutil.copy2(Path(__file__).parent / 'project' / name, project / name)
    (project / 'project.godot').write_text(
        '[application]\nconfig/name="Weva theme font checks"\n\n'
        '[rendering]\nrenderer/rendering_method="gl_compatibility"\n', encoding='utf-8')
    (project / 'weva.gdextension').write_text(
        '[configuration]\nentry_symbol="weva_library_init"\ncompatibility_minimum="4.7"\n\n[libraries]\n' +
        ''.join(f'{platform}.{mode}.x86_64="res://{library.name}"\n' for mode in ('debug', 'release')),
        encoding='utf-8')
    steps = [('import', ['--headless', '--editor', '--quit-after', '60']),
             ('headless', ['--headless', 'theme_font_tests.tscn']),
             ('headless-size', ['--headless', 'font_size_tests.tscn'])]
    if args.render:
        steps.append(('render', ['--position', '-10000,-10000', '--audio-driver', 'Dummy',
                                 'theme_font_tests.tscn']))
        steps.append(('render-size', ['--position', '-10000,-10000', '--audio-driver', 'Dummy',
                                      'font_size_tests.tscn']))
    for name, extra in steps:
        result = subprocess.run([args.godot, '--path', str(project)] + extra,
                                env=dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1',
                                         WEVA_FONT_SIZE_OUTPUT=str(project / 'pixels')),
                                capture_output=True, text=True, encoding='utf-8', timeout=120)
        log = result.stdout + result.stderr
        (project / (name + '.log')).write_text(log, encoding='utf-8')
        if result.returncode or 'ERROR:' in log or 'FAIL  ' in log:
            raise RuntimeError(f'{name} failed; artifacts: {project}\n{log}')
        if name != 'import':
            prefix = 'godot font-size context' if name.endswith('-size') else 'godot theme fonts'
            marker = re.search(prefix + r': (\d+) checks, 0 failures', log)
            if not marker:
                raise RuntimeError(f'Missing font checks; artifacts: {project}\n{log}')
            print(name + ': ' + marker.group(0), flush=True)
    print(f'Artifacts: {project}')


if __name__ == '__main__':
    main()
