#!/usr/bin/env python3
"""List libweva's C API from weva_c.h and emit linker export files for the Unity plugin.

The header is the single source of truth: every `weva_*` function declared at
file scope is exported, nothing else. Formats:

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


def api_functions(header: Path):
    text = header.read_text(encoding='utf-8')
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
        raise SystemExit('no weva_ functions found in ' + str(header))
    return names


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--header', type=Path, required=True)
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
        body = json.dumps({'header': args.header.name, 'functions': names}, indent=2) + '\n'
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(body, encoding='utf-8', newline='\n')
    print(f'{len(names)} functions -> {args.out}')


if __name__ == '__main__':
    main()
