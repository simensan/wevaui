"""Native SDK packaging checks; no compiler or remote downloads needed."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("native", Path(__file__).resolve().parents[1] / "package_native.py")
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)

class NativePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.build = self.root / "build"
        self.build.mkdir()
        self.target = "x86_64-pc-windows-msvc"
        self.metadata = dict(configuration="Release", system="Windows", processor="AMD64",
                             pointer_size=8, compiler="MSVC", cmake="3.30", libraries={})
        for name in native.LIBRARIES:
            lib = self.build / (name + ".lib")
            lib.write_bytes(b"!<arch>\narchive test fixture")
            self.metadata["libraries"][name] = str(lib)
        for relative in ["libweva/include/weva_c.h", *native.LICENSES.values()]:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture")
        (self.root / "libweva/include/weva_c.h").write_text(
            "#define WEVA_ABI_VERSION_MAJOR 0\n#define WEVA_ABI_VERSION_MINOR 45\n")
        (self.build / "native-Release.json").write_text(json.dumps(self.metadata))

    def package(self, dirty="", allow_dirty=False):
        with patch.object(native.subprocess, "check_output", side_effect=["a" * 40, dirty]):
            return native.package(self.root, self.build, self.root / "out", self.target, allow_dirty)

    def test_sdk_and_release_are_identical_and_hashes_cover_every_file(self):
        manifest = self.package()
        self.assertFalse(manifest["source_dirty"])
        self.assertEqual(manifest["abi"], {"major": 0, "minor": 45})
        self.assertEqual(len(manifest["files"]), 10)
        sdk = self.root / "out/sdk" / self.target
        release = self.root / "out/release"
        self.assertEqual((sdk / "manifest.json").read_bytes(), (release / (self.target + ".json")).read_bytes())
        for relative, entry in manifest["files"].items():
            content = (sdk / relative).read_bytes()
            self.assertEqual(content, (release / entry["asset"]).read_bytes())
            self.assertEqual(entry["sha256"], hashlib.sha256(content).hexdigest())
            self.assertEqual(entry["size"], len(content))

    def test_wrong_build_cannot_be_labelled_as_supported_target(self):
        for key, value in [("system", "Linux"), ("processor", "ARM64"),
                           ("pointer_size", 4), ("configuration", "Debug"), ("libraries", {})]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                native.validate_build(self.target, dict(self.metadata, **{key: value}))

    def test_native_macos_and_linux_architectures(self):
        for target, (system, arch) in native.TARGETS.items():
            native.validate_build(target, dict(self.metadata, system=system, processor=arch))

    def test_dirty_release_rejected_and_local_package_marked(self):
        with self.assertRaisesRegex(ValueError, "Commit native"):
            self.package(" M libweva/src/core.cpp")
        self.assertTrue(self.package(" M libweva/src/core.cpp", True)["source_dirty"])

    def test_shared_library_or_missing_icu_cannot_be_packaged(self):
        path = Path(self.metadata["libraries"]["weva_icu_common"])
        path.write_bytes(b"not a static archive")
        with self.assertRaisesRegex(ValueError, "Not a static"):
            self.package()
        path.unlink()
        with self.assertRaisesRegex(ValueError, "Missing or unexpected"):
            self.package()

if __name__ == "__main__":
    unittest.main()
