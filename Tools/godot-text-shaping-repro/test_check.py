"""A release check must reject corrupt, crashed, incomplete or noisy results."""
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import check


class ProcessResults(unittest.TestCase):
    def run_result(self, output, code=0, error=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(check.subprocess, 'run', side_effect=error,
                              return_value=subprocess.CompletedProcess([], code, output, '')):
                result = check.check_case(Path('godot'), root, root, 'emoji33', {})
            log = (root / 'emoji33.log').read_text(encoding='utf-8')
        return result, log

    def test_only_complete_success_passes(self):
        output = 'emoji33: 99 glyphs, 1360.2188 advance; 3 checks, 0 failures\n'
        result, log = self.run_result(output)
        self.assertTrue(result['passed'])
        self.assertEqual(log, output)

    def test_crash_after_success_is_failure(self):
        result, _ = self.run_result('emoji33: 99 glyphs; 3 checks, 0 failures\n', 3221226356)
        self.assertFalse(result['passed'])
        self.assertEqual(result['returncode'], 3221226356)

    def test_wrong_case_or_partial_assertions_cannot_pass(self):
        for output in ('emoji32: 96 glyphs; 3 checks, 0 failures\n',
                       'emoji33: 99 glyphs; 2 checks, 0 failures\n',
                       'emoji33: 108 glyphs; 3 checks, 3 failures\n', ''):
            with self.subTest(output=output):
                result, _ = self.run_result(output)
                self.assertFalse(result['passed'])

    def test_errors_are_preserved_and_fail(self):
        for error in ('ERROR: Failed to read the root certificate store.',
                      'SCRIPT ERROR: Parse Error', 'FAIL Source clusters'):
            with self.subTest(error=error):
                output = f'emoji33: 99 glyphs; 3 checks, 0 failures\n{error}\n'
                result, log = self.run_result(output)
                self.assertFalse(result['passed'])
                self.assertEqual(log, output)

    def test_timeout_preserves_partial_output(self):
        result, log = self.run_result('', error=subprocess.TimeoutExpired(
            ['godot'], 30, output=b'Engine started\n', stderr=b'last diagnostic\n'))
        self.assertFalse(result['passed'])
        self.assertIsNone(result['returncode'])
        self.assertEqual(log, 'Engine started\nlast diagnostic\n')

    def test_launch_failure_is_reported(self):
        result, log = self.run_result('', error=OSError('missing engine'))
        self.assertFalse(result['passed'])
        self.assertIn('missing engine', log)


if __name__ == '__main__':
    unittest.main()
