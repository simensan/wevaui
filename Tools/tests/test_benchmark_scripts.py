"""Benchmark wrappers must not report success after dropping failed samples."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipIf(os.name == 'nt', 'Bash benchmark wrappers run in WSL/Linux')
class BenchmarkScriptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.corpus = self.work / 'corpus'
        self.corpus.mkdir()
        (self.corpus / 'sample.html').write_text('<div>Sample</div>')
        self.scratch = self.work / 'scratch'
        self.scratch.mkdir()
        self.build = self.work / 'build'
        self.binary = self.build / 'Tools/weva_bench/weva_bench'
        self.binary.parent.mkdir(parents=True)
        self.binary.write_text('''#!/usr/bin/env bash
case "${BENCH_MODE:-ok}" in
    fail) echo 'sample 3 boxes best 1.000 ms'; echo 'benchmark failed' >&2; exit 7 ;;
    empty) echo 'no measurement'; exit 0 ;;
esac
echo 'sample 3 boxes best 1.000 ms mean 1.000 ms steady-state allocations 2 (3 bytes)'
''')
        self.binary.chmod(0o755)

    def run_script(self, script, *args, mode='ok'):
        env = dict(os.environ, WEVA_BUILD_GCC=str(self.build), WEVA_CORPUS=str(self.corpus),
                   TMPDIR=str(self.scratch), BENCH_MODE=mode)
        env.pop('WEVA_BENCH', None)
        return subprocess.run(['bash', str(ROOT / 'Tools' / script), *args],
                              env=env, capture_output=True, text=True)

    def test_valid_default_and_ab_runs_use_current_build_directory(self):
        for script in ('layoutbench.sh', 'flipbench.sh'):
            for args in [('1', '1'), ('--ab', str(self.binary), str(self.binary), '1', '1')]:
                with self.subTest(script=script, args=args):
                    result = self.run_script(script, *args)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn('sample', result.stdout)

    def test_missing_ab_binary_fails(self):
        for script in ('layoutbench.sh', 'flipbench.sh'):
            with self.subTest(script=script):
                result = self.run_script(script, '--ab', str(self.work / 'missing'), str(self.binary), '1', '1')
                self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_failed_process_with_a_metric_fails_and_preserves_evidence(self):
        for script in ('layoutbench.sh', 'flipbench.sh'):
            with self.subTest(script=script):
                result = self.run_script(script, '--ab', str(self.binary), str(self.binary), '1', '1', mode='fail')
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn('evidence', result.stderr)
        logs = list(self.scratch.rglob('errors.log'))
        self.assertEqual(len(logs), 2)
        self.assertTrue(all('benchmark failed' in p.read_text() for p in logs))

    def test_missing_metric_fails(self):
        for script in ('layoutbench.sh', 'flipbench.sh'):
            with self.subTest(script=script):
                result = self.run_script(script, '--ab', str(self.binary), str(self.binary), '1', '1', mode='empty')
                self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_empty_corpus_fails(self):
        (self.corpus / 'sample.html').unlink()
        for script in ('layoutbench.sh', 'flipbench.sh'):
            with self.subTest(script=script):
                result = self.run_script(script, '--ab', str(self.binary), str(self.binary), '1', '1')
                self.assertNotEqual(result.returncode, 0, result.stdout)

    def test_zero_iterations_fail(self):
        for script in ('layoutbench.sh', 'flipbench.sh'):
            for sweeps, passes in [('0', '1'), ('1', '0')]:
                with self.subTest(script=script, sweeps=sweeps, passes=passes):
                    result = self.run_script(script, '--ab', str(self.binary), str(self.binary), sweeps, passes)
                    self.assertNotEqual(result.returncode, 0, result.stdout)


if __name__ == '__main__':
    unittest.main()
