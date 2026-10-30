import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('comparison', ROOT / 'Tools/performance/compare_chrome.py')
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class ChromePerformanceTests(unittest.TestCase):
    def test_old_or_painting_binary_is_rejected(self):
        row = 'best 0.001 ms mean 0.003 ms  0 draws  0 textures'
        with self.assertRaises(ValueError):
            comparison.parse_native(row, geometry_only=True)
        with self.assertRaises(ValueError):
            comparison.parse_native('mutation-to-layout ' + row.replace('0 draws', '2 draws'), geometry_only=True)
        self.assertEqual(comparison.parse_native('mutation-to-layout ' + row, geometry_only=True)['mean_ms'], .003)

    def test_layout_mismatch_rejects_comparison(self):
        box = {'x': 0, 'y': 0, 'w': 10, 'h': 20}
        self.assertEqual(comparison.check_geometry([box], [box]), 0)
        for other in ([], [{**box, 'w': 11}]):
            with self.assertRaises(ValueError):
                comparison.check_geometry([box], other)

    def test_nonfinite_later_rectangle_rejects_comparison(self):
        box = {'x': 0, 'y': 0, 'w': 10, 'h': 20}
        with self.assertRaises(ValueError):
            comparison.check_geometry([box, box], [box, {**box, 'x': float('nan')}])

    def test_stale_initial_geometry_cannot_pass_mutation_checks(self):
        original = [{'x': 12, 'y': 0, 'w': 10, 'h': 20}]
        changed = [{**original[0], 'x': 11}]
        # The even final state matches initial geometry, but the odd state must
        # still be checked. This rejects a native path that ignores mutation.
        self.assertEqual(comparison.check_geometry(original, original), 0)
        with self.assertRaises(ValueError):
            comparison.check_mutated_geometry([original, original], [changed, original])
        with self.assertRaises(ValueError):
            comparison.check_mutated_geometry([original], [changed, original])
        self.assertEqual(comparison.check_mutated_geometry([changed, original], [changed, original]), 0)

    def test_missing_geometry_dump_is_fatal(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(FileNotFoundError):
                comparison.read_geometry(Path(directory) / 'missing.json')

    def test_native_failures_cannot_become_measurements(self):
        for output in ('', 'error: no target', 'best NaN ms mean NaN ms',
                       'best 2.0 ms mean 1.0 ms', 'best 1.0 ms mean 1.0 ms\nbest 1.0 ms mean 1.0 ms'):
            with self.subTest(output=output), self.assertRaises(ValueError):
                comparison.parse_native(output)

    def test_valid_native_row(self):
        self.assertEqual(comparison.parse_native('hud layout #target best 0.001 ms  mean 0.003 ms 2 draws'),
                         {'best_ms': .001, 'mean_ms': .003})

    def test_identical_fixture_bytes_across_runs(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            a, b = comparison.fixtures(Path(first)), comparison.fixtures(Path(second))
            self.assertEqual(len(a), 10)
            for left, right in zip(a, b):
                self.assertEqual(left['html_sha256'], right['html_sha256'])
                self.assertEqual(left['css_sha256'], right['css_sha256'])
                self.assertIn('id="target"', Path(left['html']).read_text())

    def test_original_fixtures_retain_baseline_hashes(self):
        expected = {
            'hud': ('732397a2d370ef9b7fc7c72f6bcee3d12a3261e904137ca15a4535de82826933',
                    'f700bdc9b8c559ebab5de24d67f44bcfc349530dd8dd63b7ed3077935c1f6bc4'),
            'inventory': ('168489137864305fd64b2734e385f5d55bae52a2de37f1b2f16953f69ec7a981',
                          '342126646f3393e3198fe3ada8d71db447dfde28a94438a7855739950b81c871'),
            'settings': ('ffb0257bba753133369788e6de9552ffb3787c03e76a2650a0f73cad6f66250d',
                         'f429f854b99068073ead64aadb9486c00300cdc68e9c2c8179f1b28ad6d6f947'),
        }
        with tempfile.TemporaryDirectory() as directory:
            jobs = comparison.fixtures(Path(directory))
            self.assertEqual([(j['name'], j['mode']) for j in jobs[:6]],
                             [(name, mode) for name in expected for mode in ('panel', 'leaf')])
            for job in jobs[:6]:
                self.assertEqual((job['html_sha256'], job['css_sha256']), expected[job['name']])

    def test_failed_child_preserves_log_and_raises(self):
        import subprocess
        import sys
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / 'failed.log'
            with self.assertRaises(subprocess.CalledProcessError):
                comparison.run([sys.executable, '-c', 'print("failed evidence"); raise SystemExit(3)'], log)
            self.assertIn('failed evidence', log.read_text())


if __name__ == '__main__':
    unittest.main()
