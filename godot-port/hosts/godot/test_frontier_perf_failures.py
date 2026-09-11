import json
from pathlib import Path
import tempfile
import unittest
import subprocess

from run_frontier_perf import record_failed_run


class FailureReports(unittest.TestCase):
    def test_timeout_keeps_partial_run_and_does_not_grade_timing(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / 'mobile-1.log').write_text('partial benchmark output', encoding='utf-8')
            record_failed_run({'runs': []}, out, 'mobile', 1,
                              subprocess.TimeoutExpired(['game'], 900))
            saved = json.loads((out / 'summary.json').read_text(encoding='utf-8'))
            self.assertIn('900 seconds', saved['failed_runs'][0]['error'])
            self.assertIsNone(saved['timing_passed'])
            self.assertEqual((out / saved['failed_runs'][0]['log']).read_text(), 'partial benchmark output')

    def test_preserves_failed_results_and_previous_runs(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / 'mobile-2').mkdir()
            failed = {'passed': False, 'results': [{'workload': 'inventory_sort'}]}
            (out / 'mobile-2/results.json').write_text(json.dumps(failed), encoding='utf-8')
            previous = [{'renderer': 'mobile', 'index': 1}]
            report = {'runs': previous, 'passed': True, 'timing_passed': True}
            record_failed_run(report, out, 'mobile', 2, RuntimeError('input failure'))
            saved = json.loads((out / 'summary.json').read_text(encoding='utf-8'))
            self.assertEqual(saved['runs'], previous)
            self.assertEqual(saved['failed_runs'][0]['result'], failed)
            self.assertEqual(saved['failed_runs'][0]['log'], 'mobile-2.log')
            self.assertFalse(saved['passed'])
            self.assertFalse(saved['functional_passed'])
            self.assertIsNone(saved['timing_passed'])

    def test_first_run_crash_without_result_still_has_report(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / 'mobile-1.exit.json').write_text(json.dumps({'timed_out': False, 'returncode': 17}), encoding='utf-8')
            record_failed_run({'runs': []}, out, 'mobile', 1, RuntimeError('crash'))
            saved = json.loads((out / 'summary.json').read_text(encoding='utf-8'))
            self.assertEqual(saved['failed_runs'][0]['error'], 'crash')
            self.assertNotIn('result', saved['failed_runs'][0])
            self.assertEqual(saved['failed_runs'][0]['exit_status']['returncode'], 17)

    def test_damaged_exit_status_does_not_hide_original_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / 'mobile-1.exit.json').write_text('{', encoding='utf-8')
            record_failed_run({'runs': []}, out, 'mobile', 1, RuntimeError('original crash'))
            saved = json.loads((out / 'summary.json').read_text(encoding='utf-8'))
            self.assertEqual(saved['failed_runs'][0]['error'], 'original crash')
            self.assertIn('exit_status_read_error', saved['failed_runs'][0])

    def test_truncated_result_does_not_hide_original_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / 'mobile-1').mkdir()
            (out / 'mobile-1/results.json').write_text('{', encoding='utf-8')
            record_failed_run({'runs': []}, out, 'mobile', 1, RuntimeError('timeout'))
            saved = json.loads((out / 'summary.json').read_text(encoding='utf-8'))
            failure = saved['failed_runs'][0]
            self.assertEqual(failure['error'], 'timeout')
            self.assertIn('result_read_error', failure)


if __name__ == '__main__':
    unittest.main()
