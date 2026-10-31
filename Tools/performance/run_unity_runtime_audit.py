"""Measure warmed Unity UI CPU stages; excludes GPU submission and frame pacing."""
import argparse
import hashlib
import json
import math
import os
import re
from pathlib import Path
import statistics
import subprocess


def full_inventory_payload(fixtures):
    """Bind the exact prepared mutation sequence, including boolean removal."""
    html = (fixtures / 'inventory.html').read_text(encoding='utf-8')
    states = json.loads((fixtures / 'mutations.json').read_text(encoding='utf-8'))[1]
    targets = [(m['selector'], m.get('attribute')) for m in states[0]]
    if not states or len(set(targets)) != len(targets):
        raise ValueError('Inventory requires nonempty states and unique targets')
    for index, (selector, attribute) in enumerate(targets):
        if not selector.startswith('#'):
            raise ValueError('Expected an inventory ID selector')
        pattern = r'(<[^>]+\bid="' + re.escape(selector[1:]) + r'"[^>]*>)'
        binding = '{{Field' + str(index) + '}}'
        if attribute:
            def rewrite(match):
                tag = re.sub(r'\s+' + re.escape(attribute) + r'="[^"]*"', '', match[1])
                return tag[:-1] + f' {attribute}="{binding}">'
            html, count = re.subn(pattern, rewrite, html)
        else:
            html, count = re.subn(pattern + r'[^<]*(</[^>]+>)',
                                  lambda m: m[1] + binding + m[2], html)
        if count != 1:
            raise ValueError(f'Expected exactly one target for {selector}/{attribute}')
    steps = []
    for state in states:
        if [(m['selector'], m.get('attribute')) for m in state] != targets:
            raise ValueError('Inventory states must have the same ordered targets')
        steps.append({'values': [dict(m, key=f'Field{i}', remove=m['value'] is None)
                                 for i, m in enumerate(state)]})
    return {'html': html, 'steps': steps}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('unity', 'project', 'fixtures', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--full-inventory', action='store_true',
                        help='Also measure all prepared inventory selection mutations through bindings')
    args = parser.parse_args()
    if args.runs < 1:
        parser.error('runs must be positive')
    unity, project, fixtures = (p.resolve(strict=True) for p in (args.unity, args.project, args.fixtures))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    scenarios = ['idle-hud', 'hud', 'inventory']
    payload = output / 'full-inventory-input.json'
    if args.full_inventory:
        payload.write_text(json.dumps(full_inventory_payload(fixtures)), encoding='utf-8')
        scenarios.append('inventory-full')
    summaries = []
    image_hashes = {}
    for index in range(args.runs):
        destination = output / str(index)
        env = dict(os.environ, WEVA_PREPARATION_FIXTURE=str(fixtures), WEVA_PREPARATION_OUTPUT=str(destination))
        env.pop('WEVA_RUNTIME_FULL_INVENTORY', None)
        if args.full_inventory:
            env['WEVA_RUNTIME_FULL_INVENTORY'] = str(payload)
        result = subprocess.run([str(unity), '-batchmode', '-projectPath', str(project),
            '-executeMethod', 'Weva.EditorTools.RuntimeRenderingBenchmark.Run', '-quit',
            '-logFile', str(output / f'{index}.log')], env=env, timeout=300,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        if result.returncode:
            raise RuntimeError(f'run {index}: Unity exited {result.returncode}; evidence retained')
        for scenario in scenarios:
            raw = json.loads((destination / f'{scenario}.json').read_text())
            samples = raw['samples']
            if raw['scenario'] != scenario or len(samples) != 2048 or raw['warmup'] != 128:
                raise RuntimeError('unexpected workload configuration')
            for sample in samples:
                sample['total_cpu_ms'] = sample['update_ms'] + sample['sync_ms'] + sample['record_ms']
            summary = {'run': index, 'scenario': scenario, 'unity': raw['unity'], 'device': raw['device']}
            for key in ('update_ms', 'sync_ms', 'record_ms', 'total_cpu_ms',
                        'core_update_ms', 'cascade_ms', 'layout_ms', 'paint_ms',
                        'batches', 'reused', 'uploaded_triangles'):
                values = sorted(s[key] for s in samples)
                summary[key] = {'median': statistics.median(values), 'p95': values[math.ceil(len(values)*.95)-1], 'max': max(values)}
            digest = hashlib.sha256((destination / f'{scenario}.png').read_bytes()).hexdigest()
            if scenario in image_hashes and image_hashes[scenario] != digest:
                raise RuntimeError('Repeated runtime output differs')
            image_hashes[scenario] = digest
            summary['image_sha256'] = digest
            summaries.append(summary)
        (output / 'summary.json').write_text(json.dumps(summaries, indent=2)+'\n')
        print(f'run {index}: {len(scenarios)} workloads passed', flush=True)


if __name__ == '__main__':
    main()
