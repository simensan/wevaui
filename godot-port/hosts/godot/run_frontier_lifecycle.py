#!/usr/bin/env python3
"""Check cold construction, prepared UI reuse and sustained lifecycle in a release export."""
import argparse
import ctypes
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import sys
import threading
import time

from run_frontier_perf import sha


def process_memory(process):
    """OS process bytes; independent of Godot's disabled release allocator monitor."""
    if sys.platform == 'win32':
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [('cb', wintypes.DWORD), ('faults', wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in ['peak_working', 'working', 'peak_pool_paged',
                                                   'pool_paged', 'peak_pool_nonpaged', 'pool_nonpaged',
                                                   'pagefile', 'peak_pagefile', 'private']]

        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        read = ctypes.windll.psapi.GetProcessMemoryInfo
        read.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        read.restype = wintypes.BOOL
        if read(wintypes.HANDLE(int(process._handle)), ctypes.byref(counters), counters.cb):
            return {'resident_bytes': counters.working, 'private_bytes': counters.private}
    elif sys.platform.startswith('linux'):
        try:
            lines = Path(f'/proc/{process.pid}/smaps_rollup').read_text().splitlines()
            values = {line.split(':')[0]: int(line.split()[1]) * 1024 for line in lines if ':' in line}
            return {'resident_bytes': values['Rss'],
                    'private_bytes': values.get('Private_Clean', 0) + values.get('Private_Dirty', 0)}
        except (OSError, ValueError, KeyError):
            pass
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', type=Path, required=True, help='Release export including tests/lifecycle.gd')
    parser.add_argument('--project', type=Path, required=True, help='Matching project used to export the executable')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--renderer', choices=['gl_compatibility', 'mobile'], default='mobile')
    parser.add_argument('--resolution', default='1920x1080')
    parser.add_argument('--world', choices=['static', '3d'], default='3d')
    parser.add_argument('--cycles', type=int, default=200)
    parser.add_argument('--seconds', type=float, default=120)
    parser.add_argument('--soak-frames', type=int, default=0, help='Use an exact frame count instead of duration; --seconds remains the timeout allowance')
    parser.add_argument('--soak-without-ui', action='store_true', help='Release the warmed UI before soaking, retaining the same state and scene drive')
    parser.add_argument('--font-cache-log', action='store_true', help='Enable retained core shaping-cache diagnostics; requires an instrumented native binary')
    parser.add_argument('--first-hidden', action='store_true', help='Prepare the first document hidden before its first draw')
    parser.add_argument('--unicode-names', action='store_true', help='Churn unique long mixed-script names during reuse, recreation and soak; requires a Unicode-safe engine with ICU embedded')
    args = parser.parse_args()
    if not re.fullmatch(r'[1-9][0-9]*x[1-9][0-9]*', args.resolution):
        parser.error('resolution must be WIDTHxHEIGHT')
    width, height = map(int, args.resolution.split('x'))
    if not (1024 <= width <= 7680 and 720 <= height <= 4320) or args.cycles < 1 or args.seconds < 1 or args.soak_frames < 0:
        parser.error('invalid resolution, cycles or seconds')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    executable = args.executable.resolve()
    project = args.project.resolve()
    library_name = 'weva_godot.dll' if sys.platform == 'win32' else 'libweva_godot.so'
    libraries = list(executable.parent.rglob(library_name))
    if len(libraries) != 1 or sha(libraries[0]) != sha(project / 'addons/weva/bin' / library_name):
        raise RuntimeError('Export library differs from project')
    env = {key: value for key, value in os.environ.items() if not key.startswith('WEVA_')}
    env.update(WEVA_FRONTIER_PERF_OUT=str(output), WEVA_FRONTIER_CYCLES=str(args.cycles),
               WEVA_FRONTIER_SECONDS=str(args.seconds), WEVA_FRONTIER_RESOLUTION=args.resolution,
               WEVA_FRONTIER_SOAK_FRAMES=str(args.soak_frames),
               WEVA_FRONTIER_SOAK_WITHOUT_UI='1' if args.soak_without_ui else '0',
               WEVA_FRONTIER_WORLD=args.world, WEVA_FRONTIER_FIRST_HIDDEN='1' if args.first_hidden else '0',
               WEVA_FRONTIER_UNICODE='1' if args.unicode_names else '0')
    if args.font_cache_log:
        env['WEVA_FONT_CACHE_LOG'] = '1'
    command = [str(executable), '--audio-driver', 'Dummy', '--log-file', str(output / 'engine.log'),
               '--rendering-method', args.renderer, '--position', '-10000,-10000',
               '--', '--lifecycle']
    (output / 'command.json').write_text(json.dumps(command, indent=2), encoding='utf-8')
    lines = queue.Queue()
    process = subprocess.Popen(command, env=env, cwd=output, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')

    def collect():
        for line in process.stdout:
            lines.put(line)

    reader = threading.Thread(target=collect, daemon=True)
    reader.start()
    started = time.monotonic()
    samples, markers, text = [], [], []
    phase = 'startup'
    deadline = started + args.seconds + max(180, args.cycles * 2)
    log = (output / 'run.log').open('w', encoding='utf-8')
    try:
        while process.poll() is None or reader.is_alive() or not lines.empty():
            if time.monotonic() > deadline:
                raise TimeoutError('Lifecycle run exceeded its deadline')
            memory = process_memory(process) if process.poll() is None else None
            if memory:
                samples.append(dict(elapsed_seconds=time.monotonic() - started, phase=phase, **memory))
            try:
                line = lines.get(timeout=0.25)
            except queue.Empty:
                continue
            text.append(line)
            log.write(line)
            log.flush()
            if line.startswith('FRONTIER_MEMORY '):
                marker = json.loads(line[len('FRONTIER_MEMORY '):])
                phase = marker['phase']
                marker['process_memory'] = process_memory(process)
                markers.append(marker)
        code = process.wait()
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        log.close()
        (output / 'process-memory.json').write_text(json.dumps({'samples': samples, 'markers': markers}, indent=2), encoding='utf-8')
    stdout = ''.join(text)
    if code or 'ERROR:' in stdout or 'FAIL ' in stdout or not (output / 'lifecycle.json').is_file():
        (output / 'verification.json').write_text(json.dumps({'passed': False, 'exit_code': code,
                                                              'log': 'run.log'}, indent=2), encoding='utf-8')
        raise RuntimeError(f'Lifecycle failed (exit {code}); see {output / "run.log"}')
    result = json.loads((output / 'lifecycle.json').read_text(encoding='utf-8'))
    passed = (code == 0 and 'ERROR:' not in stdout and 'FAIL ' not in stdout and
              'FRONTIER_LIFECYCLE_COMPLETE true' in stdout and result['passed'] and not result['debug_build'] and
              result['resolution'] == [width, height] and result['world'] == args.world and
              result['renderer'] == args.renderer and result['cycles'] == args.cycles and
              result['first_hidden'] == args.first_hidden and
              result.get('unicode_names') == args.unicode_names and
              result.get('name_updates', 0) > 0 and
              result.get('soak_frame_target', 0) == args.soak_frames and
              result.get('soak_without_ui', False) == args.soak_without_ui and
              (result['results']['soak_frames']['samples'] == args.soak_frames if args.soak_frames
               else result['results']['soak_seconds'] >= args.seconds))
    identical = (output / 'cold.png').read_bytes() == (output / 'prepared.png').read_bytes()
    cache_logging_observed = 'WEVA_SHAPE_CACHE ' in stdout
    passed = passed and identical and (not args.font_cache_log or cache_logging_observed)
    receipt = {'passed': passed, 'prepared_pixels_match_cold': identical,
               'font_cache_log_requested': args.font_cache_log,
               'font_cache_log_observed': cache_logging_observed,
               'executable_sha256': sha(executable), 'library_sha256': sha(libraries[0]),
               'source_sha256': {str(p.relative_to(project)): sha(p) for p in project.rglob('*')
                                 if p.is_file() and p.suffix in ['.gd', '.tscn', '.html', '.css'] and '.godot' not in p.parts},
               'process_memory_available': bool(samples), 'result': result}
    (output / 'verification.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(output / 'verification.json', flush=True)
    raise SystemExit(0 if passed else 1)


if __name__ == '__main__':
    main()
