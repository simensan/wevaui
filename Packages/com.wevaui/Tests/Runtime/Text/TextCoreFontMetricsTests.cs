using NUnit.Framework;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    public class TextCoreFontMetricsTests {
        static FaceInfo Face() => new FaceInfo("stub", "/path", 400, FaceInfo.StyleNormal);

        static TextCoreFontMetrics Make(StubBackend b = null) {
            return new TextCoreFontMetrics(b ?? new StubBackend(), Face(), new GlyphAtlas(new AtlasResizePolicy(64, 256, AtlasResizePolicy.PolicyMode.GrowThenLru)));
        }

        [Test]
        public void LineHeight_matches_face_line_height_scaled() {
            var m = Make();
            // Stub: lineHeight = 1.2 * upem; scale = fontSize/upem; lineGap is 0.
            // Path with non-zero LineHeight returns that scaled.
            Assert.That(m.LineHeight(16), Is.EqualTo(1.2 * 16).Within(1e-9));
            Assert.That(m.LineHeight(32), Is.EqualTo(1.2 * 32).Within(1e-9));
        }

        [Test]
        public void Ascent_descent_scaled_correctly() {
            var m = Make();
            Assert.That(m.Ascent(16), Is.EqualTo(0.8 * 16).Within(1e-9));
            Assert.That(m.Descent(16), Is.EqualTo(0.4 * 16).Within(1e-9));
            Assert.That(m.Ascent(20), Is.EqualTo(0.8 * 20).Within(1e-9));
            Assert.That(m.Descent(20), Is.EqualTo(0.4 * 20).Within(1e-9));
        }

        [Test]
        public void Measure_sums_per_codepoint_advances() {
            var m = Make();
            Assert.That(m.Measure("hello", 16), Is.EqualTo(5 * 8).Within(1e-9));
        }

        [Test]
        public void Measure_handles_surrogate_pair() {
            var m = Make();
            string text = "a" + char.ConvertFromUtf32(0x1F600) + "b";
            Assert.That(m.Measure(text, 16), Is.EqualTo(3 * 8).Within(1e-9));
        }

        [Test]
        public void Measure_empty_string_is_zero() {
            var m = Make();
            Assert.That(m.Measure("", 16), Is.EqualTo(0));
            Assert.That(m.Measure(null, 16), Is.EqualTo(0));
        }

        [Test]
        public void Cache_reused_across_calls() {
            var m = Make();
            m.LineHeight(16);
            m.Ascent(16);
            m.Descent(16);
            int countAfterFirst = m.CachedScaleCount;
            m.LineHeight(16);
            m.Ascent(16);
            m.Descent(16);
            Assert.That(m.CachedScaleCount, Is.EqualTo(countAfterFirst));
            Assert.That(m.CachedScaleCount, Is.EqualTo(1));
        }

        [Test]
        public void Distinct_sizes_use_distinct_cache_entries() {
            var m = Make();
            m.LineHeight(16);
            m.LineHeight(20);
            m.LineHeight(24);
            Assert.That(m.CachedScaleCount, Is.EqualTo(3));
        }

        [Test]
        public void TryGetAdvance_returns_per_codepoint_advance() {
            var m = Make();
            Assert.That(m.TryGetAdvance('A', 16, out var adv), Is.True);
            Assert.That(adv, Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void TryGetAdvance_caches() {
            var b = new StubBackend();
            var m = new TextCoreFontMetrics(b, Face());
            m.TryGetAdvance('A', 16, out _);
            int beforeCachedCount = m.CachedAdvanceCount;
            m.TryGetAdvance('A', 16, out _);
            Assert.That(m.CachedAdvanceCount, Is.EqualTo(beforeCachedCount));
        }

        [Test]
        public void TryGetGlyphRect_populates_atlas() {
            var m = Make();
            Assert.That(m.TryGetGlyphRect('A', 16, out var rect), Is.True);
            Assert.That(rect.U1, Is.GreaterThan(rect.U0));
            Assert.That(rect.V1, Is.GreaterThan(rect.V0));
            Assert.That(m.Atlas.GlyphCount, Is.EqualTo(1));
        }

        [Test]
        public void Failed_face_load_returns_zero_metrics() {
            var m = new TextCoreFontMetrics(new StubBackend(), FaceInfo.Empty);
            Assert.That(m.LineHeight(16), Is.EqualTo(0));
            Assert.That(m.Measure("hello", 16), Is.EqualTo(0));
        }

        [Test]
        public void InvalidateCaches_resets_state() {
            var m = Make();
            m.LineHeight(16);
            m.TryGetAdvance('A', 16, out _);
            Assert.That(m.CachedScaleCount, Is.GreaterThan(0));
            m.InvalidateCaches();
            Assert.That(m.CachedScaleCount, Is.EqualTo(0));
            Assert.That(m.CachedAdvanceCount, Is.EqualTo(0));
        }
    }
}
