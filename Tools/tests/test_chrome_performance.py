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
            self.assertEqual(len(a), 6)
            for left, right in zip(a, b):
                self.assertEqual(left['html_sha256'], right['html_sha256'])
                self.assertEqual(left['css_sha256'], right['css_sha256'])
                self.assertIn('id="target"', Path(left['html']).read_text())

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
