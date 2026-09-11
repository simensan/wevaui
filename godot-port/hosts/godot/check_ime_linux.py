#!/usr/bin/env python3
"""Exercise real IBus Pinyin composition in a private X11 desktop.

Requires Godot, ibus, ibus-libpinyin, xvfb, xauth, xdotool, openbox,
dbus-run-session and scrot. CJK system fonts are needed for visual review.
The fixture owns its display, session bus and settings. No desktop input
method or user language settings are changed. See docs/IME.md for limits.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--project', type=Path, default=Path(__file__).resolve().parent / 'project')
    parser.add_argument('--artifacts', type=Path, help='Parent directory for a new retained fixture')
    parser.add_argument('--ibus-sync-mode', choices=('0', '1'), default='0',
                        help='0 tests asynchronous delivery; 1 tests synchronous IBus delivery')
    parser.add_argument('--native-line-edit', action='store_true', help='Compare Godot LineEdit without Weva input routing')
    parser.add_argument('--xim', type=Path, help='Start a private XIM executable or wrapper instead of the daemon default')
    parser.add_argument('--trace-library', type=Path,
                        help='XIM diagnostic shared library, preloaded only into the Godot subprocess')
    parser.add_argument('--commit-gap-ms', type=int, default=0,
                        help='Delay XIM polling after a commit key to expose the preedit ordering race (requires trace library)')
    parser.add_argument('--session', type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.xim and not args.xim.is_file():
        parser.error('XIM executable does not exist: ' + str(args.xim))
    if args.trace_library and not args.trace_library.is_file():
        parser.error('Trace library does not exist: ' + str(args.trace_library))
    if not 0 <= args.commit_gap_ms <= 1000 or (args.commit_gap_ms and not args.trace_library):
        parser.error('--commit-gap-ms requires --trace-library and must be between 0 and 1000')
    if args.session is None:
        for program in ('xvfb-run', 'dbus-run-session', 'ibus-daemon', 'ibus', 'xdotool', 'openbox', 'scrot'):
            if not shutil.which(program):
                parser.error('Missing native test dependency: ' + program)
        if args.artifacts:
            args.artifacts.mkdir(parents=True, exist_ok=True)
        work = Path(tempfile.mkdtemp(prefix='weva-ime-', dir=args.artifacts)).resolve()
        for name in ('config', 'cache', 'runtime'):
            (work / name).mkdir(mode=0o700)
        env = dict(os.environ, XMODIFIERS='@im=ibus', GTK_IM_MODULE='ibus', GDK_BACKEND='x11',
                   QT_IM_MODULE='ibus', IBUS_ENABLE_SYNC_MODE=args.ibus_sync_mode,
                   XDG_CONFIG_HOME=str(work / 'config'), XDG_CACHE_HOME=str(work / 'cache'),
                   XDG_RUNTIME_DIR=str(work / 'runtime'), GODOT_SILENCE_ROOT_WARNING='1')
        for name in ('IBUS_ADDRESS', 'WAYLAND_DISPLAY'):
            env.pop(name, None)
        command = ['xvfb-run', '-a', '-s', '-screen 0 1280x720x24', 'dbus-run-session', '--',
                   sys.executable, str(Path(__file__).resolve()), *sys.argv[1:], '--session', str(work)]
        print('Native IME fixture:', work, flush=True)
        return subprocess.run(command, env=env, timeout=120).returncode

    work = args.session
    report = work / 'events.jsonl'
    processes = []
    logs = []
    checks = 0

    def start(command, name, env=None):
        log = (work / (name + '.log')).open('w')
        logs.append(log)
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=env)
        processes.append(process)
        return process

    def run(command, check=True):
        result = subprocess.run(command, capture_output=True, text=True, timeout=20)
        if check and result.returncode:
            raise RuntimeError(f'{command}: {result.stdout}{result.stderr}')
        return result

    def rows():
        if not report.is_file():
            return []
        return [json.loads(line) for line in report.read_text().splitlines() if line]

    def expect(condition, message):
        nonlocal checks
        checks += 1
        if not condition:
            raise RuntimeError(message + ': ' + json.dumps(rows()[-8:], ensure_ascii=False))

    def preedit():
        return next((row['text'] for row in reversed(rows()) if row['kind'] == 'os-preedit'), '')

    def until(predicate, message, seconds=10):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.05)
        raise RuntimeError(message)

    try:
        start(['openbox'], 'window-manager')
        bus = start(['ibus-daemon', '--panel=disable', '--emoji-extension=disable'] +
                    ([] if args.xim else ['--xim']), 'ibus')
        until(lambda: bus.poll() is None and 'libpinyin' in run(['ibus', 'list-engine'], False).stdout,
              'IBus did not expose libpinyin')
        if args.xim:
            start([str(args.xim.resolve())], 'xim')
        activation = run(['ibus', 'engine', 'libpinyin'], False)
        (work / 'engine-activation.log').write_text(f'{activation.returncode}\n{activation.stdout}{activation.stderr}')
        # IBus can select the engine yet fail its separate XKB layout command.
        expect(run(['ibus', 'engine']).stdout.strip() == 'libpinyin', 'Pinyin was not selected')
        godot_env = dict(os.environ)
        if args.trace_library:
            godot_env['LD_PRELOAD'] = str(args.trace_library.resolve())
            godot_env['WEVA_XIM_COMMIT_GAP_MS'] = str(args.commit_gap_ms)
        app = start([args.godot, '--path', str(args.project.resolve()), '--rendering-driver', 'opengl3',
                     '--audio-driver', 'Dummy', '--resolution', '640x360', 'ime_native_probe.tscn', '--',
                     '--ime-report=' + str(report)] + (['--native-line-edit'] if args.native_line_edit else []), 'godot', godot_env)
        until(lambda: any(row['kind'] == 'ready' for row in rows()), 'Godot did not initialize')
        window = run(['xdotool', 'search', '--onlyvisible', '--name', 'Weva IME Native Probe']).stdout.splitlines()[-1]
        run(['xdotool', 'windowactivate', '--sync', window])
        run(['xdotool', 'mousemove', '--window', window, '60', '225' if args.native_line_edit else '115', 'click', '1'])
        # The X11 backend focuses a child window for IME input. Wait for that
        # focus handoff, not an arbitrary delay before injecting physical keys.
        until(lambda: run(['xdotool', 'getwindowfocus', '-f']).stdout.strip() != window,
              'Godot did not focus its X11 IME window', 3)
        time.sleep(0.1)
        run(['xdotool', 'type', '--delay', '130', 'nihao'])
        until(lambda: preedit() == '你好', 'Pinyin preedit did not arrive')
        expect(rows()[-1]['value'] == ('' if args.native_line_edit else '你好'), 'Preedit value is incorrect')
        expect(not any(row['kind'] == 'text' for row in rows()), 'Preedit leaked committed text')
        run(['scrot', str(work / 'preedit-desktop.png')])
        run(['xdotool', 'key', 'space'])
        until(lambda: preedit() == '' and rows()[-1]['value'] == '你好' and not rows()[-1]['composing'],
              'Native commit did not arrive or preedit did not clear', 3)
        expect(rows()[-1]['value'] == '你好' and not rows()[-1]['composing'], 'Commit changed the result')
        commits = [row['text'] for row in rows() if row['kind'] == ('native-input' if args.native_line_edit else 'text')]
        expect(commits[-1:] == ['你好'] if args.native_line_edit else commits == ['你好'], 'Commit was truncated or duplicated')
        run(['scrot', str(work / 'committed-desktop.png')])
        run(['xdotool', 'type', '--delay', '130', 'zhongwen'])
        until(lambda: preedit() == '中文', 'Second preedit did not arrive')
        expect(rows()[-1]['value'] == ('你好' if args.native_line_edit else '你好中文'), 'Second preedit lost committed text')
        run(['xdotool', 'key', 'Escape'])
        until(lambda: preedit() == '' and rows()[-1]['value'] == '你好', 'Native cancellation did not arrive', 3)
        expect(rows()[-1]['value'] == '你好', 'Cancellation changed earlier text')
        run(['xdotool', 'key', 'ctrl+z'])
        until(lambda: rows()[-1]['value'] == '', 'One undo did not remove the committed composition')
        expect(not rows()[-1]['composing'], 'Undo left a composition active')
        output = (work / 'godot.log').read_text()
        expect(app.poll() is None and 'SCRIPT ERROR:' not in output and 'FAIL  ' not in output, 'Godot probe failed')
        control = 'LineEdit' if args.native_line_edit else 'Weva'
        print(f'godot native IBus IME: {checks} checks, 0 failures ({control}, sync mode {args.ibus_sync_mode})', flush=True)
        return 0
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        for log in logs:
            log.close()


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
