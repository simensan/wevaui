#!/usr/bin/env python3
"""Require a complete oracle run, including process status and every fixture."""

import argparse
from pathlib import Path
import re


def validate(log, exit_code, expected, max_differences=0):
    summaries = re.findall(r'^(\d+)/(\d+) agree, (\d+) differ, (\d+) reference bugs, (\d+) errored$', log, re.M)
    if len(summaries) != 1 or expected <= 0:
        return False
    agree, total, differ, reference, errors = map(int, summaries[0])
    return (total == expected and agree + differ + reference + errors == total and
            errors == 0 and differ <= max_differences and exit_code == int(differ > 0))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--log', type=Path, required=True)
    parser.add_argument('--corpus', type=Path, required=True)
    parser.add_argument('--exit-code', type=int, required=True)
    parser.add_argument('--max-differences', type=int, default=0)
    args = parser.parse_args()
    log = args.log.read_text(encoding='utf-8', errors='replace')
    expected = len(list(args.corpus.glob('*.html')))
    summaries = [line for line in log.splitlines() if ' agree,' in line]
    print('\n'.join(summaries) if summaries else 'No oracle summary')
    if not validate(log, args.exit_code, expected, args.max_differences):
        print(f'FAIL incomplete or failing oracle ({expected} fixtures, exit {args.exit_code})')
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
