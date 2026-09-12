#!/usr/bin/env python3
"""Install a packaged Weva addon beside project.godot, without replacing files."""
import argparse
from pathlib import Path, PurePosixPath
import zipfile


def install(archive_path, project):
    project = Path(project).resolve()
    if (project / 'addons' / 'weva').exists():
        raise ValueError('addons/weva already exists; use a fresh project directory')
    with zipfile.ZipFile(archive_path) as archive:
        members = archive.infolist()
        for member in members:
            name = PurePosixPath(member.filename)
            if (name.parts[:2] != ('addons', 'weva') or '..' in name.parts or
                    '\\' in member.filename or ':' in member.filename or
                    (member.external_attr >> 16) & 0o170000 == 0o120000):
                raise ValueError('Unexpected addon archive path: ' + member.filename)
            if (project / member.filename).exists():
                raise ValueError('Would replace existing file: ' + member.filename)
        required = {'addons/weva/weva.gdextension', 'addons/weva/weva_view.gd', 'addons/weva/build.json'}
        if not required.issubset(archive.namelist()):
            raise ValueError('Archive is missing the WevaView helper or extension metadata')
        archive.extractall(project)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('addon', type=Path)
    args = parser.parse_args()
    install(args.addon, Path(__file__).parent)
    print('Installed. Open project.godot in Godot 4.7.2, then press F5.')
