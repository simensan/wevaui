"""Compare the flex/grid formatting-context fixtures with a private headless Chrome.

Usage: python check_item_context_chrome.py --chrome PATH --output DIRECTORY
Use --no-sandbox only when a restricted local test environment requires it.
The browser opens only the generated local fixture, using a temporary profile.
"""
import argparse
import html
import json
from pathlib import Path
import re
import subprocess
import tempfile

SCRIPT = r"""

const rows=[];
function check(label,actual,expected){rows.push({label,actual,expected,pass:Math.abs(actual-expected)<.001});}
const box=id=>document.getElementById(id).getBoundingClientRect();
for(const display of ['flex','inline-flex','grid','inline-grid']) {
  for(const fixed of [false,true]) for(const contents of [false,true]) {
    document.body.innerHTML=`<style>html,body{margin:0}#r{display:${display};width:200px;flex-direction:column;grid-template-columns:200px;gap:5px;align-items:start}#a{width:100px;margin:7px 0 11px;height:${fixed?'20px':'auto'}}#child{height:10px;margin:17px 0 13px}#next{height:9px;width:100px}#contents{display:contents}</style><div id=r>${contents?'<div id=contents>':''}<div id=a><div id=child></div></div>${contents?'</div>':''}<div id=next></div></div>`;
    const h=fixed?20:40,label=`${display}/${fixed}/${contents}/`,r=box('r'),a=box('a'),c=box('child'),n=box('next');
    check(label+'item-y',a.y-r.y,7);check(label+'height',a.height,h);check(label+'child-y',c.y-a.y,17);
    check(label+'next-y',n.y-r.y,7+h+11+5);check(label+'parent-height',r.height,7+h+11+5+9);
  }
  document.body.innerHTML=`<style>html,body{margin:0}#r{display:${display};width:200px;flex-direction:column;grid-template-columns:200px;gap:5px;align-items:start}#a{width:100px}#child{float:left;width:30px;height:37px}#next{height:9px;width:100px}</style><div id=r><div id=a><div id=child></div></div><div id=next></div></div>`;
  check(display+'/float-height',box('a').height,37);check(display+'/float-next',box('next').y-box('r').y,42);check(display+'/float-parent',box('r').height,51);
  document.body.innerHTML=`<style>html,body{margin:0}#outer{display:flow-root;width:400px}#float{float:left;width:350px;height:60px}#r{display:${display};width:120px;flex-direction:column;grid-template-columns:120px}#a{width:120px;font-size:16px;line-height:20px}</style><div id=outer><div id=float></div><div id=r><div id=a>aa aa aa aa aa</div></div></div>`;
  check(display+'/outside-float',box('a').height,20);
}
document.body.textContent=JSON.stringify({browser:navigator.userAgent,rows});
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
    with tempfile.TemporaryDirectory(prefix='item-context-', dir=output) as profile:
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
    print(f"Chrome item context: {len(data['rows'])} checks, {len(failed)} failures")
    return bool(failed)


if __name__ == '__main__':
    raise SystemExit(main())
