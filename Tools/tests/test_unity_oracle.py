"""Unity oracle runs must exercise this checkout and reject stale evidence."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'hosts/unity'))
import oracle_from_unity as ORACLE
import oracle_support as SUPPORT


class UnityOracleTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.out = Path(temporary.name)
        self.dump = self.out / 'case.json'
        self.image = self.out / 'case.png'
        self.manifest = self.out / 'manifest.tsv'
        self.manifest.write_text('\t'.join(['case.html', '', '1280', '720', str(self.dump),
                                            'inter', str(self.image)]) + '\n', encoding='utf-8')
        self.results = self.out / 'results.xml'
        self.log = self.out / 'unity.log'

    def write_result(self, result='Passed', test_result='Passed', fullname=None):
        name = SUPPORT.MANIFEST_TEST if fullname is None else fullname
        self.results.write_text(
            f'<test-run result="{result}" failed="{0 if result == "Passed" else 1}">'
            f'<test-case fullname="{name}" result="{test_result}" /></test-run>', encoding='utf-8')

    def run_manifest(self):
        SUPPORT.run_unity_manifest('Unity', ROOT, self.manifest, self.results, self.log)

    def test_manifest_reaches_hidden_graphics_enabled_unity_and_exit_two_can_pass(self):
        def run(command, **kwargs):
            self.assertEqual(command[command.index('-projectPath') + 1], str(ROOT))
            self.assertNotIn('-quit', command)
            self.assertNotIn('-nographics', command)
            self.assertEqual(kwargs['env']['WEVA_NATIVE_DUMP_MANIFEST'], str(self.manifest))
            if sys.platform == 'win32':
                self.assertEqual(kwargs['startupinfo'].wShowWindow, subprocess.SW_HIDE)
            self.write_result()
            return subprocess.CompletedProcess(command, 2)
        with patch.object(SUPPORT.subprocess, 'run', side_effect=run):
            self.run_manifest()

    def test_old_xml_and_dumps_are_preserved_and_cannot_mask_missing_results(self):
        self.write_result()
        for path in (self.dump, self.image, self.image.with_suffix('.ppm'), self.log):
            path.write_text('old evidence')
        with patch.object(SUPPORT.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)):
            with self.assertRaisesRegex(RuntimeError, 'fresh results'):
                self.run_manifest()
        for path in (self.results, self.dump, self.image, self.image.with_suffix('.ppm'), self.log):
            self.assertFalse(path.exists())
            self.assertEqual(len(list(self.out.glob(path.name + '.previous-*'))), 1)

    def test_failed_inconclusive_or_wrong_test_is_rejected(self):
        for result, test_result, fullname in [('Failed', 'Failed', None),
                                              ('Passed', 'Inconclusive', None),
                                              ('Passed', 'Passed', 'unrelated.Test')]:
            with self.subTest(result=result, test_result=test_result, fullname=fullname):
                def run(command, **kwargs):
                    self.write_result(result, test_result, fullname)
                    return subprocess.CompletedProcess(command, 0)
                with patch.object(SUPPORT.subprocess, 'run', side_effect=run):
                    with self.assertRaisesRegex(RuntimeError, 'did not pass'):
                        self.run_manifest()

    def test_crashed_process_cannot_pass_even_with_xml(self):
        def run(command, **kwargs):
            self.write_result()
            return subprocess.CompletedProcess(command, 9)
        with patch.object(SUPPORT.subprocess, 'run', side_effect=run):
            with self.assertRaisesRegex(RuntimeError, 'exited 9'):
                self.run_manifest()

    def test_wsl_paths_are_arguments_not_shell_code_and_tilde_expands(self):
        html = self.out / "case's $(literal).html"
        css = self.out / 'some style.css'
        with patch.object(ORACLE.subprocess, 'check_output', return_value='/home/test\n'), \
                patch.object(ORACLE.subprocess, 'run') as run:
            ORACLE.run_weva_dump('wsl:~/build/tool', html, css, 80, 60, self.dump)
        command = run.call_args.args[0]
        self.assertEqual(command, ['wsl', '--exec', '/home/test/build/tool',
                                  ORACLE.to_wsl(html), '80', '60', ORACLE.to_wsl(self.dump),
                                  ORACLE.to_wsl(css)])

    def test_core_output_must_be_fresh(self):
        self.dump.write_text('old core dump')
        with patch.object(ORACLE.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)):
            ORACLE.run_weva_dump('weva_dump', self.out / 'case.html', None, 80, 60, self.dump)
        self.assertFalse(self.dump.exists())
        self.assertEqual(len(list(self.out.glob('case.json.previous-*'))), 1)

    def test_snapshot_css_preserves_author_rules_and_stops_motion(self):
        css = self.out / 'author.css'
        css.write_text('.panel { color: red; animation: fade 1s; }')
        frozen = SUPPORT.frozen_stylesheet(css)
        self.assertTrue(frozen.startswith(css.read_text()))
        self.assertTrue(frozen.endswith(SUPPORT.FREEZE_MOTION + '\n'))
        self.assertIn('animation:none!important', frozen)
        self.assertIn('transition:none!important', frozen)
        self.assertEqual(SUPPORT.frozen_stylesheet(None), '\n' + SUPPORT.FREEZE_MOTION + '\n')

    def test_default_checkout_and_complete_run(self):
        self.assertEqual(SUPPORT.REPO, ROOT)
        corpus = self.out / 'corpus'
        corpus.mkdir()
        (corpus / 'one.html').write_text('<p>One</p>')
        data = {'elements': [{'tag': 'p', 'id': '', 'cls': '', 'depth': 0,
                              'x': 0, 'y': 0, 'w': 50, 'h': 20}]}
        def unity(unity, project, manifest, results, log):
            self.assertEqual(project, ROOT)
            fields = manifest.read_text().strip().split('\t')
            self.assertIn(SUPPORT.FREEZE_MOTION, Path(fields[1]).read_text())
            Path(fields[4]).write_text(json.dumps(data))
        def core(tool, html, css, width, height, out):
            out.write_text(json.dumps(data))
            return subprocess.CompletedProcess([], 0)
        with patch.object(ORACLE, 'run_unity_manifest', side_effect=unity), \
                patch.object(ORACLE, 'run_weva_dump', side_effect=core):
            self.assertEqual(ORACLE.main([str(corpus), '--weva-dump', 'core', '--out', str(self.out / 'run')]), 0)
            self.assertEqual(ORACLE.main([str(corpus), '--weva-dump', 'core', '--out', str(self.out / 'run'),
                                          '--skip-unity', '--width', '400']), 1)


if __name__ == '__main__':
    unittest.main()
