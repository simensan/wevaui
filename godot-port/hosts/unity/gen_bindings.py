#!/usr/bin/env python3
"""Generate the Unity host's C# P/Invoke layer from weva_c.h.

One generated file: `WevaNative.g.cs` holds every enum, struct and function
of libweva's C ABI (weva_c.h) and of the plugin's own header (weva_unity.h)
as blittable C# (unsafe pointers, `nuint` for size_t, `delegate*
unmanaged[Cdecl]` for callback fields), plus the ABI version constants.
Hand-written convenience wrappers live beside it and are not generated.
Regenerate after any header change; CI regenerates and fails on drift so the
two cannot part.

    python gen_bindings.py --header ../../libweva/include/weva_c.h --header src/weva_unity.h \
        --out ../../../Packages/com.wevaui/Runtime/Native/WevaNative.g.cs
"""
import argparse
import re
from pathlib import Path

SCALARS = {
    'void': 'void', 'int': 'int', 'unsigned': 'uint', 'unsigned int': 'uint', 'char': 'byte',
    'int8_t': 'sbyte', 'uint8_t': 'byte', 'int16_t': 'short', 'uint16_t': 'ushort',
    'int32_t': 'int', 'uint32_t': 'uint', 'int64_t': 'long', 'uint64_t': 'ulong',
    'size_t': 'nuint', 'float': 'float', 'double': 'double', 'bool': 'byte',
    'weva_document_t': 'System.IntPtr', 'weva_element_t': 'uint',
}


