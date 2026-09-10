"""Run each TextServer reproduction in isolation; broken engines can abort."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time


CASES = ('emoji32', 'emoji33', 'emoji65', 'emoji256',
         'multiple_scripts', 'multiple_growing_scripts')


def fingerprint(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return {'path': str(path), 'sha256': digest.hexdigest()}


def as_text(value):
    if isinstance(value, bytes):
        return value.decode('utf-8', errors='replace')
    return value or ''


def check_case(godot, project, logs, case, env, *, native_mode=None):
    command = [str(godot), '--headless']
    if native_mode is None:
        command += ['--path', str(project)]
    command += ['--log-file', str(logs / f'{case}.engine.log')]
    if native_mode is None:
        command += ['--script', str(project / 'probe.gd')]
    command += ['--', case]
    started = time.monotonic()
    code = None
    reasons = []
    try:
        run = subprocess.run(command, capture_output=True, text=True,
                             encoding='utf-8', errors='replace', timeout=30, env=env,
                             cwd=godot.parent if native_mode is not None else None)
        code = run.returncode
        log = run.stdout + run.stderr
        if code != 0:
            reasons.append(f'exit code {code}')
        # Require this case's result, not an incidental success from another probe.
        if not re.search(rf'^{re.escape(case)}: [^\r\n]*; 3 checks, 0 failures\s*$', log, re.M):
            reasons.append('missing successful case summary')
        if re.search(r'ERROR:|SCRIPT ERROR:|^FAIL\b', log, re.M):
            reasons.append('engine or assertion error')
        if native_mode is not None:
            expected = str(native_mode == 'debug').lower()
            if not re.search(rf'^Template debug: {expected}\s*$', log, re.M):
                reasons.append('wrong or missing template build mode')
    except subprocess.TimeoutExpired as error:
        log = as_text(error.stdout) + as_text(error.stderr)
        reasons.append('timeout after 30 seconds')
    except OSError as error:
        log = str(error) + '\n'
        reasons.append('could not launch engine')
    # Preserve unfiltered engine output. Errors remain failures, even when the
    # assertions pass; environment failures must not become release evidence.
    (logs / f'{case}.log').write_text(log, encoding='utf-8')
    return {'case': case, 'passed': not reasons, 'returncode': code,
            'seconds': time.monotonic() - started, 'reasons': reasons,
            'command': command, 'log': str(logs / f'{case}.log')}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--logs', type=Path, required=True,
                        help='New artifact directory; existing results are never overwritten')
    parser.add_argument('--support-data', type=Path,
                        help='Godot source thirdparty/icu4c/icudt_godot.dat (needed by runtime templates)')
    args = parser.parse_args()
    executable = shutil.which(args.godot)
    if not executable:
        parser.error(f'Godot executable not found: {args.godot}')
    godot = Path(executable).resolve()
    source = Path(__file__).resolve().parent
    logs = args.logs.resolve()
    support_data = args.support_data or os.environ.get('GODOT_TEXT_SUPPORT_DATA')
    if support_data:
        support_data = Path(support_data).resolve()
        if not support_data.is_file():
            parser.error(f'ICU support data not found: {support_data}')
    try:
        logs.mkdir(parents=True, exist_ok=False)
    except FileExistsError:
        parser.error(f'Artifact directory already exists: {logs}')
    project = logs / 'project'
    project.mkdir()
    for name in ('project.godot', 'probe.gd'):
        shutil.copy2(source / name, project / name)
    result = {'started_utc': datetime.now(timezone.utc).isoformat(),
              'engine': fingerprint(godot),
              'probe': fingerprint(project / 'probe.gd'),
              'support_data': fingerprint(support_data) if support_data else None,
              'cases': [], 'passed': False}
    env = dict(os.environ, GODOT_SILENCE_ROOT_WARNING='1')
    if support_data:
        env['GODOT_TEXT_SUPPORT_DATA'] = str(support_data)
    for case in CASES:
        checked = check_case(godot, project, logs, case, env)
        result['cases'].append(checked)
        result['passed'] = len(result['cases']) == len(CASES) and all(
            entry['passed'] for entry in result['cases'])
        (logs / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        detail = ': ' + ', '.join(checked['reasons']) if checked['reasons'] else ''
        print(f'{"PASS" if checked["passed"] else "FAIL"} {case}{detail}', flush=True)
    failed = sum(not entry['passed'] for entry in result['cases'])
    print(f'Godot text shaping: {len(CASES)} isolated cases, {failed} failures')
    print(f'Artifacts: {logs}')
    return int(not result['passed'])


if __name__ == '__main__':
    sys.exit(main())
