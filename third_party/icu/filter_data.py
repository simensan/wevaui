#!/usr/bin/env python3
"""Embed the pinned ICU root collation/character-boundary resources, without I/O.

ICU's CmnD format has a sorted table of names and relative data offsets. Keep
resource bytes unchanged, rebuilding only that table and its 16-byte padding.
This is a data packager, not a Unicode algorithm or normalization approximation.
"""
import hashlib
from pathlib import Path
import struct
import sys

SOURCE_SHA256 = 'd5cf2a40dccbe471781ec7af85693bff542ff12f0b670c9630c4e72d60714b8b'
# ulayout.icu carries the Indic syllabic and positional categories
# (weva_char_indic_category); without it every code point is "other".
KEEP = {'root.res', 'en.res', 'en_US.res', 'res_index.res', 'pool.res', 'uemoji.icu', 'ulayout.icu',
        'coll/root.res', 'coll/en.res', 'coll/res_index.res', 'coll/ucadata.icu',
        'brkitr/root.res', 'brkitr/en.res', 'brkitr/en_US.res', 'brkitr/char.brk'}

def package(source):
    if hashlib.sha256(source).hexdigest() != SOURCE_SHA256:
        raise ValueError('ICU 78.3 data SHA-256 mismatch')
    header_size = struct.unpack_from('<H', source)[0]
    if source[2:4] != b'\xda\x27' or source[8] != 0 or source[12:16] != b'CmnD':
        raise ValueError('Expected little-endian ICU common data')
    body = source[header_size:]
    count = struct.unpack_from('<I', body)[0]
    entries = []
    for i in range(count):
        name_offset, data_offset = struct.unpack_from('<II', body, 4 + i * 8)
        end = body.index(b'\0', name_offset)
        entries.append((body[name_offset:end], data_offset))
    offsets = sorted({offset for _, offset in entries} | {len(body)})
    ends = dict(zip(offsets, offsets[1:]))
    picked = [(name, body[offset:ends[offset]]) for name, offset in entries
              if name.decode().removeprefix('icudt78l/') in KEEP]
    found = {name.decode().removeprefix('icudt78l/') for name, _ in picked}
    required = KEEP
    if not required <= found:
        raise ValueError(f'Missing required data: {required - found}')
    result = bytearray(4 + 8 * len(picked))
    struct.pack_into('<I', result, 0, len(picked))
    name_offsets = []
    for name, _ in picked:
        name_offsets.append(len(result))
        result.extend(name + b'\0')
    for i, (_, data) in enumerate(picked):
        result.extend(b'\0' * (-len(result) % 16))
        struct.pack_into('<II', result, 4 + 8 * i, name_offsets[i], len(result))
        result.extend(data)
    return source[:header_size] + result, sorted(found)

def main():
    source, output = map(Path, sys.argv[1:])
    data, names = package(source.read_bytes())
    rows = [','.join(f'0x{v:02x}' for v in data[i:i+24]) for i in range(0,len(data),24)]
    output.write_text('// Generated from Unicode ICU 78.3. See third_party/icu/LICENSE.txt.\n'
                      '#include "unicode/utypes.h"\n'
                      '#if U_IS_BIG_ENDIAN\n#error "The embedded ICU package requires little endian"\n#endif\n'
                      'extern "C" { alignas(16) extern U_EXPORT const unsigned char U_ICUDATA_ENTRY_POINT[] = {\n'
                      + ',\n'.join(rows) + '\n}; }\n', encoding='utf-8')
    print(f'ICU root data: {len(data):,} bytes, {len(names)} resources')

if __name__ == '__main__':
    main()
