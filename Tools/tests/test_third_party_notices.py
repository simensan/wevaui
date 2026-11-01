"""All distributed hosts must carry the exact canonical dependency notices."""
import copy
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Tools'))
sys.path.insert(0, str(ROOT / 'hosts/godot'))
import third_party_notices as notices
import package_addon

spec = importlib.util.spec_from_file_location('unity_notices', ROOT / 'hosts/unity/bundle_notices.py')
unity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(unity)


class ThirdPartyNoticesTests(unittest.TestCase):
    def test_all_vendored_license_texts_are_registered(self):
        registered = {n['source'] for n in notices.inventory()['notices']}
        actual = {p.relative_to(ROOT).as_posix() for p in (ROOT / 'third_party').glob('*/LICENSE*') if p.is_file()}
        self.assertEqual(registered, actual)

    def test_unity_notices_include_every_exact_license(self):
        rendered = unity.render()
        self.assertEqual(unity.OUT.read_text(encoding='utf-8'), rendered)
        for notice in notices.inventory()['notices']:
            self.assertIn((ROOT / notice['source']).read_text(encoding='utf-8').rstrip('\n'), rendered)
        self.assertIn('stb_image JPEG decoder', rendered)

    def test_invalid_inventory_fails_before_files_are_packaged(self):
        original = notices.inventory()
        cases = []
        changed = copy.deepcopy(original); changed['notices'][1]['native'] = changed['notices'][0]['native']; cases.append(changed)
        changed = copy.deepcopy(original); changed['notices'][0]['godot'] = 'folder' + chr(92) + 'name'; cases.append(changed)
        changed = copy.deepcopy(original); changed['dependencies']['stb']['license'] = 'missing.txt'; cases.append(changed)
        changed = copy.deepcopy(original); del changed['dependencies']['stb']; cases.append(changed)
        for changed in cases:
            with self.subTest(changed=changed['notices'][0]['godot']), patch.object(notices.json, 'loads', return_value=changed):
                with self.assertRaises(ValueError): notices.inventory()

    def test_godot_archive_contains_notices_and_matching_sbom(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory); cpp = work / 'godot-cpp'; cpp.mkdir(); (cpp / 'LICENSE.md').write_text('binding license')
            elf = bytearray(64); elf[:7] = b'\x7fELF\x02\x01\x01'; struct.pack_into('<HH', elf, 16, 3, 62)
            library = work / 'libweva_godot.so'; library.write_bytes(elf)
            archives = []
            for name in ['one.zip', 'two.zip']:
                output = work / name
                with patch.object(sys, 'argv', ['package_addon.py', '--linux-library', str(library), '--godot-cpp-dir', str(cpp), '--version', '0.1.0-preview.1', '--output', str(output)]), patch.object(package_addon, 'validate_metadata', return_value={'fixture': True}):
                    package_addon.main()
                archives.append(output.read_bytes())
                with zipfile.ZipFile(output) as archive:
                    for filename, content in notices.godot_notices().items():
                        self.assertEqual(archive.read('addons/weva/' + filename), content)
                    sbom = json.loads(archive.read('addons/weva/build.json'))['dependencies']
                    self.assertEqual(sbom, notices.godot_dependencies())
                    self.assertEqual(sbom['stb']['license'], 'STB_LICENSE.txt')
            self.assertEqual(*archives, 'identical package inputs must remain deterministic')


if __name__ == '__main__':
    unittest.main()
