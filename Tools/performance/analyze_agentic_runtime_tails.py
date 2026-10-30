"""Attribute runtime profile tails without discarding slow frames or summing overlaps."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics


def analyze(profile):
    artifact = next(a for a in profile['artifacts'] if a['kind'] == 'profile_frames')
    payload = Path(artifact['path']).read_bytes()
    if hashlib.sha256(payload).hexdigest() != artifact['sha256']:
        raise ValueError('Profile frame artifact hash mismatch')
    frames = [json.loads(line) for line in payload.splitlines()]
    if len(frames) != profile['summary']['frames'] or not frames:
        raise ValueError('Incomplete frame artifact')
    names = profile['summary']['section_names']
    if any(len(f['sections_ms']) != len(names) for f in frames):
        raise ValueError('Section schema mismatch')
    ranked = sorted(frames, key=lambda f: f['interval_ms'])
    count = max(1, math.ceil(len(frames) * .05))
    middle_start = (len(frames) - count) // 2

    def summarize(group):
        def mean(get):
            return statistics.mean(get(f) for f in group)
        return dict(
            count=len(group),
            interval_mean_ms=mean(lambda f: f['interval_ms']),
            main_cpu_mean_ms=mean(lambda f: f['main_cpu_ms']),
            sections_mean_ms={name: mean(lambda f: f['sections_ms'][i])
                              for i, name in enumerate(names)},
            worker_mean_ms={name: mean(lambda f: f['worker_ms'][name])
                            for name in frames[0]['worker_ms']},
        )

    return dict(variant=profile['variant'], round=profile['round'], mode=profile['mode'],
                source_sha256=artifact['sha256'],
                all=summarize(frames), middle_five_percent=summarize(
                    ranked[middle_start:middle_start + count]),
                slowest_five_percent=summarize(ranked[-count:]),
                chronological_blocks=[summarize(frames[i:i + 100])
                                      for i in range(0, len(frames), 100)])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('profiles', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    profiles = json.loads(args.profiles.read_text(encoding='utf-8'))
    result = dict(method='Means within wall-interval-ranked middle/slowest 5%; '
                  'chronological 100-frame blocks. Sections overlap: do not sum. '
                  'Same-frame association does not establish causality across threads.',
                  profiles=[analyze(profile) for profile in profiles])
    with args.output.open('x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
        output.write('\n')


if __name__ == '__main__':
    main()
