"""Qualify Linux imports with cold, warm and different-version help caches."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

from check import as_text, fingerprint


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--reference-editor', required=True, help='Different editor build to seed an incompatible help cache')
    parser.add_argument('--logs', required=True, type=Path, help='New evidence directory')
    parser.add_argument('--rounds', type=int, default=4)
    args = parser.parse_args()
    if sys.platform != 'linux':
        parser.error('This probe isolates Linux editor state through XDG directories')
    if args.rounds < 1:
        parser.error('--rounds must be positive')
    editor = Path(shutil.which(args.godot) or args.godot).resolve()
    reference = Path(shutil.which(args.reference_editor) or args.reference_editor).resolve()
    if not editor.is_file():
        parser.error(f'Editor not found: {editor}')
    if not reference.is_file():
        parser.error(f'Reference editor not found: {reference}')
    out = args.logs.resolve()
    out.mkdir(parents=True, exist_ok=False)
    report = {'passed': False, 'editor': fingerprint(editor), 'reference_editor': fingerprint(reference), 'runs': []}
    report['editor_version'] = subprocess.check_output([str(editor), '--version'], text=True, timeout=30).strip()
    report['reference_version'] = subprocess.check_output([str(reference), '--version'], text=True, timeout=30).strip()
    # The documentation cache's version hash includes the engine version.
    if report['editor_version'] == report['reference_version']:
        parser.error('--reference-editor must have a different version string to exercise cache regeneration')

    def save():
        (out / 'result.json').write_text(json.dumps(report, indent=2) + '\n')

    save()
    seed_cache = None
    for index in range(-1, args.rounds):
        directory = out / ('seed' if index == -1 else str(index))
        project = directory / 'project'
        project.mkdir(parents=True)
        (project / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Import shutdown probe"\n[rendering]\nrenderer/rendering_method="gl_compatibility"\n')
        (project / 'probe.gd').write_text('extends Node\n')
        environment = dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1')
        for kind in ['CACHE', 'CONFIG', 'DATA']:
            path = directory / kind.lower()
            path.mkdir()
            environment[f'XDG_{kind}_HOME'] = str(path)
        for state in (['seed'] if index == -1 else ['cold', 'warm', 'foreign-cache']):
            if state == 'foreign-cache':
                destination = directory / 'cache/godot' / seed_cache.name
                shutil.copy2(seed_cache, destination)
            command = [str(reference if index == -1 else editor), '--headless', '--path', str(project),
                       '--log-file', str(directory / f'{state}.engine.log'), '--import']
            started = time.monotonic()
            code = None
            try:
                result = subprocess.run(command, capture_output=True, text=True,
                                        encoding='utf-8', errors='replace', env=environment, timeout=60)
                code, log = result.returncode, result.stdout + result.stderr
            except subprocess.TimeoutExpired as error:
                log = as_text(error.stdout) + as_text(error.stderr) + '\nImport timed out\n'
            except OSError as error:
                log = str(error)
            (directory / f'{state}.log').write_text(log, encoding='utf-8')
            plain = re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', log)
            caches = list((directory / 'cache').rglob('editor_doc_cache-*.res'))
            passed = code == 0 and 'ERROR:' not in plain and '[ DONE ] first_scan_filesystem' in plain and bool(caches)
            report['runs'].append({'round': index, 'state': state, 'passed': passed,
                                   'returncode': code, 'seconds': time.monotonic() - started,
                                   'command': command, 'help_cache_created': bool(caches)})
            save()
            print(f'{state} import {index}: {"PASS" if passed else "FAIL"}', flush=True)
            if state == 'seed':
                if not passed or len(caches) != 1:
                    return 1
                seed_cache = caches[0]
    report['passed'] = len(report['runs']) == args.rounds * 3 + 1 and all(row['passed'] for row in report['runs'])
    save()
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
