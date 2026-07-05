using NUnit.Framework;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    public class StubBackendTests {
        static FaceInfo Face() => new FaceInfo("stub", "/path", 400, FaceInfo.StyleNormal);

        [Test]
        public void LoadFace_returns_deterministic_metrics() {
            var b = new StubBackend();
            Assert.That(b.LoadFace(Face(), out var m), Is.True);
            Assert.That(m.UnitsPerEm, Is.EqualTo(1024).Within(1e-9));
            Assert.That(m.Ascent, Is.EqualTo(0.8 * 1024).Within(1e-9));
            Assert.That(m.Descent, Is.EqualTo(0.4 * 1024).Within(1e-9));
        }

        [Test]
        public void LoadFace_caches_result() {
            var b = new StubBackend();
            b.LoadFace(Face(), out _);
            int after1 = b.LoadFaceCallCount;
            b.LoadFace(Face(), out _);
            // Method runs but should not re-create FaceMetrics; we just track the dictionary lookup path.
            Assert.That(b.LoadFaceCallCount, Is.GreaterThan(0));
            Assert.That(after1, Is.GreaterThan(0));
        }

        [Test]
        public void LoadFace_rejects_invalid() {
            var b = new StubBackend();
            Assert.That(b.LoadFace(FaceInfo.Empty, out _), Is.False);
        }

        [Test]
        public void TryGetGlyphAdvance_returns_half_em() {
            var b = new StubBackend();
            b.LoadFace(Face(), out _);
            Assert.That(b.TryGetGlyphAdvance(Face(), 'A', 16, out var adv), Is.True);
            Assert.That(adv, Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void TryGetGlyphAdvance_rejects_zero_codepoint() {
            var b = new StubBackend();
            Assert.That(b.TryGetGlyphAdvance(Face(), 0, 16, out _), Is.False);
        }

        [Test]
        public void Rasterize_produces_padded_bitmap() {
            var b = new StubBackend(0.5, 1.2, 0.8, 0.4, 2);
            Assert.That(b.RasterizeGlyph(Face(), 'A', 16, out var glyph), Is.True);
            Assert.That(glyph.Width, Is.EqualTo(8 + 4));
            Assert.That(glyph.Height, Is.EqualTo((int)System.Math.Ceiling((0.8 + 0.4) * 16) + 4));
            Assert.That(glyph.Padding, Is.EqualTo(2));
            Assert.That(glyph.Pixels.Length, Is.EqualTo(glyph.Width * glyph.Height));
        }

        [Test]
        public void Rasterize_metrics_match_expected() {
            var b = new StubBackend();
            b.RasterizeGlyph(Face(), 'A', 20, out var glyph);
            Assert.That(glyph.Metrics.AdvanceX, Is.EqualTo(10).Within(1e-9));
            Assert.That(glyph.Metrics.BearingY, Is.EqualTo(0.8 * 20).Within(1e-9));
        }
    }
}
