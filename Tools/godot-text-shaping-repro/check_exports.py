"""Verify Unicode safety in debug and release exports with embedded ICU data."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

from check import CASES, as_text, check_case, fingerprint


def node_probe(text):
    """Keep the same assertions, adapting only the standalone entry point."""
    if text.count('extends SceneTree') != 1 or text.count('func _initialize() -> void:') != 1:
        raise ValueError('Standalone probe entry point changed; review its exported wrapper')
    return text.replace('extends SceneTree', 'extends Node').replace(
        'func _initialize() -> void:',
        'func _ready() -> void:\n\tprint("Template debug: ", OS.is_debug_build())').replace(
        'quit(', 'get_tree().quit(')


def editor_step(editor, project, out, label, options, env):
    command = [str(editor), '--headless', '--path', str(project),
               '--log-file', str(out / (label + '.engine.log')), *map(str, options)]
    code = None
    try:
        result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=120, env=env)
        code, log = result.returncode, result.stdout + result.stderr
    except subprocess.TimeoutExpired as error:
        log = as_text(error.stdout) + as_text(error.stderr) + '\nExport step timed out\n'
    except OSError as error:
        log = str(error)
    (out / (label + '.log')).write_text(log, encoding='utf-8')
    (out / (label + '.process.json')).write_text(
        json.dumps({'command': command, 'returncode': code}, indent=2), encoding='utf-8')
    if code != 0 or re.search(r'ERROR:|SCRIPT ERROR:|^FAIL\b', log, re.M):
        raise RuntimeError(f'{label} failed; see {out}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True, help='Editor executable')
    parser.add_argument('--debug-template', type=Path)
    parser.add_argument('--release-template', type=Path)
    parser.add_argument('--logs', type=Path, required=True, help='New artifact directory')
    args = parser.parse_args()
    if sys.platform not in ('win32', 'linux'):
        parser.error('This export gate covers Windows and Linux x86_64')
    editor = Path(shutil.which(args.godot) or args.godot).resolve()
    if not editor.is_file():
        parser.error(f'Editor not found: {editor}')
    if bool(args.debug_template) != bool(args.release_template):
        parser.error('Specify both custom templates or neither')
    templates = {}
    for mode, path in [('debug', args.debug_template), ('release', args.release_template)]:
        if path:
            path = path.resolve()
            if not path.is_file():
                parser.error(f'{mode} template not found: {path}')
            templates[mode] = path
    out = args.logs.resolve()
    out.mkdir(parents=True, exist_ok=False)
    source = Path(__file__).with_name('probe.gd')
    report = {'passed': False, 'editor': fingerprint(editor), 'probe': fingerprint(source),
              'templates': {mode: fingerprint(path) for mode, path in templates.items()}, 'exports': []}

    def save():
        (out / 'result.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

    save()
    # The runtime must obtain ICU from its pack, never the caller's filesystem.
    env = dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1')
    env.pop('GODOT_TEXT_SUPPORT_DATA', None)
    platform = 'Windows Desktop' if sys.platform == 'win32' else 'Linux'
    for mode in ['debug', 'release']:
        directory = out / mode
        project, native = directory / 'project', directory / 'native'
        project.mkdir(parents=True)
        native.mkdir()
        (project / 'probe.gd').write_text(node_probe(source.read_text(encoding='utf-8')), encoding='utf-8')
        (project / 'main.tscn').write_text('[gd_scene load_steps=2 format=3]\n'
            '[ext_resource type="Script" path="res://probe.gd" id="1"]\n'
            '[node name="Probe" type="Node"]\nscript = ExtResource("1")\n', encoding='utf-8')
        (project / 'project.godot').write_text('config_version=5\n[application]\n'
            'config/name="Weva exported text safety"\nrun/main_scene="res://main.tscn"\n'
            '[internationalization]\nlocale/include_text_server_data=true\n'
            '[rendering]\nrenderer/rendering_method="gl_compatibility"\n', encoding='utf-8')
        (project / 'export_presets.cfg').write_text(f'[preset.0]\nname="TextSafety"\nplatform="{platform}"\n'
            'runnable=true\nexport_filter="all_resources"\ninclude_filter=""\nexclude_filter=""\n'
            '[preset.0.options]\nbinary_format/architecture="x86_64"\n' +
            ''.join(f'custom_template/{key}={json.dumps(path.as_posix(), ensure_ascii=False)}\n'
                    for key, path in templates.items()), encoding='utf-8')
        executable = native / ('TextSafety.exe' if sys.platform == 'win32' else 'TextSafety.x86_64')
        editor_step(editor, project, directory, 'import', ['--import'], env)
        editor_step(editor, project, directory, 'export', ['--export-' + mode, 'TextSafety', executable], env)
        if not executable.is_file() or not executable.with_suffix('.pck').is_file():
            raise RuntimeError(f'{mode}: missing native executable or pack')
        hidden = directory / 'source-unavailable'
        relocated = directory / 'relocated'
        if not all(p.resolve().is_relative_to(out) for p in [project, native, hidden, relocated]):
            raise RuntimeError('Export paths escaped the private fixture')
        project.rename(hidden)
        native.rename(relocated)
        executable = relocated / executable.name
        result = {'mode': mode, 'executable': fingerprint(executable),
                  'pack': fingerprint(executable.with_suffix('.pck')), 'cases': []}
        report['exports'].append(result)
        save()
        for case in CASES:
            checked = check_case(executable, None, directory, case, env, native_mode=mode)
            result['cases'].append(checked)
            print(f'{mode} {case}: {"PASS" if checked["passed"] else "FAIL"}', flush=True)
            save()
    report['passed'] = bool(CASES) and len(report['exports']) == 2 and all(
        [case['case'] for case in export['cases']] == list(CASES) and
        all(case['passed'] for case in export['cases']) for export in report['exports'])
    save()
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
