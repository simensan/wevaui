import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / 'hosts/godot'
sys.path.insert(0, str(HOST))
import build_metadata
import package_addon
import check_export

spec = importlib.util.spec_from_file_location('oracle_summary', ROOT / 'tools/check_oracle_summary.py')
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)


class OracleGateTests(unittest.TestCase):
    def test_complete_run(self):
        self.assertTrue(oracle.validate('45/47 agree, 0 differ, 2 reference bugs, 0 errored', 0, 47))

    def test_crash_after_success(self):
        self.assertFalse(oracle.validate('47/47 agree, 0 differ, 0 reference bugs, 0 errored', 139, 47))

    def test_errors_cannot_hide_behind_zero_differences(self):
        self.assertFalse(oracle.validate('46/47 agree, 0 differ, 0 reference bugs, 1 errored', 1, 47))

    def test_empty_and_incomplete_runs(self):
        for text, expected in [('0/0 agree, 0 differ, 0 reference bugs, 0 errored', 0),
                               ('1/1 agree, 0 differ, 0 reference bugs, 0 errored', 47),
                               ('46/47 agree, 0 differ, 0 reference bugs, 0 errored', 47)]:
            self.assertFalse(oracle.validate(text, 0, expected))

    def test_release_rejects_development_allowance(self):
        log = '44/47 agree, 3 differ, 0 reference bugs, 0 errored'
        self.assertTrue(oracle.validate(log, 1, 47, 3))
        self.assertFalse(oracle.validate(log, 1, 47))


class ExportGateTests(unittest.TestCase):
    def test_engine_error_fails_even_after_expected_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.CompletedProcess(['godot'], 0, 'expected summary\nERROR: invalid resource\n')
            with patch.object(check_export.subprocess, 'run', return_value=result):
                with self.assertRaises(RuntimeError):
                    check_export.run(['godot'], Path(directory), 'expected summary')
            self.assertEqual((Path(directory) / 'verification-logs/000.output.log').read_text(), result.stdout)

    def test_timeout_preserves_partial_output(self):
        with tempfile.TemporaryDirectory() as directory:
            error = subprocess.TimeoutExpired(['godot'], 120, output=b'partial engine output\n')
            with patch.object(check_export.subprocess, 'run', side_effect=error):
                with self.assertRaises(subprocess.TimeoutExpired):
                    check_export.run(['godot'], Path(directory))
            self.assertEqual((Path(directory) / 'verification-logs/000.output.log').read_text(),
                             'partial engine output\n')


class MetadataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.library = self.root / 'weva_godot.dll'
        self.library.write_bytes(b'linked-library')
        self.metadata = {
            'schema': 1, 'library_sha256': build_metadata.sha256(self.library),
            'source': {'sha256': 'source'}, 'godot_cpp': {'sha256': 'dependency'},
            'configuration': 'Release', 'sanitizers': False, 'godot_api': '4.7',
            'precision': 'single', 'compiler': {'id': 'MSVC', 'version': '19.44'},
        }
        self.write_metadata()

    def write_metadata(self):
        Path(str(self.library) + '.build.json').write_text(json.dumps(self.metadata))

    def validate(self):
        with patch.object(build_metadata, 'source_hash', return_value='source'), \
             patch.object(build_metadata, 'godot_cpp_hash', return_value='dependency'):
            return build_metadata.validate_metadata(self.library, self.root, self.root)

    def test_matching_library(self):
        self.assertEqual(self.validate(), self.metadata)

    def test_library_tampering(self):
        self.library.write_bytes(b'different-library')
        with self.assertRaisesRegex(ValueError, 'does not match'):
            self.validate()

    def test_stale_source(self):
        self.metadata['source']['sha256'] = 'older-source'
        self.write_metadata()
        with self.assertRaisesRegex(ValueError, 'different source'):
            self.validate()

    def test_dependency_mismatch(self):
        self.metadata['godot_cpp']['sha256'] = 'other-dependency'
        self.write_metadata()
        with self.assertRaisesRegex(ValueError, 'godot-cpp inputs differ'):
            self.validate()

    def test_instrumented_library(self):
        self.metadata['sanitizers'] = True
        self.write_metadata()
        with self.assertRaisesRegex(ValueError, 'Instrumented'):
            self.validate()

    def test_incompatible_api_and_precision(self):
        for key, value in [('godot_api', '4.6'), ('precision', 'double'), ('configuration', 'Debug')]:
            with self.subTest(key=key):
                original = self.metadata[key]
                self.metadata[key] = value
                self.write_metadata()
                with self.assertRaises(ValueError):
                    self.validate()
                self.metadata[key] = original

    def test_missing_metadata(self):
        Path(str(self.library) + '.build.json').unlink()
        with self.assertRaisesRegex(ValueError, 'Missing build metadata'):
            self.validate()

    def test_input_hash_includes_file_names_and_contents(self):
        directory = self.root / 'input'
        directory.mkdir()
        path = directory / 'source.cpp'
        path.write_bytes(b'one')
        first = build_metadata.tree_hash(self.root, ['input'])
        path.write_bytes(b'two')
        second = build_metadata.tree_hash(self.root, ['input'])
        self.assertNotEqual(first, second)
        path.rename(directory / 'other.cpp')
        self.assertNotEqual(second, build_metadata.tree_hash(self.root, ['input']))

    def test_executable_is_not_a_shared_library(self):
        # Valid architecture/magic alone used to accept an executable as an addon.
        pe = bytearray(128)
        pe[:2] = b'MZ'
        struct.pack_into('<I', pe, 60, 64)
        pe[64:70] = b'PE\0\0\x64\x86'
        struct.pack_into('<H', pe, 88, 0x20b)
        self.library.write_bytes(pe)
        with self.assertRaises(ValueError):
            package_addon.verify_library(self.library, 'windows')
        struct.pack_into('<H', pe, 86, 0x2000)
        self.library.write_bytes(pe)
        self.assertEqual(package_addon.verify_library(self.library, 'windows'), pe)
        elf = bytearray(64)
        elf[:7] = b'\x7fELF\x02\x01\x01'
        struct.pack_into('<HH', elf, 16, 2, 62)
        self.library.write_bytes(elf)
        with self.assertRaises(ValueError):
            package_addon.verify_library(self.library, 'linux')
        struct.pack_into('<H', elf, 16, 3)
        self.library.write_bytes(elf)
        self.assertEqual(package_addon.verify_library(self.library, 'linux'), elf)


@unittest.skipIf(os.name == 'nt' or not shutil.which('bash'), 'Bash integration runs on Linux')
class ComparisonGateTests(unittest.TestCase):
    def run_comparison(self, script, mode):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'sample.html').write_text('<p>fixture</p>')
            command = root / 'timeout'
            command.write_text('#!/bin/sh\n'
                               'if [ "$PROBE_MODE" != missing ]; then\n'
                               '  echo "structural 0 px (0.00%)"\n'
                               '  echo "over tolerance 0 px (0.00%)"\nfi\n'
                               '[ "$PROBE_MODE" != crash ] || exit 139\n')
            command.chmod(0o755)
            env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'], PROBE_MODE=mode)
            return subprocess.run(['bash', str(HOST / script), str(root)], env=env,
                                  capture_output=True, text=True, timeout=10).returncode

    def test_crashes_after_good_metrics_fail(self):
        for script in ('compare_all.sh', 'compare_live.sh'):
            with self.subTest(script=script):
                self.assertNotEqual(self.run_comparison(script, 'crash'), 0)

    def test_missing_metrics_fail(self):
        for script in ('compare_all.sh', 'compare_live.sh'):
            with self.subTest(script=script):
                self.assertNotEqual(self.run_comparison(script, 'missing'), 0)

    def test_completed_comparisons_pass(self):
        for script in ('compare_all.sh', 'compare_live.sh'):
            with self.subTest(script=script):
                self.assertEqual(self.run_comparison(script, 'pass'), 0)


if __name__ == '__main__':
    unittest.main()
