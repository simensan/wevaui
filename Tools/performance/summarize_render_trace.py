"""Summarize Chrome CPU stages, excluding nested double counting and trace tails.

CPU work per mutation is not elapsed frame time or hardware GPU execution time.
"""
import argparse
import collections
import json
from pathlib import Path


def union_duration(intervals):
    total = 0
    end = float('-inf')
    for start, stop in sorted(intervals):
        total += max(0, stop - max(start, end))
        end = max(end, stop)
    return total


def summarize(events, updates):
    starts = [e for e in events if e.get('name') == 'renderbench:start']
    stops = [e for e in events if e.get('name') == 'renderbench:end']
    if len(starts) != 1 or len(stops) != 1 or updates <= 0:
        raise ValueError('Expected one measurement window and positive update count')
    start, stop = starts[0]['ts'], stops[0]['ts']
    if stop <= start:
        raise ValueError('Invalid measurement window')
    renderer_pid = starts[0]['pid']
    groups = {'style_layout': {'UpdateLayoutTree', 'Layout'},
              'paint_recording': {'PrePaint', 'Paint', 'Layerize'},
              'raster_workers': {'RasterTask'}}
    result = {}
    for group, names in groups.items():
        threads = collections.defaultdict(list)
        count = 0
        for event in events:
            if (event.get('name') not in names or event.get('ph') != 'X'
                    or event.get('pid') != renderer_pid):
                continue
            a, b = max(start, event['ts']), min(stop, event['ts'] + event.get('dur', 0))
            if b > a:
                threads[event['tid']].append((a, b))
                count += 1
        result[group + '_cpu_ms_per_update'] = sum(map(union_duration, threads.values())) / 1000 / updates
        result[group + '_events'] = count
    result['window_ms'] = (stop - start) / 1000
    result['gpu_execution_ms'] = None
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    receipt = json.loads((args.directory / 'results.json').read_text(encoding='utf-8'))
    rows = []
    for row in receipt['results']:
        events = json.loads((args.directory / row['trace']).read_text(encoding='utf-8'))['traceEvents']
        rows.append({'case': row['name'], 'round': row['round'], **summarize(events, row['updates'])})
    print(json.dumps(rows, indent=2))


if __name__ == '__main__':
    main()
