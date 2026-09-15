"""Shared process and result handling for the Unity oracle tools."""
import os
from pathlib import Path
import subprocess
import uuid
import xml.etree.ElementTree as ET


REPO = Path(__file__).resolve().parents[2]
DEFAULT_UNITY = r'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe'
MANIFEST_TEST = 'Weva.Tests.EditorTests.Native.NativeLayoutDumpTests.Manifest_DumpsEveryCaseForTheOracle'
FREEZE_MOTION = '*,*::before,*::after{animation:none!important;transition:none!important;}'


def frozen_stylesheet(css):
    # Match the browser capture's stable layout snapshot. weva_dump does not run
    # the animation clock; NativeDocument.Update(0) otherwise applies keyframes.
    text = Path(css).read_text(encoding='utf-8') if css else ''
    return text + '\n' + FREEZE_MOTION + '\n'


def hidden_process_options():
    if os.name != 'nt':
        return {}
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    return {'startupinfo': startup}


def preserve_previous(path):
    path = Path(path)
    if path.exists():
        path.rename(path.with_name(path.name + '.previous-' + uuid.uuid4().hex[:12]))


def run_unity_manifest(unity, project, manifest, results, log):
    """Require a fresh passing manifest test, preserving all earlier evidence."""
    for path in (results, log):
        preserve_previous(path)
    for line in Path(manifest).read_text(encoding='utf-8').splitlines():
        if not line.strip():
            continue
        fields = line.split('\t')
        preserve_previous(fields[4])
        if len(fields) > 6 and fields[6]:
            preserve_previous(fields[6])
            preserve_previous(Path(fields[6]).with_suffix('.ppm'))
    cmd = [unity, '-batchmode', '-projectPath', str(project), '-runTests',
           '-testPlatform', 'EditMode', '-testFilter', MANIFEST_TEST,
           '-testResults', str(results), '-logFile', str(log)]
    result = subprocess.run(cmd, env=dict(os.environ, WEVA_NATIVE_DUMP_MANIFEST=str(manifest)),
                            **hidden_process_options())
    if result.returncode not in (0, 2):
        raise RuntimeError(f'Unity exited {result.returncode}; see {log}')
    try:
        root = ET.parse(results).getroot()
    except (OSError, ET.ParseError) as error:
        raise RuntimeError(f'Unity produced no valid fresh results; see {log}') from error
    tests = [t for t in root.iter('test-case') if t.get('fullname') == MANIFEST_TEST]
    if (root.get('result') != 'Passed' or root.get('failed') != '0' or
            len(tests) != 1 or tests[0].get('result') != 'Passed'):
        raise RuntimeError(f'Unity manifest test did not pass; see {results}')
