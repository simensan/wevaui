"""The WSL gate must execute the same live Windows reference as CI."""
import argparse
import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch


SOURCE = Path(__file__).resolve().parents[1] / 'oracle/run_chrome_checks.py'
SPEC = importlib.util.spec_from_file_location('chrome_reference', SOURCE)
RUNNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNNER)


class WindowsReferenceTests(unittest.TestCase):
    def args(self, **overrides):
        fields = dict(windows_python='/mnt/c/Python/python.exe', chrome='C:/Chrome/chrome.exe',
                      out='/mnt/c/repo with spaces/results', known_failing='/mnt/c/repo/known.txt',
                      only=None)
        fields.update(overrides)
        return argparse.Namespace(**fields)

    def test_paths_remain_arguments_including_shell_characters(self):
        value = '/mnt/c/name with spaces/$literal;`text`/out'
        with patch.object(RUNNER.subprocess, 'check_output', return_value='C:\\out\n') as call:
            self.assertEqual(RUNNER.windows_path(value), 'C:\\out')
        self.assertEqual(call.call_args.args[0], ['wslpath', '-w', str(Path(value).absolute())])
        self.assertNotIn('shell', call.call_args.kwargs)
        with patch.object(RUNNER.subprocess, 'check_output') as call:
            self.assertEqual(RUNNER.windows_path('C:/Chrome/chrome.exe'), 'C:/Chrome/chrome.exe')
            call.assert_not_called()

    def test_fresh_runner_preserves_filter_known_file_and_failure_exit(self):
        def convert(path):
            return path if path == 'C:/Chrome/chrome.exe' else 'C:/shared/' + Path(path).name
        for result, known in [(0, '/mnt/c/repo/known.txt'), (1, '/mnt/c/repo/known.txt'), (2, '')]:
            with self.subTest(result=result), patch.object(RUNNER.sys, 'platform', 'linux'), \
                    patch.object(RUNNER, 'windows_path', side_effect=convert), \
                    patch.object(RUNNER.subprocess, 'run', return_value=subprocess.CompletedProcess([], result)) as run:
                self.assertEqual(RUNNER.run_windows_reference(self.args(only='number_editing', known_failing=known)), result)
                command = run.call_args.args[0]
                self.assertEqual(command, ['/mnt/c/Python/python.exe', 'C:/shared/run_chrome_checks.py',
                                          '--chrome', 'C:/Chrome/chrome.exe', '--out', 'C:/shared/results',
                                          '--known-failing', 'C:/shared/known.txt' if known else '', '--only', 'number_editing'])
                self.assertNotIn('shell', run.call_args.kwargs)

    def test_no_browser_or_non_wsl_cannot_fall_back_to_a_different_reference(self):
        with patch.object(RUNNER.sys, 'platform', 'win32'), self.assertRaises(ValueError):
            RUNNER.run_windows_reference(self.args())
        with patch.object(RUNNER.sys, 'platform', 'linux'), self.assertRaises(ValueError):
            RUNNER.run_windows_reference(self.args(chrome=None))

    def test_linux_filesystem_output_rejected_before_starting_windows(self):
        with patch.object(RUNNER.sys, 'platform', 'linux'), \
                patch.object(RUNNER, 'windows_path', return_value='\\\\wsl.localhost\\Ubuntu\\tmp\\out'), \
                patch.object(RUNNER.subprocess, 'run') as run, self.assertRaises(ValueError):
            RUNNER.run_windows_reference(self.args(out='/tmp/out'))
        run.assert_not_called()


if __name__ == '__main__':
    unittest.main()
