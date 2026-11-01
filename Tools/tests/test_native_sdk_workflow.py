"""Engine/dependency changes must exercise the distributable SDK build matrix."""
import ast
import fnmatch
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class NativeSdkWorkflowTests(unittest.TestCase):
    def test_sdk_inputs_trigger_native_qualification(self):
        source = (ROOT / '.github/workflows/native-sdk.yml').read_text()
        patterns = ast.literal_eval(re.search(r'paths:\s*(\[[^\]]+\])', source).group(1))
        for path in ['libweva/src/image_decode.cpp', 'libweva/include/weva_c.h',
                     'libweva/tests/test_image_decode.cpp', 'third_party/stb/stb_image.h',
                     'CMakeLists.txt', 'LICENSE.md', 'Tools/native/CMakeLists.txt',
                     'Tools/package_native.py', 'Tools/weva_bench/main.cpp',
                     'Tools/sanitizer_probe.cpp', 'Tools/check_sanitizer.cmake']:
            with self.subTest(path=path):
                self.assertTrue(any(fnmatch.fnmatchcase(path, p) for p in patterns), path)


if __name__ == '__main__':
    unittest.main()
