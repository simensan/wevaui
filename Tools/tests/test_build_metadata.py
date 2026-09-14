"""Build receipts must identify the checkout and the host's actual inputs."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


class BuildMetadataTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.source = self.work / 'checkout'
        paths = [
            'CMakeLists.txt', 'libweva/CMakeLists.txt', 'libweva/src/core.cpp',
            'third_party/icu/CMakeLists.txt', 'hosts/godot/CMakeLists.txt',
            'hosts/godot/src/host.cpp', 'hosts/godot/build_metadata.py',
            'hosts/unity/CMakeLists.txt', 'hosts/unity/src/plugin.cpp',
            'hosts/unity/build_metadata.py', 'hosts/unity/gen_exports.py',
            'libweva/include/weva_c.h',
        ]
        for name in paths:
            path = self.source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'fixture\n')
        (self.source / 'libweva/include/weva_c.h').write_bytes(
            b'#define WEVA_ABI_VERSION_MAJOR 0\n#define WEVA_ABI_VERSION_MINOR 42\n')
        self.cpp = self.work / 'godot-cpp'
        for name in ['CMakeLists.txt', 'cmake/input', 'include/input', 'src/input',
                     'gdextension/input', 'tools/input', 'binding_generator.py',
                     'build_profile.py', 'doc_source_generator.py',
                     'make_interface_header.py', 'LICENSE.md']:
            path = self.cpp / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'dependency\n')
        self.library = self.work / 'plugin.bin'
        self.library.write_bytes(b'linked-library')
        self.git('init', '-q')
        self.git('add', '.')
        self.git('-c', 'user.name=Metadata Test', '-c', 'user.email=test@example.invalid',
                 '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture')

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.source), *args], text=True,
                                       stderr=subprocess.PIPE).strip()

    def receipt(self, host):
        command = [sys.executable, str(ROOT / f'hosts/{host}/build_metadata.py'),
                   '--source-dir', str(self.source), '--library', str(self.library),
                   '--configuration', 'Release', '--compiler', 'fixture',
                   '--compiler-version', '1', '--cmake-version', '3.20', '--sanitizers', 'OFF']
        if host == 'godot':
            command += ['--godot-cpp-dir', str(self.cpp), '--godot-api', '4.7', '--precision', 'single']
        subprocess.run(command, check=True, capture_output=True, text=True, cwd=self.work)
        return json.loads(Path(str(self.library) + '.build.json').read_text())

    def test_flat_checkout_records_its_own_revision_and_dirty_state(self):
        revision = self.git('rev-parse', 'HEAD')
        for host in ['godot', 'unity']:
            with self.subTest(host=host):
                source = self.receipt(host)['source']
                self.assertEqual(source['revision'], revision)
                self.assertIs(source['dirty'], False)
        (self.source / 'libweva/src/core.cpp').write_bytes(b'changed core\n')
        for host in ['godot', 'unity']:
            with self.subTest(host=host, dirty=True):
                self.assertIs(self.receipt(host)['source']['dirty'], True)

    def test_unity_receipt_tracks_its_own_native_sources_and_export_generator(self):
        unity = self.receipt('unity')['source']['sha256']
        godot = self.receipt('godot')['source']['sha256']
        for name in ['hosts/unity/src/plugin.cpp', 'hosts/unity/CMakeLists.txt',
                     'hosts/unity/gen_exports.py', 'hosts/unity/build_metadata.py']:
            with self.subTest(input=name):
                (self.source / name).write_bytes(b'changed build input\n')
                changed = self.receipt('unity')['source']['sha256']
                self.assertNotEqual(changed, unity)
                self.assertEqual(self.receipt('godot')['source']['sha256'], godot)
                unity = changed


if __name__ == '__main__':
    unittest.main()
