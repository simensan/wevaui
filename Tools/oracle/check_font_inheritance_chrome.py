"""Compare the nested-font regression fixtures with a private headless Chrome.

Usage: python check_font_inheritance_chrome.py --chrome PATH --output DIRECTORY
Use --no-sandbox only when a restricted local test environment requires it.
The browser opens only the generated local fixture, using a temporary profile.
"""
import argparse
import html
import json
from pathlib import Path
import re
import subprocess

SCRIPT = r"""
const rows = [];
function check(label, actual, expected) {
    rows.push({label, actual, expected, pass: Math.abs(actual - expected) < .001});
}
const markup = '<div id=base><div id=a><div id=b><div id=c><div id=sample></div></div></div></div></div>';
function build(css) {
    document.body.innerHTML = '<style>html,body{margin:0;font-size:16px}' +
        '#sample{width:1em;height:1em;background:#09f}' + css + '</style>' + markup;
}
const cases = [
    ['2em','2em','2em',128], ['150%','150%','150%',54],
    ['2em','150%','calc(1em + 4px)',52], ['2em','inherit','inherit',32],
    ['2em','unset','unset',32], ['2em','revert','revert-layer',32],
    ['2em','initial','2em',32], ['2em','var(--size, inherit)','inherit !important',32]
];
for (const [a,b,c,expected] of cases) {
    build(`#a{font-size:${a}}#b{font-size:${b}}#c{font-size:${c}}`);
    check(`${a}/${b}/${c} computed`, parseFloat(getComputedStyle(document.querySelector('#c')).fontSize), expected);
    check(`${a}/${b}/${c} inherited leaf`, document.querySelector('#sample').getBoundingClientRect().width, expected);
}
build('#a{font-size:2em}@layer first,second;@layer first{#b{font-size:150%}}@layer second{#b{font-size:revert-layer}}');
check('revert-layer lower declaration', document.querySelector('#sample').getBoundingClientRect().width, 48);
for (const font of ['', 'inherit', 'unset', '2em', 'initial']) {
    build('#a{font-size:2em}#b{font-size:150%}#b::before{content:"x";' + (font ? `font-size:${font}` : '') + '}');
    check('pseudo ' + font, parseFloat(getComputedStyle(document.querySelector('#b'), '::before').fontSize),
        font === '2em' ? 96 : font === 'initial' ? 16 : 48);
}
for (const display of ['block', 'contents']) {
    build(`#a{font-size:2em;display:${display}}#b{font-size:2em}` +
        '#b.inherit{font-size:inherit!important}#b.unset{font-size:unset}#b.initial{font-size:initial}' +
        '#b.empty{font-size:var(--missing)}#b.relative{font-size:150%}');
    for (const base of [16,20,12,16]) {
        for (const mode of ['inherit','','unset','initial','empty','relative','']) {
            document.querySelector('#base').style.fontSize = base + 'px';
            document.querySelector('#b').className = mode;
            const expected = mode === 'initial' ? 16 : mode === 'relative' ? base * 3 : mode ? base * 2 : base * 4;
            const rect = document.querySelector('#sample').getBoundingClientRect();
            check(`${display}/${base}/${mode} width`, rect.width, expected);
            check(`${display}/${base}/${mode} height`, rect.height, expected);
        }
    }
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
    command = ['node', str(Path(__file__).with_name('chrome_test_browser.cjs')),
               args.chrome, fixture.as_uri()]
    if args.no_sandbox:
        command.append('--no-sandbox')
    result = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=45)
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
    print(f"Chrome font inheritance: {len(data['rows'])} checks, {len(failed)} failures")
    return bool(failed)


if __name__ == '__main__':
    raise SystemExit(main())
