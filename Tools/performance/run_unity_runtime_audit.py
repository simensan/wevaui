"""Measure warmed Unity UI CPU stages; excludes GPU submission and frame pacing."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('unity', 'project', 'fixtures', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--runs', type=int, default=3)
    args = parser.parse_args()
    if args.runs < 1:
        parser.error('runs must be positive')
    unity, project, fixtures = (p.resolve(strict=True) for p in (args.unity, args.project, args.fixtures))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    summaries = []
    image_hashes = {}
    for index in range(args.runs):
        destination = output / str(index)
        env = dict(os.environ, WEVA_PREPARATION_FIXTURE=str(fixtures), WEVA_PREPARATION_OUTPUT=str(destination))
        result = subprocess.run([str(unity), '-batchmode', '-projectPath', str(project),
            '-executeMethod', 'Weva.EditorTools.RuntimeRenderingBenchmark.Run', '-quit',
            '-logFile', str(output / f'{index}.log')], env=env, timeout=300,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        if result.returncode:
            raise RuntimeError(f'run {index}: Unity exited {result.returncode}; evidence retained')
        for scenario in ('idle-hud', 'hud', 'inventory'):
            raw = json.loads((destination / f'{scenario}.json').read_text())
            samples = raw['samples']
            if raw['scenario'] != scenario or len(samples) != 2048 or raw['warmup'] != 128:
                raise RuntimeError('unexpected workload configuration')
            for sample in samples:
                sample['total_cpu_ms'] = sample['update_ms'] + sample['sync_ms'] + sample['record_ms']
            summary = {'run': index, 'scenario': scenario, 'unity': raw['unity'], 'device': raw['device']}
            for key in ('update_ms', 'sync_ms', 'record_ms', 'total_cpu_ms', 'batches', 'reused', 'uploaded_triangles'):
                values = sorted(s[key] for s in samples)
                summary[key] = {'median': statistics.median(values), 'p95': values[math.ceil(len(values)*.95)-1], 'max': max(values)}
            digest = hashlib.sha256((destination / f'{scenario}.png').read_bytes()).hexdigest()
            if scenario in image_hashes and image_hashes[scenario] != digest:
                raise RuntimeError('Repeated runtime output differs')
            image_hashes[scenario] = digest
            summary['image_sha256'] = digest
            summaries.append(summary)
        (output / 'summary.json').write_text(json.dumps(summaries, indent=2)+'\n')
        print(f'run {index}: three workloads passed', flush=True)


if __name__ == '__main__':
    main()
