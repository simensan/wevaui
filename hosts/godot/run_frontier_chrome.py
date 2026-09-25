"""Compare the live Frontier Camp UI with stock Chrome using identical font bytes.

Requires the sample's installed addon, Godot, Node and Tools/Layout's Puppeteer.
Writes an isolated project, raw geometry, screenshots and a failing report when
browser differences remain. Does not alter the sample or its installed binary.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--node', default='node')
    parser.add_argument('--dll', help='Candidate addon binary; copied only into the isolated project')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    host = Path(__file__).resolve().parent
    out = Path(args.out).resolve()
    if out.exists():
        raise SystemExit('Use a new output directory to preserve previous evidence.')
    out.mkdir(parents=True)
    project = out / 'project'
    shutil.copytree(repo / 'examples/frontier_camp', project,
                    ignore=shutil.ignore_patterns('.godot'))
    shutil.copy2(host / 'frontier_chrome.gd', project / 'parity.gd')
    dll = project / 'addons/weva/bin/weva_godot.dll'
    if args.dll:
        shutil.copy2(Path(args.dll).resolve(), dll)
    receipt = {'binary_sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
               'geometry_tolerance': '0.02px + 0.00025 * abs(native coordinate)',
               'normalization': 'same Godot font bytes; no engine UA overlay; native line heights',
               'commands': []}

    def run(label, command):
        result = subprocess.run(command, cwd=repo, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, timeout=120,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        (out / (label + '.log')).write_bytes(result.stdout)
        receipt['commands'].append({'label': label, 'command': command, 'exit_code': result.returncode})
        text = result.stdout.decode('utf-8', errors='replace')
        if result.returncode or 'SCRIPT ERROR:' in text or 'ERROR:' in text:
            (out / 'verification.json').write_text(json.dumps(receipt, indent=2))
            raise RuntimeError(label + ' failed; see preserved log')

    engine = str(Path(args.godot).resolve())
    run('import', [engine, '--headless', '--path', str(project), '--import'])
    run('native', [engine, '--path', str(project), '--rendering-method', 'gl_compatibility',
                   '--script', 'res://parity.gd'])
    run('chrome', [args.node, str(host / 'frontier_chrome.mjs'), str(project)])
    native = json.loads((project / 'native.json').read_text())
    chrome = json.loads((project / 'chrome.json').read_text())
    assert native['failures'] == 0
    assert len(native['states']) == len(chrome['states']) == 11
    receipt['browser'] = chrome['browser']
    receipt['font_sha256'] = hashlib.sha256((project / 'chrome-font.ttf').read_bytes()).hexdigest()
    receipt['states'] = []
    for a, b in zip(native['states'], chrome['states']):
        assert a['name'] == b['name'] and b['fontLoaded']
        differences = []
        for selector, rect in a['rects'].items():
            other = b['rects'].get(selector)
            if other is None:
                differences.append({'selector': selector, 'missing': True})
                continue
            for axis, actual, expected in zip(['x', 'y', 'width', 'height'], rect, other):
                delta = abs(actual - expected)
                if delta > .02 + .00025 * abs(actual):
                    differences.append(dict(selector=selector, axis=axis, native=actual,
                                            chrome=expected, delta=delta))
        behavior = {key: a[key] == b[key] for key in ['focus', 'scroll', 'value']}
        receipt['states'].append({'name': a['name'], 'behavior': behavior, 'differences': differences})
    receipt['behavior_checks'] = sum(len(s['behavior']) for s in receipt['states'])
    receipt['behavior_failures'] = sum(not v for s in receipt['states'] for v in s['behavior'].values())
    receipt['geometry_checks'] = len(native['selectors']) * 4 * len(native['states'])
    receipt['geometry_findings'] = sum(len(s['differences']) for s in receipt['states'])
    receipt['passed'] = not receipt['geometry_findings'] and not receipt['behavior_failures']
    (out / 'verification.json').write_text(json.dumps(receipt, indent=2))
    print(json.dumps({k: v for k, v in receipt.items() if k not in ['states', 'commands']}, indent=2))
    return 0 if receipt['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
