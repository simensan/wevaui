"""Check corner-radius whitespace and computed font units in a private Chrome.

Usage: python check_corner_radius_chrome.py --chrome PATH --output DIRECTORY
The generated local fixture uses a temporary browser profile. --no-sandbox is
optional, for restricted local test environments that require it.
"""
import argparse
import html
import json
from pathlib import Path
import re
import subprocess
import tempfile

SCRIPT = r"""
const rows = [];
const sample = document.createElement('div');
document.body.append(sample);
function check(label, actual, expected) {
    rows.push({label, actual, expected, pass: actual === expected});
}
const properties = ['border-top-left-radius','border-top-right-radius',
                    'border-bottom-right-radius','border-bottom-left-radius'];
for (const prop of properties) {
    for (const separator of [' ', '\t', '\n', '\r\n', '/* axes */', ' \t ']) {
        sample.style.cssText = `width:200px;height:100px;${prop}:12px${separator}8px`;
        check(`${prop}/${JSON.stringify(separator)}`, getComputedStyle(sample).getPropertyValue(prop), '12px 8px');
    }
    sample.style.cssText = `width:200px;height:100px;${prop}:1em 2em`;
    for (const size of [16,20,12,16]) {
        sample.style.fontSize = size + 'px';
        check(`${prop}/font ${size}`, getComputedStyle(sample).getPropertyValue(prop), `${size}px ${size*2}px`);
    }
    sample.style.setProperty(prop, '10%\t20%');
    check(`${prop}/percent pair`, getComputedStyle(sample).getPropertyValue(prop), '10% 20%');
    sample.style.setProperty(prop, '12px\n8px');
    check(`${prop}/replace`, getComputedStyle(sample).getPropertyValue(prop), '12px 8px');
    sample.style.removeProperty(prop);
    check(`${prop}/remove`, getComputedStyle(sample).getPropertyValue(prop), '0px');
}
document.body.textContent = JSON.stringify({browser: navigator.userAgent, rows});
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--chrome', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--no-sandbox', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    fixture = output / 'chrome.html'
    fixture.write_text('<!doctype html><body><script>' + SCRIPT + '</script>', encoding='utf-8')
    with tempfile.TemporaryDirectory(prefix='corner-radius-', dir=output) as profile:
        command = [args.chrome, '--headless', '--disable-gpu', '--no-first-run',
                   '--user-data-dir=' + profile, '--dump-dom', fixture.as_uri()]
        if args.no_sandbox:
            command.insert(1, '--no-sandbox')
        result = subprocess.run(command, capture_output=True, text=True, timeout=45)
        (output / 'chrome-raw.log').write_text(result.stdout + result.stderr, encoding='utf-8')
        match = re.search(r'<body>(.*?)</body>', result.stdout, re.S)
        if result.returncode or not match:
            raise RuntimeError('Chrome did not produce a result; see chrome-raw.log')
        data = json.loads(html.unescape(match.group(1)))
    (output / 'chrome.json').write_text(json.dumps(data, indent=2), encoding='utf-8')
    failed = [row for row in data['rows'] if not row['pass']]
    for row in failed:
        print(row)
    print(data['browser'])
    print(f"Chrome corner radius: {len(data['rows'])} checks, {len(failed)} failures")
    return bool(failed)


if __name__ == '__main__':
    raise SystemExit(main())
