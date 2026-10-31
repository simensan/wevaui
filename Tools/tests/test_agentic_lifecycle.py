"""Guards that prevent a stopped or contaminated run being reported as a soak."""
import importlib.util
from pathlib import Path
import unittest
import subprocess
import sys

spec = importlib.util.spec_from_file_location('agentic_runtime_audit', Path(__file__).resolve().parents[1] / 'performance/run_agentic_runtime_audit.py')
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class LifecycleGuards(unittest.TestCase):
    def test_idle_stays_valid_without_reload_progress(self):
        self.assertEqual(audit.check_lifecycle_state({'audit_mode': 4, 'audit_reloads': 0}, 'idle', 0, 0, 600), (0, 0))

    def test_idle_rejects_unexpected_reload(self):
        with self.assertRaisesRegex(RuntimeError, 'unexpectedly reloaded'):
            audit.check_lifecycle_state({'audit_mode': 4, 'audit_reloads': 1}, 'idle', 0, 0, 10)

    def test_reload_progress_resets_deadline(self):
        self.assertEqual(audit.check_lifecycle_state({'audit_mode': 5, 'audit_reloads': 12}, 'reload', 10, 0, 25), (12, 25))

    def test_reload_rejects_stall(self):
        with self.assertRaisesRegex(RuntimeError, 'stopped making progress'):
            audit.check_lifecycle_state({'audit_mode': 5, 'audit_reloads': 10}, 'reload', 10, 0, 31)

    def test_rejects_changed_mode(self):
        for mode, actual in [('idle', 5), ('reload', 4)]:
            with self.subTest(mode=mode), self.assertRaises(RuntimeError):
                audit.check_lifecycle_state({'audit_mode': actual, 'audit_reloads': 0}, mode, 0, 0, 1)

    def test_endpoint_sampling_requires_positive_duration(self):
        result = subprocess.run([sys.executable, str(Path(audit.__file__)), '--project', '.', '--engine', 'unused=.', '--output', 'unused-audit-output',
                                 '--lifecycle-observation', 'endpoints'], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn('endpoints requires a positive lifecycle duration', result.stderr)

    def test_rejects_counter_reset(self):
        with self.assertRaises(RuntimeError):
            audit.check_lifecycle_state({'audit_mode': 5, 'audit_reloads': 0}, 'reload', 10, 0, 1)


if __name__ == '__main__':
    unittest.main()
