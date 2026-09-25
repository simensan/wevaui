"""Keep copied sample images resolvable and isolated from other samples."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import unquote, urlsplit


SCRIPT = Path(__file__).resolve().parents[1] / "oracle" / "collect_samples.py"
SPEC = importlib.util.spec_from_file_location("collect_samples", SCRIPT)
COLLECT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECT)


class SampleCollectionTests(unittest.TestCase):
    def test_nested_and_parent_assets_keep_their_contents_without_collisions(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            page = root / "source" / "page"
            out = root / "corpus"
            files = {"a/icon.png": b"red", "b/icon.png": b"blue",
                     "../shared/my icon.png": b"green"}
            for name, data in files.items():
                source = page / name
                source.parent.mkdir(parents=True, exist_ok=True)
                source.write_bytes(data)
            original = ('<img src="a/icon.png"><img src="b/icon.png">'
                        '<style>.x { background: url(\'../shared/my%20icon.png?v=1#part\'); }</style>')
            result = COLLECT.copy_assets(original, page, out, "demo")
            matches = list(COLLECT.ASSET.finditer(result))
            self.assertEqual(len(matches), 3)
            paths = []
            for match, expected in zip(matches, files.values()):
                url = urlsplit(match.group(1) or match.group(2))
                dest = (out / unquote(url.path)).resolve()
                self.assertTrue(dest.is_relative_to(out.resolve()))
                self.assertEqual(dest.read_bytes(), expected)
                paths.append(dest)
            self.assertEqual(len(set(paths)), 3)
            self.assertIn("?v=1#part", result)
            self.assertEqual(COLLECT.copy_assets(original, page, out, "demo"), result)

    def test_remote_and_data_urls_are_unchanged(self):
        markup = ('<img src="https://example.test/a.png">'
                  '<img src="//example.test/b.png">'
                  '<style>.a { background: url(data:image/png;base64,AAAA); }</style>')
        self.assertEqual(COLLECT.copy_assets(markup, ".", ".", "unused"), markup)

    def test_missing_local_asset_fails_collection(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(FileNotFoundError):
                COLLECT.copy_assets('<img src="missing.png">', tmp, tmp, "demo")

    def test_full_collection_renames_pages_and_rewrites_inline_and_sibling_css(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            sources = (root / "Assets/UI", root / "Packages/com.wevaui/Samples~/A",
                       root / "Packages/com.wevaui/Samples~/B")
            for index, folder in enumerate(sources):
                (folder / "Sprites").mkdir(parents=True)
                (folder / "Sprites/frame.png").write_bytes(bytes([index]))
                (folder / "menu.html").write_text(
                    '<img src="Sprites/frame.png"><style>.a { background: url(Sprites/frame.png); }</style>',
                    encoding="utf-8")
            (sources[1] / "menu.css").write_text('.b { border-image-source: url(Sprites/frame.png); }', encoding="utf-8")
            out = root / "out"
            run = subprocess.run([sys.executable, str(SCRIPT), str(root), "--out", str(out)],
                                 capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stderr)
            names = ("menu", "sample-menu", "sample-sample-menu")
            self.assertEqual(sorted(p.stem for p in out.glob("*.html")), sorted(names))
            self.assertIn('href="sample-menu.css"', (out / "sample-menu.html").read_text())
            for index, name in enumerate(names):
                for extension in (".html", ".css"):
                    text = (out / (name + extension)).read_text()
                    matches = list(COLLECT.ASSET.finditer(text))
                    self.assertTrue(matches)
                    for match in matches:
                        path = out / unquote(match.group(1) or match.group(2))
                        self.assertEqual(path.read_bytes(), bytes([index]))


if __name__ == "__main__":
    unittest.main()
