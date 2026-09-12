#!/usr/bin/env python3
"""Fingerprint an extension and its build inputs; called by CMake after linking."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def tree_hash(root, paths):
    """Hash names and bytes, independent of checkout location or timestamps."""
    root = Path(root)
    files = []
    for relative in paths:
        path = root / relative
        if not path.exists():
            raise ValueError(f'Missing build input: {path}')
        if path.is_dir():
            files.extend(p for p in path.rglob('*') if p.is_file() and '__pycache__' not in p.parts)
        else:
            files.append(path)
    digest = hashlib.sha256()
    for path in sorted(set(files), key=lambda p: p.relative_to(root).as_posix()):
        digest.update(path.relative_to(root).as_posix().encode('utf-8') + b'\0')
        digest.update(bytes.fromhex(sha256(path)))
    return digest.hexdigest()


def source_hash(root):
    return tree_hash(root, ['CMakeLists.txt', 'libweva/CMakeLists.txt',
                           'libweva/include', 'libweva/src', 'third_party',
                           'hosts/godot/CMakeLists.txt', 'hosts/godot/src',
                           'hosts/godot/build_metadata.py'])


def godot_cpp_hash(root):
    return tree_hash(root, ['CMakeLists.txt', 'cmake', 'include', 'src',
                           'gdextension', 'tools', 'binding_generator.py',
                           'build_profile.py', 'doc_source_generator.py',
                           'make_interface_header.py', 'LICENSE.md'])


def git_state(root):
    command = ['git', '-c', f'safe.directory={Path(root).resolve().as_posix()}', '-C', str(root)]
    try:
        revision = subprocess.check_output(command + ['rev-parse', 'HEAD'],
                                           stderr=subprocess.DEVNULL, text=True).strip()
        dirty = bool(subprocess.check_output(command + ['status', '--porcelain', '--', '.'],
                                             stderr=subprocess.DEVNULL, text=True).strip())
        return {'revision': revision, 'dirty': dirty}
    except (OSError, subprocess.CalledProcessError):
        # Source archives have no Git directory. Their content hash is still exact.
        return {'revision': None, 'dirty': None}


def validate_metadata(library, root, cpp_root):
    path = Path(str(library) + '.build.json')
    if not path.is_file():
        raise ValueError(f'Missing build metadata: {path}; rebuild the extension with CMake')
    data = json.loads(path.read_text(encoding='utf-8'))
    if data.get('schema') != 1 or data.get('library_sha256') != sha256(library):
        raise ValueError(f'Build metadata does not match library: {library}')
    if data.get('source', {}).get('sha256') != source_hash(root):
        raise ValueError(f'Library was built from different source: {library}; rebuild it')
    if data.get('godot_cpp', {}).get('sha256') != godot_cpp_hash(cpp_root):
        raise ValueError(f'Library godot-cpp inputs differ from --godot-cpp-dir: {library}')
    if data.get('sanitizers') is not False:
        raise ValueError('Instrumented libraries cannot be packaged for distribution')
    if data.get('godot_api') != '4.7' or data.get('precision') != 'single':
        raise ValueError('The addon currently requires Godot API 4.7 and single precision')
    if data.get('custom_api'):
        raise ValueError('A custom Godot API requires separate compatibility verification')
    if data.get('configuration') not in ('Release', 'RelWithDebInfo'):
        raise ValueError('Package a Release or RelWithDebInfo extension')
    if not data.get('compiler', {}).get('id') or not data['compiler'].get('version'):
        raise ValueError('Build metadata is missing the compiler identity')
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('library', 'source-dir', 'godot-cpp-dir'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('configuration', 'compiler', 'compiler-version', 'cmake-version',
                 'godot-api', 'precision', 'sanitizers'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--custom-api', default='')
    args = parser.parse_args()
    root = args.source_dir.resolve()
    data = {
        'schema': 1,
        'library_sha256': sha256(args.library),
        'source': dict(git_state(root.parent), sha256=source_hash(root)),
        'godot_cpp': dict(git_state(args.godot_cpp_dir), sha256=godot_cpp_hash(args.godot_cpp_dir)),
        'configuration': args.configuration,
        'compiler': {'id': args.compiler, 'version': args.compiler_version},
        'cmake_version': args.cmake_version,
        'godot_api': args.godot_api,
        'precision': args.precision,
        'custom_api': sha256(args.custom_api) if args.custom_api else None,
        'sanitizers': args.sanitizers.upper() not in ('OFF', 'FALSE', '0', ''),
    }
    output = Path(str(args.library) + '.build.json')
    output.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print(f'Build metadata: {output}')


if __name__ == '__main__':
    main()