def strip_comments(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    return re.sub(r'//[^\n]*', '', text)


def map_type(ctype, enums, structs, callbacks):
    t = ' '.join(ctype.replace('*', ' * ').split())
    t = re.sub(r'\bconst\b', '', t).strip()
    stars = t.count('*')
    base = t.replace('*', '').strip()
    if base in callbacks:
        return callbacks[base] + '*' * stars
    if base in enums:
        cs = 'int'  # enums travel as their underlying integer in signatures
    elif base in structs:
        cs = base
    else:
        cs = SCALARS.get(base)
        if cs is None:
            raise SystemExit('unmapped C type: ' + ctype)
    if cs == 'void' and stars:
        return 'void' + '*' * stars
    return cs + '*' * stars


def split_params(params, enums, structs, callbacks):
    params = params.strip()
    if params in ('', 'void'):
        return []
    out = []
    for raw in split_top(params):
        raw = ' '.join(raw.split())
        m = re.fullmatch(r'(.+?)\s*\(\s*\*\s*(\w+)\s*\)\s*\((.*)\)', raw)
        if m:  # inline function pointer parameter
            ret, name, inner = m.groups()
            out.append((fnptr(ret, inner, enums, structs, callbacks), name))
            continue
        m = re.fullmatch(r'(.+?)\s*(\w+)\s*(\[[^\]]*\])?', raw)
        if not m:
            raise SystemExit('unparsed parameter: ' + raw)
        ctype, name, array = m.groups()
        cs = map_type(ctype, enums, structs, callbacks)
        if array:
            cs += '*'
        out.append((cs, RESERVED.get(name, name)))
    return out


def split_top(text):
    parts, depth, current = [], 0, ''
    for ch in text:
        if ch == '(':
            depth += 1
        elif ch == ')':
            depth -= 1
        if ch == ',' and depth == 0:
            parts.append(current)
            current = ''
        else:
            current += ch
    if current.strip():
        parts.append(current)
    return parts


def fnptr(ret, params, enums, structs, callbacks):
    args = [cs for cs, _ in split_params(params, enums, structs, callbacks)]
    return 'delegate* unmanaged[Cdecl]<' + ', '.join(args + [map_type(ret, enums, structs, callbacks)]) + '>'


RESERVED = {'out': '@out', 'in': '@in', 'ref': '@ref', 'params': '@params', 'string': '@string', 'event': '@event', 'object': '@object'}


def parse(text):
    enums, structs, callbacks, functions, defines = {}, {}, {}, [], {}
    for m in re.finditer(r'typedef enum (\w+)\s*\{(.*?)\}\s*(\w+);', text, re.S):
        name, body = m.group(1), m.group(2)
        entries, value = [], -1
        for item in split_top(body):
            item = item.strip()
            if not item:
                continue
            if '=' in item:
                key, expr = [s.strip() for s in item.split('=', 1)]
                expr_cs = expr.replace('u <<', ' <<').replace('u<<', '<<')
                value = eval(expr.replace('u', ''))  # header values are small literals
                entries.append((key, expr_cs))
            else:
                value += 1
                entries.append((item, str(value)))
        enums[name] = entries
    for m in re.finditer(r'typedef struct (\w+)\s*\{(.*?)\}\s*(\w+);', text, re.S):
        structs[m.group(1)] = m.group(2)
    # Callback typedefs: `typedef ret (*name)(params);` with the return type on
    # the typedef's own line, so a struct's function-pointer fields never match.
    for m in re.finditer(r'\n[ \t]*typedef[ \t]+([\w \t\*]+?)[ \t]*\(\s*\*\s*(\w+)\s*\)\s*\(([^;]*?)\);', text, re.S):
        callbacks[m.group(2)] = (m.group(1), m.group(3))
    for m in re.finditer(r'#define\s+(WEVA_\w+)\s+(.+)', text):
        defines[m.group(1)] = m.group(2).strip()
    # A declaration starts a line with its return type (one line), then the
    # name and a parameter list that may wrap but holds no ';'.
    for m in re.finditer(r'\n[ \t]*((?:const[ \t]+)?[A-Za-z_][\w \t\*]*?)[ \t]*\b(weva_\w+)[ \t]*\(([^;]*?)\)[ \t]*;', text, re.S):
        ret, name, params = m.groups()
        if 'typedef' in ret or name in callbacks:
            continue
        functions.append((ret.strip(), name, params))
    return enums, structs, callbacks, functions, defines


def struct_fields(body, enums, structs, callbacks):
    fields = []
    for decl in body.split(';'):
        decl = ' '.join(decl.split())
        if not decl:
            continue
        m = re.fullmatch(r'(.+?)\s*\(\s*\*\s*(\w+)\s*\)\s*\((.*)\)', decl)
        if m:
            ret, name, params = m.groups()
            fields.append(('public ' + fnptr(ret, params, enums, structs, callbacks) + ' ' + name + ';', None))
            continue
        # The declarator list is matched strictly (names, arrays, leading
        # stars), so the type cannot stop short of a word.
        m = re.fullmatch(r'(.+?)\s*((?:\*\s*)?\w+(?:\[\d+\])*(?:\s*,\s*(?:\*\s*)?\w+(?:\[\d+\])*)*)', decl)
        if not m:
            raise SystemExit('unparsed field: ' + decl)
        ctype, names = m.groups()
        for item in names.split(','):
            item = item.strip()
            am = re.fullmatch(r'(\w+)((?:\[\d+\])+)', item)
            if am:
                name, dims = am.groups()
                count = 1
                for d in re.findall(r'\d+', dims):
                    count *= int(d)
                cs = map_type(ctype, enums, structs, callbacks)
                fields.append((f'public fixed {cs} {name}[{count}];', f'{ctype} {item}'))
            else:
                stars = item.count('*')
                name = item.replace('*', '').strip()
                cs = map_type(ctype + '*' * stars, enums, structs, callbacks)
                fields.append((f'public {cs} {RESERVED.get(name, name)};', None))
    return fields


def generate(headers):
    text = strip_comments('\n'.join(Path(h).read_text(encoding='utf-8') for h in headers))
    enums, structs, callbacks, functions, defines = parse(text)
    cb_types = {name: fnptr(ret, params, enums, structs, callbacks) for name, (ret, params) in callbacks.items()}
    out = ['// <auto-generated> by godot-port/hosts/unity/gen_bindings.py from ' + ', '.join(Path(h).name for h in headers) + '. Do not edit; regenerate.',
           '// Blittable P/Invoke surface of libweva\'s C ABI for the Unity host (weva_core plugin).',
           '#nullable disable', 'using System;', 'using System.Runtime.InteropServices;', '',
           'namespace Weva.Native', '{']
    # Enums and structs sit at namespace level so callers name them as the
    # header does (weva_status, weva_draw); the static class holds the externs.
    for name, entries in enums.items():
        flags = any('<<' in v for _, v in entries)
        out.append('    ' + ('[Flags] ' if flags else '') + f'public enum {name} : ' + ('uint' if flags else 'int'))
        out.append('    {')
        for key, value in entries:
            out.append(f'        {key} = {value},')
        out.append('    }')
        out.append('')
    for name, body in structs.items():
        out.append('    [StructLayout(LayoutKind.Sequential)]')
        out.append(f'    public unsafe struct {name}')
        out.append('    {')
        for line, note in struct_fields(body, enums, structs, cb_types):
            out.append('        ' + line + (f' // {note}' if note else ''))
        out.append('    }')
        out.append('')
    out.extend(['    public static unsafe partial class WevaNative', '    {',
                '        public const string Library = "weva_core";'])
    for name, value in defines.items():
        if 'VERSION' in name:
            out.append(f'        public const int {name} = {value};')
        elif name == 'WEVA_ELEMENT_NONE':
            out.append(f'        public const uint {name} = 0xFFFFFFFFu;')
    out.append('')
    for ret, name, params in functions:
        args = ', '.join(f'{cs} {n}' for cs, n in split_params(params, enums, structs, cb_types))
        out.append('        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]')
        out.append(f'        public static extern {map_type(ret, enums, structs, cb_types)} {name}({args});')
    out.extend(['    }', '}', ''])
    return '\n'.join(out), len(functions), len(structs), len(enums)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--header', type=Path, required=True, action='append',
                        help='weva_c.h, then any host-only header (repeatable, in order)')
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--check', action='store_true', help='Fail if the output differs from the file on disk')
    args = parser.parse_args()
    text, nf, ns, ne = generate(args.header)
    if args.check:
        current = args.out.read_text(encoding='utf-8') if args.out.exists() else ''
        if current != text:
            raise SystemExit(f'{args.out} is out of date; regenerate with gen_bindings.py')
        print(f'{args.out} is current ({nf} functions, {ns} structs, {ne} enums)')
        return
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(text, encoding='utf-8', newline='\n')
    print(f'{nf} functions, {ns} structs, {ne} enums -> {args.out}')


if __name__ == '__main__':
    main()
