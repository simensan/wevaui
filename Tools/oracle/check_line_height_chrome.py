"""Compare the line-height inheritance regression fixtures with a private headless Chrome.

Usage: python check_line_height_chrome.py --chrome PATH --output DIRECTORY
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
    rows.push({label, actual, expected, pass: typeof expected === 'number' ?
        Math.abs(actual - expected) < .001 : actual === expected});
}
function build(css, display = 'block') {
    document.body.innerHTML = '<style>html,body{margin:0;font-size:16px}' +
        '#p{font-size:20px}#c{font-size:10px;display:' + display + '}#g{font-size:30px}' +
        css + '</style><div id=p><div id=c><div id=g>x</div></div></div>';
}
const value = id => parseFloat(getComputedStyle(document.querySelector('#' + id)).lineHeight);
const cases = [
    ['150%',30,30,30], ['2em',40,40,40], ['32px',32,32,32],
    ['calc(1em + 50% + 2px)',32,32,32], ['2rem',32,32,32],
    ['1.5',30,15,45], ['calc(1 + 0.5)',30,15,45],
    ['min(1.5,2)',30,15,45], ['clamp(1.25,1.5,2)',30,15,45],
    ['calc(1em - 30px)',0,0,0], ['calc(-1)',0,0,0], ['0',0,0,0]
];
for (const display of ['block','contents'])
for (const [raw,p,c,g] of cases)
for (const inheritance of ['', 'inherit', 'unset', 'revert', 'revert-layer', 'var(--missing)']) {
    build(`#p{line-height:${raw}}#c{line-height:${inheritance}}`, display);
    for (const [id,expected] of [['p',p],['c',c],['g',g]])
        check(`${display}/${raw}/${inheritance}/${id}`, value(id), expected);
}
for (const raw of ['150%','2em','1.5','calc(1 + 0.5)']) {
    build(`#p{line-height:${raw}}#c.own{line-height:${raw}}#c.inherit{line-height:inherit!important}`);
    for (const fs of [20,24,12,20]) {
        document.querySelector('#p').style.fontSize = fs + 'px';
        for (const mode of ['', 'own', 'inherit', '']) {
            document.querySelector('#c').className = mode;
            const unitless = raw === '1.5' || raw === 'calc(1 + 0.5)';
            const ratio = raw === '2em' ? 2 : 1.5;
            check(`mutate/${raw}/${fs}/${mode}`, value('c'), (unitless || mode === 'own' ? 10 : fs) * ratio);
        }
    }
}
for (const raw of ['', 'inherit', 'unset', '2em', '1.5', 'calc(1 + 0.5)', 'initial']) {
    build('#p{line-height:150%}#c::before{content:"x";font-size:5px;' +
        (raw ? `line-height:${raw}` : '') + '}');
    const actual = getComputedStyle(document.querySelector('#c'),'::before').lineHeight;
    check('pseudo/' + raw, raw === 'initial' ? actual : parseFloat(actual),
        raw === 'initial' ? 'normal' : raw === '2em' ? 10 :
        raw === '1.5' || raw === 'calc(1 + 0.5)' ? 7.5 : 30);
}
build('#p{line-height:150%}@layer first,second;@layer first{#c{line-height:2em}}' +
      '@layer second{#c{line-height:revert-layer}}');
check('rollback retains own length basis',value('c'),20);
build('#p{line-height:150%}#c::before{content:"x";font-size:5px}@layer first,second;' +
      '@layer first{#c::before{line-height:2em}}@layer second{#c::before{line-height:revert-layer}}');
check('pseudo rollback retains own length basis',parseFloat(getComputedStyle(document.querySelector('#c'),'::before').lineHeight),10);
build('#p{line-height:150%}#c{font:10px/2em monospace}');
check('font shorthand owns line height',value('c'),20);
for (const keyword of ['normal','initial']) {
    build(`#p{line-height:150%}#c{line-height:${keyword}}`);
    check('normal remains a keyword/' + keyword,getComputedStyle(document.querySelector('#g')).lineHeight,'normal');
}
document.body.textContent = JSON.stringify({browser:navigator.userAgent,rows});
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
    print(f"Chrome line height: {len(data['rows'])} checks, {len(failed)} failures")
    return bool(failed)


if __name__ == '__main__':
    raise SystemExit(main())
