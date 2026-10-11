"""The documented corpus harvester must run and preserve Unicode fixtures."""
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class HarvestCorpusTests(unittest.TestCase):
    def test_cli_harvests_void_elements_unicode_and_reused_styles(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'source'
            source.mkdir()
            (source / 'Fixture.cs').write_text(
                'var css = @"input { color: red; }";\n'
                'var first = @"<input value=""旅人"">";\n'
                'var second = @"<p>café</p>";\n', encoding='utf-8')
            output = root / 'corpus'
            result = subprocess.run(
                [sys.executable, str(ROOT / 'Tools/oracle/harvest_corpus.py'),
                 str(source), '--out', str(output), '--min-elements', '1'],
                capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual((output / 'Fixture-00.html').read_bytes(),
                             '<input value="旅人">'.encode('utf-8'))
            self.assertEqual((output / 'Fixture-01.html').read_bytes(),
                             '<p>café</p>'.encode('utf-8'))
            for index in ['00', '01']:
                self.assertEqual((output / ('Fixture-' + index + '.css')).read_text(),
                                 'input { color: red; }')


if __name__ == '__main__':
    unittest.main()
