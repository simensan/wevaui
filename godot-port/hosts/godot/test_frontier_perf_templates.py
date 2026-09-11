import configparser
import json
from pathlib import Path
import tempfile
import unittest
from run_frontier_perf import release_template_override, sha


class TemplateSelectionTests(unittest.TestCase):
    def exercise(self, fail):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            preset = project / 'export_presets.cfg'
            original = b'; preserve comments and CRLF\r\n[preset.0]\r\nname="Windows Desktop"\r\n[preset.0.options]\r\ncustom_template/release="old.exe"\r\n'
            preset.write_bytes(original)
            template = project / 'custom template.exe'
            template.write_bytes(b'template identity')
            try:
                with release_template_override(project, 'Windows Desktop', template) as identity:
                    config = configparser.ConfigParser(interpolation=None)
                    config.read(preset, encoding='utf-8')
                    self.assertEqual(json.loads(config['preset.0.options']['custom_template/release']), template.as_posix())
                    self.assertEqual(identity['sha256'], sha(template))
                    self.assertEqual(identity['selection'], 'explicit')
                    if fail:
                        raise RuntimeError('export failed')
            except RuntimeError:
                self.assertTrue(fail)
            self.assertEqual(preset.read_bytes(), original)

    def test_explicit_template_and_exact_restoration(self):
        self.exercise(False)

    def test_failed_export_restores_original_presets(self):
        self.exercise(True)

    def test_default_is_explicitly_unverified(self):
        with tempfile.TemporaryDirectory() as directory:
            with release_template_override(Path(directory), 'Windows Desktop', None) as identity:
                self.assertEqual(identity, {'selection': 'project_or_godot_default', 'sha256': None})
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_missing_template_does_not_modify_project(self):
        with tempfile.TemporaryDirectory() as directory:
            project = Path(directory)
            with self.assertRaises(ValueError):
                with release_template_override(project, 'Windows Desktop', project / 'missing.exe'):
                    self.fail('Missing template accepted')
            self.assertEqual(list(project.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
