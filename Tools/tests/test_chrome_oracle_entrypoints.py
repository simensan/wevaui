"""The public oracle commands require real Chrome coverage, even on empty input."""
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools/oracle'))
import chrome_sweep as SWEEP
import run_oracle as ENTRY


class ChromeOracleEntrypointTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.html = self.work / 'case.html'
        self.html.write_text('<p>Example</p>')
        self.args = [str(self.work), '--weva-dump', 'core', '--out-dir', str(self.work / 'out')]

    def test_legacy_entrypoint_uses_browser_metrics_and_enforces_ceiling(self):
        with patch.object(SWEEP, 'compare', return_value=('case', 'DIFF', ['p .x (2.0px)'], 0)) as compare:
            self.assertEqual(ENTRY.main(self.args), 1)
            self.assertIs(compare.call_args.args[-1], True)

    def test_empty_corpus_or_filter_never_passes(self):
        with patch.object(SWEEP, 'compare') as compare:
            self.assertEqual(ENTRY.main(self.args + ['--only', 'absent']), 2)
            self.html.unlink()
            self.assertEqual(ENTRY.main(self.args), 2)
            compare.assert_not_called()

    def test_missing_chrome_capture_fails(self):
        with patch.object(SWEEP, 'compare', return_value=None):
            self.assertEqual(ENTRY.main(self.args), 1)

    def test_core_failure_fails(self):
        with patch.object(SWEEP, 'compare', return_value=('case', 'CRASH', ['tool failed'], 0)):
            self.assertEqual(ENTRY.main(self.args), 1)

    def test_geometry_ceiling_distinguishes_rounding_from_divergence(self):
        for delta, expected in [(0.2, 0), (2.0, 1)]:
            with self.subTest(delta=delta):
                with patch.object(SWEEP, 'compare', return_value=('case', 'DIFF', [f'p .x ({delta}px)'], 0)):
                    self.assertEqual(ENTRY.main(self.args), expected)

    def test_unmatched_element_fails_even_without_geometry_delta(self):
        with patch.object(SWEEP, 'compare', return_value=('case', 'DIFF', ['Unmatched Chrome element p'], 1)):
            self.assertEqual(ENTRY.main(self.args), 1)


if __name__ == '__main__':
    unittest.main()
