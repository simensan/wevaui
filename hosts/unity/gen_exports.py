#!/usr/bin/env python3
"""List libweva's C API from weva_c.h and emit linker export files for the Unity plugin.

The header is the single source of truth: every `weva_*` function declared at
file scope is exported, nothing else. A host-only header (weva_unity.h) may
follow the ABI header; its `weva_*` functions are exported the same way. Formats:

  def      MSVC module definition (LIBRARY/EXPORTS)
  version  GNU ld version script (global: the API; local: everything else)
  json     the names, for the P/Invoke generator and for tests

    python gen_exports.py --header ../../libweva/include/weva_c.h --format def --out weva_core.def
"""
import argparse
import json
import re
from pathlib import Path

DECL = re.compile(r'^\s*(?:[A-Za-z_][\w\s\*]*?)\b(weva_[A-Za-z0-9_]+)\s*\(', re.M)


def api_functions(headers):
    text = '\n'.join(Path(h).read_text(encoding='utf-8') for h in headers)
    # Strip comments so documentation mentioning other functions is not exported.
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    text = re.sub(r'//[^\n]*', '', text)
    names = []
    for match in DECL.finditer(text):
        name = match.group(1)
        line_start = text.rfind('\n', 0, match.start()) + 1
        line = text[line_start:match.start()]
        # A declaration, not a typedef of a callback pointer or a macro use.
        if 'typedef' in line or '(*' in text[match.start():match.end() + 2]:
            continue
        if name not in names:
            names.append(name)
    if not names:
        raise SystemExit('no weva_ functions found in ' + ', '.join(map(str, headers)))
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--header', type=Path, required=True, action='append',
                        help='weva_c.h, then any host-only header (repeatable, in order)')
    parser.add_argument('--format', choices=['def', 'version', 'json'], required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--library', default='weva_core', help='LIBRARY name for the .def format')
    args = parser.parse_args()
    names = api_functions(args.header)
    if args.format == 'def':
        body = 'LIBRARY ' + args.library + '\nEXPORTS\n' + ''.join('    ' + n + '\n' for n in names)
    elif args.format == 'version':
        body = 'WEVA_C_API {\n  global:\n' + ''.join('    ' + n + ';\n' for n in names) + '  local:\n    *;\n};\n'
    else:
        body = json.dumps({'headers': [h.name for h in args.header], 'functions': names}, indent=2) + '\n'
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(body, encoding='utf-8', newline='\n')
    print(f'{len(names)} functions -> {args.out}')


if __name__ == '__main__':
    main()
