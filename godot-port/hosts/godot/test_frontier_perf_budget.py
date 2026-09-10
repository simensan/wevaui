import copy
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from frontier_perf_budget import evaluate


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.budget = {'schema': 1, 'name': 'test', 'renderers': ['mobile'],
                       'minimum_runs': 2, 'minimum_frames': 600, 'minimum_warmups': 120,
                       'scope': {'resolution': [1280, 720], 'world': 'static', 'automatic': False, 'adapter': 'test GPU'},
                       'limits_ms': {'settings_toggle': {'changed_api_cpu': 3.0}}}
        run = {'renderer': 'mobile', 'index': 1, 'result': {'renderer': 'mobile', 'adapter': 'test GPU',
               'passed': True, 'debug_build': False, 'viewport': [1280, 720], 'world': 'static', 'auto_update': False,
               'results': [{'workload': 'settings_toggle', 'frames': 600, 'warmups': 120,
                            'changed_api_cpu': {'samples': 100, 'p95_ms': 2.0}}]}}
        second = copy.deepcopy(run)
        second['index'] = 2
        self.report = {'passed': True, 'frames': 600, 'warmups': 120, 'resolution': [1280, 720],
                       'world': 'static', 'automatic': False, 'runs': [run, second]}

    def result(self):
        return evaluate(self.report, self.budget)

    def timing(self):
        return self.report['runs'][0]['result']['results'][0]['changed_api_cpu']

    def test_equal_budget_passes(self):
        self.timing()['p95_ms'] = 3.0
        self.assertTrue(self.result()['passed'])

    def test_outlier_is_not_averaged_away(self):
        self.timing()['p95_ms'] = 5.4
        self.assertFalse(self.result()['passed'])
        self.assertIn('Budget exceeded', self.result()['errors'][0])

    def test_invalid_measurements_fail(self):
        for value in (None, math.nan, math.inf, -1, True, '2'):
            with self.subTest(value=value):
                self.timing()['p95_ms'] = value
                self.assertFalse(self.result()['passed'])
                json.dumps(self.result(), allow_nan=False)

    def test_insufficient_samples_fail(self):
        for count in (0, 1, 9, None, True):
            self.timing()['samples'] = count
            self.assertFalse(self.result()['passed'])

    def test_small_population_is_visible_without_regrading(self):
        for count, rank in ((10, 10), (100, 95)):
            self.timing()['samples'] = count
            result = self.result()
            self.assertTrue(result['passed'])
            check = result['checks'][0]
            self.assertEqual((check['samples'], check['minimum_samples'], check['p95_rank']),
                             (count, 10, rank))
        self.timing()['samples'] = None
        result = self.result()
        self.assertFalse(result['passed'])
        self.assertIsNone(result['checks'][0]['samples'])
        self.assertIsNone(result['checks'][0]['p95_rank'])
        json.dumps(result, allow_nan=False)

    def test_missing_case_fails(self):
        self.report['runs'][0]['result']['results'] = []
        self.assertFalse(self.result()['passed'])

    def test_missing_renderer_fails(self):
        self.budget['renderers'].append('gl_compatibility')
        self.assertFalse(self.result()['passed'])

    def test_insufficient_or_duplicate_runs_fail(self):
        self.report['runs'][1]['index'] = 1
        self.assertFalse(self.result()['passed'])
        self.report['runs'].pop()
        self.assertFalse(self.result()['passed'])

    def test_scope_and_release_are_required(self):
        for key, value in [('adapter', 'other GPU'), ('debug_build', True), ('passed', False),
                           ('viewport', [1920, 1080]), ('world', '3d'), ('auto_update', True), ('renderer', 'other')]:
            original = self.report['runs'][0]['result'][key]
            self.report['runs'][0]['result'][key] = value
            self.assertFalse(self.result()['passed'], key)
            self.report['runs'][0]['result'][key] = original

    def test_workload_length_is_checked(self):
        self.report['runs'][0]['result']['results'][0]['frames'] = 60
        self.assertFalse(self.result()['passed'])

    def test_unscoped_run_is_not_ignored(self):
        extra = copy.deepcopy(self.report['runs'][0])
        extra['renderer'] = 'unscoped'
        self.report['runs'].append(extra)
        self.assertFalse(self.result()['passed'])

    def test_report_length_cannot_overstate_samples(self):
        self.report['frames'] = 1200
        self.assertFalse(self.result()['passed'])

    def test_functional_failure_fails(self):
        self.report['passed'] = False
        self.assertFalse(self.result()['passed'])
        self.report['functional_passed'] = True
        self.assertTrue(self.result()['passed'])  # Regrade a previously failed timing profile.

    def test_invalid_budget_rejected(self):
        self.budget['limits_ms']['settings_toggle']['changed_api_cpu'] = math.nan
        with self.assertRaises(ValueError):
            self.result()

    def test_core_and_frame_budgets(self):
        for metric in ('core_cpu', 'changed_core_cpu', 'whole_frame', 'changed_whole_frame'):
            with self.subTest(metric=metric):
                self.setUp()
                self.budget['limits_ms'] = {'settings_toggle': {metric: 3.0}}
                for run in self.report['runs']:
                    run['result']['results'][0][metric] = {'samples': 600, 'p95_ms': 2.0}
                self.assertTrue(self.result()['passed'])
                timing = self.report['runs'][0]['result']['results'][0][metric]
                timing['samples'] = 10 if metric.startswith('changed_') else 599
                self.assertEqual(self.result()['passed'], metric.startswith('changed_'))
                timing['samples'] = 600
                timing['p95_ms'] = 4.0
                self.assertFalse(self.result()['passed'])
                del self.report['runs'][0]['result']['results'][0][metric]
                self.assertFalse(self.result()['passed'])

    def test_unknown_metric_rejected(self):
        self.budget['limits_ms'] = {'settings_toggle': {'unknown': 3.0}}
        with self.assertRaises(ValueError):
            self.result()

    def test_cli_failure_is_nonzero_and_preserves_report(self):
        self.timing()['p95_ms'] = 5.4
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory)
            (p/'report.json').write_text(json.dumps(self.report), encoding='utf-8')
            (p/'budget.json').write_text(json.dumps(self.budget), encoding='utf-8')
            result = subprocess.run([sys.executable, str(Path(__file__).with_name('frontier_perf_budget.py')),
                '--report', str(p/'report.json'), '--budget', str(p/'budget.json'), '--output', str(p/'out.json')], capture_output=True)
            self.assertEqual(result.returncode, 1)
            self.assertFalse(json.loads((p/'out.json').read_text(encoding='utf-8'))['passed'])


if __name__ == '__main__':
    unittest.main()
