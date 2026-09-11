from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import check
from check_exports import node_probe


class NativeChecks(unittest.TestCase):
    def run_native(self, log, mode='release', code=0):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(check.subprocess, 'run', return_value=subprocess.CompletedProcess([], code, log, '')) as run:
                result = check.check_case(root/'game.exe', None, root, 'emoji33', {}, native_mode=mode)
                command = run.call_args.args[0]
                self.assertNotIn('--path', command)
                self.assertNotIn('--script', command)
                self.assertEqual(run.call_args.kwargs['cwd'], root)
            return result

    def test_release_requires_matching_build_mode_and_complete_case(self):
        case = 'emoji33: 99 glyphs; 3 checks, 0 failures\n'
        self.assertTrue(self.run_native('Template debug: false\n' + case)['passed'])
        for prefix in ['', 'Template debug: true\n']:
            self.assertFalse(self.run_native(prefix + case)['passed'])
        self.assertFalse(self.run_native('Template debug: false\n' + case, code=1)['passed'])

    def test_debug_does_not_accept_release_template(self):
        case = 'emoji33: 99 glyphs; 3 checks, 0 failures\n'
        self.assertTrue(self.run_native('Template debug: true\n' + case, mode='debug')['passed'])
        self.assertFalse(self.run_native('Template debug: false\n' + case, mode='debug')['passed'])

    def test_export_wrapper_preserves_case_and_icu_checks(self):
        source = Path(__file__).with_name('probe.gd').read_text(encoding='utf-8')
        wrapped = node_probe(source)
        body = source[source.index('func run_probe()'):]
        self.assertEqual(wrapped[wrapped.index('func run_probe()'):], body.replace('quit(', 'get_tree().quit('))
        self.assertIn('extends Node', wrapped)
        with self.assertRaises(ValueError):
            node_probe('unexpected replacement probe')


if __name__ == '__main__':
    unittest.main()
