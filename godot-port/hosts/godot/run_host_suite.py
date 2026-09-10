#!/usr/bin/env python3
"""Run the native host test suite headless and write a receipt.

The suite is the editor import plus every *_tests/*_smoke/demo scene in
hosts/godot/project, and the western_survival smoke script. Pass a project
whose addons/weva/bin holds the library under test. The gallery scenes resolve
the oracle sample corpus through ``../../../tools/oracle/corpus/samples``
relative to the project, so a copy must keep that layout (a ``tools`` junction
or symlink beside a ``hosts/godot/project`` copy is enough).

    python hosts/godot/run_host_suite.py --godot /path/to/godot \
        --project hosts/godot/project --out /new/output/folder
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

SCENES = [
    'form_baseline_tests', 'border_tests', 'item_context_tests', 'line_height_tests', 'font_inheritance_tests',
    'theme_font_tests', 'font_size_tests', 'input_integration_tests', 'keyboard_integration_tests',
    'ime_integration_tests', 'text_editing_tests', 'text_autoscroll_tests', 'maxlength_tests', 'form_state_tests',
    'select_control_tests', 'select_autoscroll_tests', 'typeahead_tests', 'test_scene', 'binding_tests',
    'hover_tests', 'gallery_hover_test', 'gallery_clock_tests', 'demo_smoke', 'inventory_demo',
    'intrinsic_size_tests', 'dialog_cancel_tests', 'form_validation_tests', 'timing_tests',
    'validity_snapshot_tests', 'explicit_validity_tests', 'range_selector_tests', 'validity_selector_tests',
    'multicol_sizing_tests', 'block_margin_tests', 'input_geometry_tests', 'range_direction_tests',
    'number_step_tests', 'popover_beforetoggle_tests', 'unknown_at_rule_tests', 'css_diagnostic_tests',
    'conditional_keyframe_tests', 'font_warmup_tests', 'hidden_transition_tests', 'long_effect_list_tests',
    'delayed_transition_tests', 'transition_cancellation_tests', 'transition_reversal_tests',
    'font_face_tests', 'gamepad_navigation_tests', 'live_reload_tests',
]
SCRIPTS = {'survival_smoke': 'res://samples/western_survival/survival_smoke.gd'}
SUMMARY = re.compile(r'(\d+) checks, (\d+) failures')
ERRORS = re.compile(r'SCRIPT ERROR|FAIL  |^FAIL |ERROR:', re.M)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path, help='New directory for logs and receipt.json')
    parser.add_argument('--import-frames', type=int, default=60)
    parser.add_argument('--timeout', type=int, default=300, help='Seconds per scene')
    args = parser.parse_args()
    project = args.project.resolve()
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    env = {k: v for k, v in os.environ.items() if not k.startswith('WEVA_')}
    env['GODOT_SILENCE_ROOT_WARNING'] = '1'
    results = []

    def run(name, extra, timeout):
        started = time.time()
        command = [args.godot, '--headless', '--path', str(project)] + extra
        try:
            p = subprocess.run(command, env=env, capture_output=True, text=True, encoding='utf-8',
                               errors='replace', timeout=timeout)
            log, code = p.stdout + p.stderr, p.returncode
        except subprocess.TimeoutExpired as e:
            log, code = (e.stdout or '') + (e.stderr or '') + '\nTIMEOUT', 'timeout'
        (out / f'{name}.log').write_text(log, encoding='utf-8')
        checks = sum(int(m.group(1)) for m in SUMMARY.finditer(log))
        failures = sum(int(m.group(2)) for m in SUMMARY.finditer(log))
        passed = code == 0 and failures == 0 and not ERRORS.search(log) and (checks > 0 or name == 'import')
        results.append({'name': name, 'passed': passed, 'checks': checks, 'failures': failures, 'exit': code,
                        'seconds': round(time.time() - started, 1), 'command': command})
        print(f"{name:32} {'PASS' if passed else 'FAIL'} checks={checks} failures={failures} exit={code}", flush=True)

    run('import', ['--editor', '--quit-after', str(args.import_frames)], max(args.timeout, 600))
    for name in SCENES:
        if (project / f'{name}.tscn').is_file():
            run(name, [f'{name}.tscn'], args.timeout)
        else:
            results.append({'name': name, 'passed': False, 'checks': 0, 'failures': 0, 'exit': 'missing scene'})
            print(f'{name:32} FAIL missing scene', flush=True)
    for name, script in SCRIPTS.items():
        run(name, ['--script', script], args.timeout)
    library_name = {'win32': 'weva_godot.dll', 'darwin': 'libweva_godot.dylib'}.get(sys.platform, 'libweva_godot.so')
    library = project / 'addons/weva/bin' / library_name
    if not library.is_file():
        library = None
    receipt = {'passed': all(r['passed'] for r in results), 'entries': len(results),
               'checks': sum(r['checks'] for r in results), 'godot': args.godot, 'project': str(project),
               'binary_sha256': hashlib.sha256(library.read_bytes()).hexdigest() if library else None,
               'tests': results}
    (out / 'receipt.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(f"host suite: {receipt['entries']} entries, {receipt['checks']} checks, "
          f"{'all passed' if receipt['passed'] else 'FAILED'}; receipt {out / 'receipt.json'}")
    return 0 if receipt['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
