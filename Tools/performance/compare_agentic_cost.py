"""Alternate existing Agentic weva_cost executables; retain every raw receipt.

Reports CPU timings printed by the diagnostic, not GPU completion or FPS.
"""
import argparse
import hashlib
import json
import re
import statistics
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--rounds', type=int, default=8)
    parser.add_argument('--args', nargs=argparse.REMAINDER, default=[])
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('rounds must be positive')
    binaries = {'before': args.before.resolve(strict=True), 'after': args.after.resolve(strict=True)}
    args.output.mkdir(parents=True, exist_ok=False)
    rows = []
    for round_index in range(args.rounds):
        order = ['before', 'after'] if round_index % 2 == 0 else ['after', 'before']
        for version in order:
            result = subprocess.run([str(binaries[version]), *args.args], capture_output=True, text=True, timeout=120)
            (args.output / f'{round_index}-{version}.log').write_text(result.stdout + result.stderr, encoding='utf-8')
            result.check_returncode()
            blocks = result.stdout.split('a frame where a binding moved, mean of 200:')[1:]
            if len(blocks) != 2:
                raise ValueError('Expected both documented weva_cost workloads')
            for case, block in zip(['hud', 'inventory'], blocks):
                metrics = {}
                for key, pattern in [('core', r'core update, total\s+([0-9.]+)'),
                                     ('host', r'host draw\s+([0-9.]+)'),
                                     ('frame', r'\n    frame\s+([0-9.]+)')]:
                    match = re.search(pattern, block)
                    if not match:
                        raise ValueError(f'Missing {key} timing for {case}')
                    metrics[key] = float(match[1])
                rows.append(dict(round=round_index, version=version, case=case, **metrics))
    receipt = {'binaries': {k: {'path': str(v), 'sha256': hashlib.sha256(v.read_bytes()).hexdigest()}
                            for k, v in binaries.items()}, 'arguments': args.args, 'rows': rows}
    (args.output / 'results.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    for case in ['hud', 'inventory']:
        for version in binaries:
            print(case, version, {key: statistics.median(row[key] for row in rows
                  if row['case'] == case and row['version'] == version) for key in ['core', 'host', 'frame']})


if __name__ == '__main__':
    main()
