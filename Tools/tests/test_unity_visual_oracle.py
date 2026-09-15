"""The visual oracle produces fresh Chrome pairs, never retired C# baselines."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'hosts/unity'))
import goldens_from_unity as GOLDENS


class UnityVisualOracleTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.corpus = self.work / 'corpus'
        self.corpus.mkdir()
        self.html = self.corpus / 'demo.html'
        self.html.write_text('<p>Example</p>', encoding='utf-8')
        self.capture = Path(str(self.html) + '.chrome-layout.json')
        self.capture.write_text('{"width":800,"height":600}', encoding='utf-8')
        self.out = self.work / 'out'

    def run_tool(self, *extra):
        return GOLDENS.main([str(self.corpus), '--out', str(self.out), *extra])

    def unity(self, unity, project, manifest, results, log):
        self.assertEqual(project, ROOT)
        row = manifest.read_text().strip().split('\t')
        self.assertEqual(row[2:4], ['800', '600'])
        self.assertEqual(row[5], 'inter')
        self.assertIn('animation:none!important', Path(row[1]).read_text())
        Path(row[4]).write_text('{"elements":[]}')
        Path(row[6]).write_bytes(b'new Unity image')

    def browser(self, command, **kwargs):
        rows = json.loads(Path(command[2]).read_text())
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]['html'], str(self.html))
        self.assertEqual((rows[0]['width'], rows[0]['height']), (800, 600))
        Path(rows[0]['png']).write_bytes(b'new Chrome image')
        Path(command[3]).write_text(json.dumps({'browser': 'Chrome/test', 'captures': rows}))
        return subprocess.CompletedProcess(command, 0, 'captured\n', '')

    def test_capture_pairs_preserve_source_captures_and_do_not_claim_pixel_pass(self):
        original = self.capture.read_bytes()
        with patch.object(GOLDENS, 'run_unity_manifest', side_effect=self.unity), \
                patch.object(GOLDENS.subprocess, 'run', side_effect=self.browser):
            self.assertEqual(self.run_tool(), 0)
        report = json.loads((self.out / 'report.json').read_text())
        self.assertEqual(report['comparison'], 'visual review required')
        self.assertEqual(report['browser'], 'Chrome/test')
        self.assertEqual(len(report['pairs']), 1)
        self.assertEqual(self.capture.read_bytes(), original)
        self.assertFalse(Path(str(self.html) + '.chrome.png').exists())

    def test_no_matching_cases_is_an_error(self):
        self.assertEqual(self.run_tool('--only', 'does-not-exist'), 2)

    def test_invalid_viewport_is_an_error(self):
        self.capture.write_text('{"width":0,"height":600}')
        self.assertEqual(self.run_tool(), 2)

    def test_reuse_rejects_modified_html(self):
        with patch.object(GOLDENS, 'run_unity_manifest', side_effect=self.unity), \
                patch.object(GOLDENS.subprocess, 'run', side_effect=self.browser):
            self.assertEqual(self.run_tool(), 0)
        self.html.write_text('<p>Changed</p>')
        with patch.object(GOLDENS.subprocess, 'run') as browser:
            self.assertEqual(self.run_tool('--skip-unity'), 1)
            browser.assert_not_called()

    def test_old_browser_receipt_cannot_mask_failed_capture(self):
        with patch.object(GOLDENS, 'run_unity_manifest', side_effect=self.unity), \
                patch.object(GOLDENS.subprocess, 'run', side_effect=self.browser):
            self.assertEqual(self.run_tool(), 0)
        with patch.object(GOLDENS.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')):
            self.assertEqual(self.run_tool('--skip-unity'), 1)
        self.assertFalse((self.out / 'chrome-receipt.json').exists())
        self.assertTrue(list(self.out.glob('chrome-receipt.json.previous-*')))


if __name__ == '__main__':
    unittest.main()
