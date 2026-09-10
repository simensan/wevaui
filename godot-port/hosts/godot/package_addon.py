#!/usr/bin/env python3
"""Create a desktop preview addon ZIP from explicitly chosen Godot API 4.7 builds."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import zipfile

from build_metadata import validate_metadata


def verify_library(path, platform):
    data = path.read_bytes()
    if platform == 'linux':
        valid = (len(data) >= 64 and data[:7] == b'\x7fELF\x02\x01\x01' and
                 struct.unpack_from('<HH', data, 16) == (3, 62))
    else:
        valid = len(data) >= 64 and data[:2] == b'MZ'
        if valid:
            offset = struct.unpack_from('<I', data, 60)[0]
            valid = (offset >= 64 and len(data) >= offset + 26 and
                     data[offset:offset + 6] == b'PE\0\0\x64\x86' and
                     struct.unpack_from('<H', data, offset + 22)[0] & 0x2000 != 0 and
                     struct.unpack_from('<H', data, offset + 24)[0] == 0x20b)
    if not valid:
        raise ValueError(f'{path} is not a {platform} x86_64 library')
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--linux-library', type=Path)
    parser.add_argument('--windows-library', type=Path)
    parser.add_argument('--godot-cpp-dir', type=Path, required=True, help='Source checkout used for the builds (license notice)')
    parser.add_argument('--version', required=True, help='Explicit preview version, e.g. 0.1.0-preview.1')
    parser.add_argument('--output', type=Path, required=True, help='New ZIP path; an existing file is never replaced')
    args = parser.parse_args()
    if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.(0|[1-9]\d*)', args.version):
        parser.error('Use X.Y.Z-preview.N; stable releases remain blocked (see PRODUCT_READINESS.md)')
    libraries = [('linux', args.linux_library, 'libweva_godot.so'), ('windows', args.windows_library, 'weva_godot.dll')]
    if not any(path for _, path, _ in libraries):
        parser.error('Supply at least one platform library')
    host = Path(__file__).resolve().parent
    form_state = (host.parents[1] / 'docs' / 'FORM_STATE.md').read_bytes()
    first_section = form_state.find(b'\n## ')
    if first_section >= 0:
        # Repository installation status can differ from the archive being built.
        form_state = (form_state.splitlines()[0] + b'\n\nArchive version: **' +
                      args.version.encode('utf-8') + b'**. See [build.json](build.json) for binary identity.\n'
                      b'The source/candidate checkpoints below are historical records, not this archive\'s installation status.\n' +
                      form_state[first_section:])
    files = {
        'README.md': (host / 'ADDON_README.md').read_bytes(),
        'CSS_DIAGNOSTICS.md': (host.parents[1] / 'docs' / 'CSS_DIAGNOSTICS.md').read_bytes(),
        'IME.md': (host.parents[1] / 'docs' / 'IME.md').read_bytes(),
        'TEXT_EDITING.md': (host.parents[1] / 'docs' / 'TEXT_EDITING.md').read_bytes(),
        'GODOT_TEXT_SHAPING.md': (host.parents[1] / 'docs' / 'GODOT_TEXT_SHAPING.md').read_bytes(),
        'FORM_STATE.md': form_state
            .replace(b'(../third_party/icu/README.md)', b'(ICU_DATA.md)')
            .replace(b'(../third_party/decimal/README.md)', b'(DECIMAL.md)')
            .replace(b'(../third_party/ada/README.md)', b'(ADA.md)'),
        'ICU_LICENSE.txt': (host.parents[1] / 'third_party/icu/LICENSE.txt').read_bytes(),
        'ICU_DATA.md': (host.parents[1] / 'third_party/icu/README.md').read_bytes()
            .replace(b'(LICENSE.txt)', b'(ICU_LICENSE.txt)'),
        'UNICODE_LICENSE.txt': (host.parents[1] / 'third_party/unicode/LICENSE.txt').read_bytes(),
        'DECIMAL_LICENSE.txt': (host.parents[1] / 'third_party/decimal/LICENSE.txt').read_bytes(),
        'DECIMAL.md': (host.parents[1] / 'third_party/decimal/README.md').read_bytes()
            .replace(b'(LICENSE.txt)', b'(DECIMAL_LICENSE.txt)'),
        'ADA_LICENSE_MIT.txt': (host.parents[1] / 'third_party/ada/LICENSE-MIT').read_bytes(),
        'ADA_LICENSE_APACHE.txt': (host.parents[1] / 'third_party/ada/LICENSE-APACHE').read_bytes(),
        'ADA.md': (host.parents[1] / 'third_party/ada/README.md').read_bytes()
            .replace(b'(LICENSE-MIT)', b'(ADA_LICENSE_MIT.txt)')
            .replace(b'(LICENSE-APACHE)', b'(ADA_LICENSE_APACHE.txt)'),
        'UNICODE_DATA.md': (host.parents[1] / 'third_party/unicode/README.md').read_bytes()
            .replace(b'(LICENSE.txt)', b'(UNICODE_LICENSE.txt)'),
        'LICENSE.md': (host.parents[2] / 'LICENSE.md').read_bytes(),
        'GODOT_CPP_LICENSE.md': (args.godot_cpp_dir / 'LICENSE.md').read_bytes(),
        'weva_view.gd': (host / 'addon' / 'weva_view.gd').read_bytes(),
    }
    metadata = {'schema': 1, 'version': args.version, 'status': 'development-preview',
                'godot_api': '4.7', 'architecture': 'x86_64',
                'dependencies': {'icu': {'version': '78.3', 'linkage': 'static', 'search_locale': 'en',
                                        'source_sha256': '3a2e7a47604ba702f345878308e6fefeca612ee895cf4a5f222e7955fabfe0c0'},
                                 'decimal': {'upstream': 'Chromium Blink WTF Decimal',
                                             'revision': '9596b537e435c5c23d247cb7d9925d81e8f7707e',
                                             'linkage': 'static', 'license': 'DECIMAL_LICENSE.txt'},
                                 'ada': {'version': '4.0.0',
                                         'revision': 'b12a893a45809da8103bb4f1e2f6f5ee13f9100b',
                                         'linkage': 'static', 'license': 'ADA_LICENSE_MIT.txt',
                                         'url_pattern': False}},
                'libraries': {}}
    descriptor = '[configuration]\nentry_symbol = "weva_library_init"\ncompatibility_minimum = "4.7"\nreloadable = true\n\n[libraries]\n'
    for platform, path, name in libraries:
        if path is None:
            continue
        build = validate_metadata(path, host.parents[1], args.godot_cpp_dir)
        data = verify_library(path, platform)
        files['bin/' + name] = data
        metadata['libraries'][platform] = {'path': 'bin/' + name,
                                           'sha256': hashlib.sha256(data).hexdigest(), 'build': build}
        for config in ('debug', 'release'):
            descriptor += f'{platform}.{config}.x86_64 = "res://addons/weva/bin/{name}"\n'
    files['weva.gdextension'] = descriptor.encode()
    files['build.json'] = (json.dumps(metadata, indent=2) + '\n').encode()
    for path in sorted((host / 'addon_example').iterdir()):
        if path.suffix in ('.gd', '.tscn', '.html', '.css', '.svg'):
            files['example/' + path.name] = path.read_bytes()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, 'x', compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()):
            # Stable metadata makes repeated packaging of identical inputs identical.
            info = zipfile.ZipInfo('addons/weva/' + name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, data)
    print(f'{args.output}: {len(files)} files; ' + ', '.join(metadata['libraries']))


if __name__ == '__main__':
    main()
