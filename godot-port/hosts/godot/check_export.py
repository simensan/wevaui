#!/usr/bin/env python3
"""Install into a fresh project and verify packed and native desktop exports.

Only the host binary and descriptor are copied from the development project.
The fixture uses imported PNG art and HTML/CSS included by the export filter.
Exporting a pack requires the Godot editor binary, but no export templates.
--native also exports and launches debug, release and embedded-pack games;
install the matching desktop export templates before using it.
"""

import argparse
from contextlib import nullcontext
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
import zlib


def png():
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 8, 6, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress((b'\0' + bytes([255, 32, 64, 255]) * 8) * 6)) + chunk(b'IEND', b''))


def run(command, cwd, expected=None):
    # Keep every command's complete output, including import/export failures.
    # Explicit engine logs also avoid the caller's global Godot app-data path.
    log_dir = Path(cwd) / 'verification-logs'
    log_dir.mkdir(exist_ok=True)
    number = len(list(log_dir.glob('*.output.log')))
    prefix = log_dir / f'{number:03d}'
    command = [command[0], '--log-file', str(prefix) + '.engine.log', *command[1:]]
    Path(str(prefix) + '.command.txt').write_text(repr(command) + '\n', encoding='utf-8')
    try:
        result = subprocess.run(command, cwd=cwd, text=True, encoding='utf-8', errors='replace',
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                env=dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1'), timeout=120)
    except subprocess.TimeoutExpired as error:
        partial = error.stdout or ''
        if isinstance(partial, bytes):
            partial = partial.decode('utf-8', errors='replace')
        Path(str(prefix) + '.output.log').write_text(partial, encoding='utf-8')
        raise
    Path(str(prefix) + '.output.log').write_text(result.stdout, encoding='utf-8')
    if result.returncode or 'ERROR:' in result.stdout or 'FAIL ' in result.stdout or (expected and expected not in result.stdout):
        print(result.stdout)
        raise RuntimeError(f'Command failed ({result.returncode}): {command}')
    for line in result.stdout.splitlines():
        if 'godot export smoke' in line or 'weva addon example:' in line:
            print(line, flush=True)
    return result.stdout


def capture_example(command, work, label):
    path = work / (label + '.png')
    output = run(command + ['--rendering-driver', 'opengl3', '--audio-driver', 'Dummy',
                           '--resolution', '640x720', '--position', '-10000,-10000',
                           '--quit-after', '120', '--', '--weva-capture=' + str(path)], work,
                 'godot export render: 640x720')
    (work / (label + '-render.log')).write_text(output, encoding='utf-8')
    if not path.is_file():
        raise RuntimeError(f'{label}: rendered example is missing')
    return hashlib.sha256(path.read_bytes()).hexdigest()


def native_exports(args, work, project, binary, library):
    """Godot must discover and copy the native library without our assistance."""
    preset = project / 'export_presets.cfg'
    original = preset.read_text()
    launches = []
    for mode, embedded in (('debug', False), ('release', False), ('release', True)):
        label = mode + ('-embedded' if embedded else '')
        directory = work / ('native ' + label)
        directory.mkdir()
        executable = directory / ('smoke.exe' if sys.platform == 'win32' else 'smoke.x86_64')
        preset.write_text(original + f'binary_format/embed_pck={str(embedded).lower()}\n')
        output = run([args.godot, '--headless', '--path', str(project),
                      '--export-' + mode, 'Smoke', str(executable)], work)
        (work / (label + '-export.log')).write_text(output, encoding='utf-8')
        if not executable.is_file() or executable.with_suffix('.pck').is_file() == embedded:
            raise RuntimeError(f'{label}: missing executable or wrong pack layout')
        shipped = list(directory.rglob(library))
        if len(shipped) != 1 or hashlib.sha256(shipped[0].read_bytes()).digest() != hashlib.sha256(binary.read_bytes()).digest():
            raise RuntimeError(f'{label}: Godot did not export the selected extension library')
        launches.append((label, mode, directory, executable.name))

    # All these paths were created inside this invocation's private directory.
    # Hide the source before launching, then relocate each exported directory.
    # This catches accidental references back to the editor project or build.
    hidden = work / 'source-unavailable'
    if not project.resolve().is_relative_to(work) or not hidden.resolve().is_relative_to(work):
        raise RuntimeError('Export fixture paths escaped their working directory')
    project.rename(hidden)
    for label, mode, directory, name in launches:
        relocated = work / ('launched ' + label)
        if not directory.resolve().is_relative_to(work) or not relocated.resolve().is_relative_to(work):
            raise RuntimeError('Native export paths escaped their working directory')
        directory.rename(relocated)
        command = [str(relocated / name), '--headless', '--quit-after', '120']
        expected = f'godot export smoke (native-{mode}): 13 checks, 0 failures'
        output = run(command + ['--', '--packed', '--native-' + mode], relocated, expected)
        (work / (label + '-run.log')).write_text(output, encoding='utf-8')
        if args.addon:
            output = run(command + ['--', '--weva-example-test'], relocated,
                         'weva addon example: 17 checks, 0 failures')
            (work / (label + '-example.log')).write_text(output, encoding='utf-8')
        if args.render:
            rendered = capture_example([str(relocated / name)], work, label)
            baseline = hashlib.sha256((work / 'project.png').read_bytes()).hexdigest()
            if rendered != baseline:
                raise RuntimeError(f'{label}: exported example pixels differ from the editor project')
            print(f'godot native render: {label} matches project pixels ({rendered})', flush=True)
        print(f'godot native export: {label} library, relocation and startup PASS', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', default=os.environ.get('GODOT_BIN', 'godot'))
    parser.add_argument('--library', type=Path, help='Built library; defaults to project/addons/weva/bin')
    parser.add_argument('--addon', type=Path, help='Install and test a packaged addon ZIP, including its example')
    parser.add_argument('--native', action='store_true', help='Also verify native debug/release exports (requires matching templates)')
    parser.add_argument('--debug-template', type=Path, help='Explicit custom debug template; requires --release-template and --native')
    parser.add_argument('--release-template', type=Path, help='Explicit custom release template; requires --debug-template and --native')
    parser.add_argument('--render', action='store_true', help='Compare example pixels in native exports (requires --native, --addon and a display)')
    parser.add_argument('--keep', action='store_true', help='Keep the isolated fixture for debugging')
    args = parser.parse_args()
    if args.addon and args.library:
        parser.error('--addon and --library are alternatives')
    if args.render and not (args.native and args.addon):
        parser.error('--render requires --native and --addon')
    custom_templates = {}
    if args.debug_template or args.release_template:
        if not (args.native and args.debug_template and args.release_template):
            parser.error('Custom templates require --native, --debug-template and --release-template together')
        for mode, path in [('debug', args.debug_template), ('release', args.release_template)]:
            path = path.resolve()
            if not path.is_file():
                parser.error(f'Custom {mode} template does not exist: {path}')
            custom_templates[mode] = path
    if sys.platform not in ('win32', 'linux'):
        parser.error('This smoke fixture currently covers Windows and Linux x86_64.')
    host = Path(__file__).resolve().parent
    platform, library = ('Windows Desktop', 'weva_godot.dll') if sys.platform == 'win32' else ('Linux', 'libweva_godot.so')
    binary = args.library or host / 'project/addons/weva/bin' / library
    if not args.addon and not binary.is_file():
        parser.error(f'Build the host first: {binary}')
    # This directory is created and owned by this invocation alone.
    directory = nullcontext(tempfile.mkdtemp(prefix='weva-export-')) if args.keep else tempfile.TemporaryDirectory(prefix='weva-export-')
    with directory as temporary:
        work = Path(temporary).resolve()
        if args.keep:
            print('Fixture:', work, flush=True)
        project, exported = work / 'project', work / 'exported'
        addon = project / 'addons/weva'
        project.mkdir()
        (project / 'art').mkdir()
        exported.mkdir()
        if custom_templates:
            (work / 'custom-templates.json').write_text(json.dumps({mode: {
                'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()
            } for mode, path in custom_templates.items()}, indent=2), encoding='utf-8')
        if args.addon:
            with zipfile.ZipFile(args.addon) as archive:
                for entry in archive.infolist():
                    # A package can only install regular files inside its addon.
                    name = entry.filename
                    if (not name.startswith('addons/weva/') or '\\' in name or ':' in name or
                            '..' in Path(name).parts or (entry.external_attr >> 16) & 0o170000 == 0o120000):
                        raise RuntimeError(f'Unsafe addon entry: {name}')
                    archive.extract(entry, project)
            binary = addon / 'bin' / library
            if not binary.is_file():
                raise RuntimeError(f'Addon has no {platform} library: {binary}')
            manifest = json.loads((addon / 'build.json').read_text(encoding='utf-8'))
            key = 'windows' if sys.platform == 'win32' else 'linux'
            selected = manifest.get('libraries', {}).get(key, {})
            actual_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
            if (manifest.get('schema') != 1 or not manifest.get('version') or
                    selected.get('path') != 'bin/' + library or selected.get('sha256') != actual_hash or
                    selected.get('build', {}).get('library_sha256') != actual_hash):
                raise RuntimeError('Packaged library does not match its versioned build manifest')
            # The headless display driver keeps its root window at 64x64.
            # A SubViewport gives the unmodified example a real UI-sized canvas.
            (project / 'example_test.tscn').write_text('[gd_scene load_steps=2 format=3]\n'
                '[ext_resource type="PackedScene" path="res://addons/weva/example/example.tscn" id="1"]\n'
                '[node name="Test" type="Node"]\n[node name="Viewport" type="SubViewport" parent="."]\n'
                'size = Vector2i(640, 720)\n[node name="Example" parent="Viewport" instance=ExtResource("1")]\n')
        else:
            (addon / 'bin').mkdir(parents=True)
            shutil.copy2(binary, addon / 'bin' / library)
            shutil.copy2(host / 'project/addons/weva/weva.gdextension', addon)
        shutil.copy2(host / 'export_smoke.gd', project / 'main.gd')
        (project / 'art/pixel.png').write_bytes(png())
        (project / 'art/vector.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" width="9" height="7">'
            '<rect width="9" height="7" fill="#20c080"/></svg>')
        (project / 'project.godot').write_text('config_version=5\n[application]\nconfig/name="Weva export smoke"\n'
            'run/main_scene="res://main.tscn"\n[display]\nwindow/size/viewport_width=640\nwindow/size/viewport_height=720\n'
            '[rendering]\nrenderer/rendering_method="gl_compatibility"\n')
        (project / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n'
            '[ext_resource type="Script" path="res://main.gd" id="1"]\n[node name="Smoke" type="Node"]\nscript = ExtResource("1")\n')
        (project / 'ui.html').write_text('<img id="icon" src="art/pixel.png"><img id="vector" src="art/vector.svg">'
            '<div id="panel"></div>')
        (project / 'ui.css').write_text('html,body{margin:0}#panel{width:40px;height:30px;background-image:url(art/pixel.png)}')
        (project / 'export_presets.cfg').write_text(f'[preset.0]\nname="Smoke"\nplatform="{platform}"\n'
            'runnable=true\nexport_filter="all_resources"\ninclude_filter="*.html,*.css"\nexclude_filter=""\n'
            '[preset.0.options]\nbinary_format/architecture="x86_64"\n' +
            ''.join(f'custom_template/{mode}={json.dumps(path.as_posix(), ensure_ascii=False)}\n'
                    for mode, path in custom_templates.items()), encoding='utf-8')
        # Immediate --import/--quit can crash Godot's extension documentation
        # generation on a fresh cache (godotengine/godot#111645). Let the
        # editor initialize normally; the following run proves import finished.
        run([args.godot, '--headless', '--path', str(project), '--editor', '--quit-after', '60'], work)
        run([args.godot, '--headless', '--path', str(project), '--quit-after', '120'], work,
            'godot export smoke (project):')
        if args.addon:
            run([args.godot, '--headless', '--path', str(project), '--scene',
                 'res://example_test.tscn', '--quit-after', '120', '--',
                 '--weva-example-test'], work, 'weva addon example: 17 checks, 0 failures')
        if args.render:
            capture_example([args.godot, '--path', str(project)], work, 'project')
        pack = exported / 'smoke.pck'
        run([args.godot, '--headless', '--path', str(project), '--export-pack', 'Smoke', str(pack)], work)
        # --export-pack exports resources. Native libraries remain real files,
        # as they do next to a game executable in a full platform export.
        native = exported / 'addons/weva/bin'
        native.mkdir(parents=True)
        shutil.copy2(binary, native / library)
        run([args.godot, '--headless', '--path', str(exported), '--main-pack', str(pack),
             '--quit-after', '120', '--', '--packed'], exported, 'godot export smoke (pack):')
        if args.addon:
            run([args.godot, '--headless', '--path', str(exported), '--main-pack', str(pack),
                 '--scene', 'res://example_test.tscn', '--quit-after', '120', '--',
                 '--weva-example-test'], exported, 'weva addon example: 17 checks, 0 failures')
        if args.native:
            native_exports(args, work, project, binary, library)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
