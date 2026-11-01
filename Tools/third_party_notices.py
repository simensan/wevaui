"""One dependency notice inventory for the native SDK, Unity and Godot packages."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def inventory(root=ROOT):
    data = json.loads((Path(root) / 'third_party/notices.json').read_text(encoding='utf-8'))
    if data.get('schema') != 1:
        raise ValueError('Unsupported dependency notice inventory')
    notices = data['notices']
    dependencies = data['dependencies']
    if not notices or {n['component'] for n in notices} != set(dependencies):
        raise ValueError('Dependency notices and SBOM components differ')
    for host in ['native', 'godot']:
        names = [n[host] for n in notices]
        if len(names) != len(set(names)) or any('/' in n or chr(92) in n or n in ['', '.', '..'] for n in names):
            raise ValueError('Invalid or duplicate notice destinations: ' + host)
    for notice in notices:
        source = Path(notice['source'])
        if source.is_absolute() or '..' in source.parts or not (Path(root) / source).is_file():
            raise ValueError('Missing or invalid notice source: ' + str(source))
    for component, metadata in dependencies.items():
        if not any(n['component'] == component and n['godot'] == metadata['license'] for n in notices):
            raise ValueError('SBOM license has no bundled notice: ' + component)
    return data


def native_licenses():
    return {'Weva.txt': 'LICENSE.md', **{n['native']: n['source'] for n in inventory()['notices']}}


def unity_components():
    return [(n['heading'], n['version'], n['license'], n['source']) for n in inventory()['notices']]


def godot_notices(root=ROOT):
    return {n['godot']: (Path(root) / n['source']).read_bytes() for n in inventory(root)['notices']}


def godot_dependencies():
    return inventory()['dependencies']
