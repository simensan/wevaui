import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import run_frontier_perf


class BenchmarkTimeoutTests(unittest.TestCase):
    def test_budget_scope_mismatch_launches_nothing(self):
        budget = Path(__file__).resolve().parents[2] / 'examples/frontier_camp/tests/performance_budget_desktop.json'
        for extra in [['--resolution', '1920x1080'], ['--automatic'], ['--world', '3d']]:
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as temporary:
                output = Path(temporary) / 'output'
                argv = ['run_frontier_perf.py', '--godot', 'unused', '--output', str(output),
                        '--budget', str(budget), *extra]
                with patch.object(sys, 'argv', argv), patch.object(run_frontier_perf.subprocess, 'run') as launch:
                    with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                        run_frontier_perf.main()
                self.assertEqual(error.exception.code, 2)
                launch.assert_not_called()
                self.assertFalse(output.exists())

    def test_default_and_override_reach_process_and_failure_report(self):
        for override, expected in [(None, 180), (900, 900)]:
            with self.subTest(timeout=expected), tempfile.TemporaryDirectory() as temporary:
                base = Path(temporary)
                project = base / 'project'
                native = project / 'addons/weva/bin'
                native.mkdir(parents=True)
                export = base / 'export'
                export.mkdir()
                name = 'weva_godot.dll' if sys.platform == 'win32' else 'libweva_godot.so'
                (native / name).write_bytes(b'native')
                (export / name).write_bytes(b'native')
                engine = base / 'engine'
                engine.write_bytes(b'engine')
                executable = export / 'game'
                executable.write_bytes(b'game')
                output = base / 'output'
                argv = ['run_frontier_perf.py', '--godot', str(engine), '--project', str(project),
                        '--executable', str(executable), '--output', str(output),
                        '--runs', '1', '--renderers', 'mobile']
                if override is not None:
                    argv += ['--run-timeout-seconds', str(override), '--frame-phases']

                def time_out(command, **kwargs):
                    self.assertEqual(kwargs['timeout'], expected)
                    self.assertEqual(kwargs['env']['WEVA_FRONTIER_FRAME_PHASES'], '1' if override is not None else '0')
                    raise subprocess.TimeoutExpired(command, expected, output=b'partial\xff output')

                with patch.object(sys, 'argv', argv), patch.object(run_frontier_perf.subprocess, 'run', side_effect=time_out):
                    with self.assertRaises(subprocess.TimeoutExpired):
                        run_frontier_perf.main()
                report = json.loads((output / 'summary.json').read_text())
                self.assertEqual(report['run_timeout_seconds'], expected)
                self.assertEqual(report['frame_phase_diagnostics'], override is not None)
                self.assertIsNone(report['timing_passed'])
                self.assertFalse(report['passed'])
                self.assertIn('partial', (output / 'mobile-1.log').read_text())
                exit_status = json.loads((output / 'mobile-1.exit.json').read_text())
                self.assertEqual(exit_status, {'timed_out': True, 'timeout_seconds': expected})
                self.assertEqual(report['failed_runs'][0]['exit_status'], exit_status)

    def test_invalid_timeout_creates_no_output_and_launches_no_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'output'
            argv = ['run_frontier_perf.py', '--godot', 'unused', '--output', str(output),
                    '--run-timeout-seconds', '0']
            with patch.object(sys, 'argv', argv), patch.object(run_frontier_perf.subprocess, 'run') as launch:
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                    run_frontier_perf.main()
            self.assertEqual(error.exception.code, 2)
            launch.assert_not_called()
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
