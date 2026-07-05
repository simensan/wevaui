using NUnit.Framework;
using Weva.Paint;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    public class AtlasRegistryTests {
        sealed class StubLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                int styleFlags = style == FontStyle.Italic ? FaceInfo.StyleItalic : FaceInfo.StyleNormal;
                face = new FaceInfo(family, family + "/" + weight, weight, styleFlags);
                return true;
            }
        }

        [SetUp]
        public void Reset() {
            AtlasRegistry.Clear();
        }

        [Test]
        public void Two_runs_with_different_fonts_register_two_atlases() {
            var loader = new StubLoader();
            var backend = new StubBackend();
            var fl = new FontLoader(loader, backend);
            // One atlas per SdfFontMetrics instance — matches the v1 design
            // where a doc owns its own atlas. Using two distinct SdfFontMetrics
            // means two atlases, even when both serve different family chains.
            var sdf1 = new SdfFontMetrics(fl, backend, new GlyphAtlas());
            var sdf2 = new SdfFontMetrics(fl, backend, new GlyphAtlas());
            sdf1.MetricsFor("FontA", FontStyle.Normal, 400);
            sdf2.MetricsFor("FontB", FontStyle.Normal, 400);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(2));
        }

        [Test]
        public void Same_font_reused_registers_atlas_once() {
            var fl = new FontLoader(new StubLoader(), new StubBackend());
            var sdf = new SdfFontMetrics(fl, new StubBackend(), new GlyphAtlas());
            sdf.MetricsFor("FontA", FontStyle.Normal, 400);
            sdf.MetricsFor("FontA", FontStyle.Normal, 400);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Atlas_lookup_returns_registered_instance() {
            var fl = new FontLoader(new StubLoader(), new StubBackend());
            var atlas = new GlyphAtlas();
            var sdf = new SdfFontMetrics(fl, new StubBackend(), atlas);
            var face = sdf.FaceFor("FontA", FontStyle.Normal, 400);
            Assert.That(AtlasRegistry.GetAtlas(face), Is.SameAs(atlas));
        }

        [Test]
        public void Unregister_removes_atlas() {
            var face = new FaceInfo("F", "/p", 400, FaceInfo.StyleNormal);
            AtlasRegistry.RegisterAtlas(face, new GlyphAtlas());
            Assert.That(AtlasRegistry.Count, Is.EqualTo(1));
            Assert.That(AtlasRegistry.UnregisterAtlas(face), Is.True);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryGetAtlas_returns_false_for_unknown_face() {
            var face = new FaceInfo("Nope", "/p", 400, FaceInfo.StyleNormal);
            Assert.That(AtlasRegistry.TryGetAtlas(face, out _), Is.False);
        }

        [Test]
        public void Invalid_face_or_null_atlas_is_ignored() {
            AtlasRegistry.RegisterAtlas(FaceInfo.Empty, new GlyphAtlas());
            AtlasRegistry.RegisterAtlas(new FaceInfo("F", "/p", 400, 0), null);
            Assert.That(AtlasRegistry.Count, Is.EqualTo(0));
        }
    }
}
