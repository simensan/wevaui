#!/usr/bin/env python3
"""Verify the standalone consumer project, its native input and relocated export."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', type=Path, required=True)
    parser.add_argument('--addon', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='New output directory')
    parser.add_argument('--native', action='store_true', help='Export and run a release executable')
    args = parser.parse_args()
    engine, addon, out = args.godot.resolve(), args.addon.resolve(), args.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).resolve().parents[2] / 'examples' / 'frontier_camp'
    project = out / 'project'
    shutil.copytree(source, project, ignore=shutil.ignore_patterns('addons', '.godot', 'artifacts', '__pycache__', '*.tmp'))
    spec = importlib.util.spec_from_file_location('frontier_install', source / 'install_addon.py')
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    installer.install(addon, project)
    manifest = json.loads((project / 'addons/weva/build.json').read_text())
    platform = 'windows' if sys.platform == 'win32' else 'linux'
    library = manifest['libraries'][platform]
    binary = project / 'addons/weva' / library['path']
    if digest(binary) != library['sha256']:
        raise RuntimeError('Installed library differs from the addon manifest')
    runs = []

    def run(label, command, expected=None):
        log = out / (label + '.engine.log')
        command = [str(command[0]), '--log-file', str(log), '--audio-driver', 'Dummy', *map(str, command[1:])]
        (out / (label + '.command.json')).write_text(json.dumps(command, indent=2))
        result = subprocess.run(command, cwd=out, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                encoding='utf-8', errors='replace', timeout=120)
        (out / (label + '.log')).write_text(result.stdout, encoding='utf-8')
        if result.returncode or 'ERROR:' in result.stdout or 'FAIL ' in result.stdout or (expected and expected not in result.stdout):
            print(result.stdout)
            raise RuntimeError(label + ' failed')
        match = re.search(r'frontier integration: (\d+) checks, 0 failures', result.stdout)
        runs.append({'name': label, 'checks': int(match[1]) if match else None, 'passed': True})
        print(label + ': ' + (match[0] if match else 'passed'), flush=True)

    run('import', [engine, '--headless', '--path', project, '--editor', '--quit-after', '60'])
    run('headless', [engine, '--headless', '--path', project, '--', '--check'], 'frontier integration:')
    for renderer in ('gl_compatibility', 'mobile'):
        run(renderer, [engine, '--path', project, '--rendering-method', renderer, '--position', '-10000,-10000',
                       '--', '--check', '--capture=' + str(out / renderer)], 'frontier integration:')

    # Ship a clean, self-contained editor project, with the exact installed addon.
    with zipfile.ZipFile(out / 'frontier-camp-project.zip', 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(project.rglob('*')):
            if path.is_file() and '.godot' not in path.parts:
                archive.write(path, Path('frontier-camp') / path.relative_to(project))

    if args.native:
        preset = 'Windows Desktop' if platform == 'windows' else 'Linux'
        exported = out / 'exported'
        exported.mkdir()
        executable = exported / ('FrontierCamp.exe' if platform == 'windows' else 'FrontierCamp.x86_64')
        run('export', [engine, '--headless', '--path', project, '--export-release', preset, executable])
        shipped = list(exported.rglob(binary.name))
        if len(shipped) != 1 or digest(shipped[0]) != library['sha256']:
            raise RuntimeError('Export did not include the exact extension library')
        # Paths were created here and verified to remain inside this output.
        relocated = out / 'playable'
        hidden = out / 'source-unavailable'
        for path in (exported, relocated, project, hidden):
            if not path.resolve().is_relative_to(out):
                raise RuntimeError('Relocation escaped the output directory')
        exported.rename(relocated)
        project.rename(hidden)
        executable = relocated / executable.name
        run('export-headless', [executable, '--headless', '--', '--check'], 'frontier integration:')
        run('export-render', [executable, '--rendering-method', 'gl_compatibility', '--position', '-10000,-10000',
                             '--', '--check', '--capture=' + str(out / 'export')], 'frontier integration:')
        for name in ('camp', 'crafted', 'settings'):
            if digest(out / ('gl_compatibility-' + name + '.png')) != digest(out / ('export-' + name + '.png')):
                raise RuntimeError('Export pixels differ: ' + name)
        hidden.rename(project)
        shutil.make_archive(str(out / 'frontier-camp-windows' if platform == 'windows' else out / 'frontier-camp-linux'),
                            'zip', relocated)

    receipt = {'passed': True, 'engine_sha256': digest(engine), 'addon_sha256': digest(addon),
               'library_sha256': library['sha256'], 'runs': runs,
               'export_pixels_match_project': True if args.native else None}
    (out / 'verification.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print('Verified project: ' + str(project))


if __name__ == '__main__':
    main()
