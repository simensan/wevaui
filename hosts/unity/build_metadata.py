#!/usr/bin/env python3
"""Fingerprint the Unity plugin and its build inputs; called by CMake after linking.

The same digests the Godot packager verifies (library bytes, source tree,
git state, compiler and configuration) plus the ABI version the header
declares, written as `<plugin>.build.json` beside the plugin. There is no
engine binding to fingerprint: the plugin depends on nothing but the core.
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'godot'))
from build_metadata import git_state, sha256, source_hash  # noqa: E402


def abi_version(header: Path):
    text = header.read_text(encoding='utf-8')
    major = re.search(r'#define\s+WEVA_ABI_VERSION_MAJOR\s+(\d+)', text)
    minor = re.search(r'#define\s+WEVA_ABI_VERSION_MINOR\s+(\d+)', text)
    if not major or not minor:
        raise SystemExit('weva_c.h declares no ABI version')
    return {'major': int(major.group(1)), 'minor': int(minor.group(1))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('library', 'source-dir'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('configuration', 'compiler', 'compiler-version', 'cmake-version', 'sanitizers'):
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args()
    root = args.source_dir.resolve()
    data = {
        'schema': 1,
        'host': 'unity',
        'library_sha256': sha256(args.library),
        'source': dict(git_state(root), sha256=source_hash(root, host='unity')),
        'abi': abi_version(root / 'libweva' / 'include' / 'weva_c.h'),
        'configuration': args.configuration,
        'compiler': {'id': args.compiler, 'version': args.compiler_version},
        'cmake_version': args.cmake_version,
        'sanitizers': args.sanitizers.upper() not in ('OFF', 'FALSE', '0', ''),
    }
    output = Path(str(args.library) + '.build.json')
    output.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print(f'Build metadata: {output}')


if __name__ == '__main__':
    main()
