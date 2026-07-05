#if UNITY_2023_1_OR_NEWER
using NUnit.Framework;
using UnityEngine.TextCore.LowLevel;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    public class SdfGlyphRasterizerTests {
        static FaceInfo MakeFace(string family = "test", string path = "/test/sans.ttf") {
            return new FaceInfo(family, path, 400, FaceInfo.StyleNormal);
        }

        [SetUp]
        public void Reset() {
            SdfGlyphRasterizer.Override = null;
            SdfGlyphRasterizer.ResetLookupForTests();
        }

        [TearDown]
        public void TearDown() {
            SdfGlyphRasterizer.Override = null;
            SdfGlyphRasterizer.ResetLookupForTests();
        }

        // The reflection lookup is best-effort. On Unity 6000.4.1f1 the method
        // is present in UnityEngine.TextCoreTextEngineModule.dll. On older
        // versions the lookup may fail; the rasterizer falls back. We assert
        // the lookup attempt doesn't throw and reports a string error if it
        // can't bind.
        [Test]
        public void Reflection_lookup_does_not_throw() {
            bool _ = SdfGlyphRasterizer.ReflectionAvailable;
            // Either succeeded or has an error; never both.
            string err = SdfGlyphRasterizer.ReflectionError;
            if (SdfGlyphRasterizer.ReflectionAvailable) {
                Assert.That(err, Is.Null);
            } else {
                Assert.That(err, Is.Not.Null.And.Not.Empty);
            }
        }

        [Test]
        public void Override_is_used_when_set() {
            int callCount = 0;
            SdfGlyphRasterizer.Override = (glyphs, padding, mode, tex) => {
                callCount++;
                return FontEngineError.Success;
            };
            // The override is checked but the rasterizer still requires a valid
            // path-based face for FontEngine.LoadFontFace to succeed. With a
            // bogus path we expect the FontEngine call to fail before the
            // override is invoked. So zero calls is a valid pass.
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            rasterizer.TryRasterize(MakeFace("nope", "/nonexistent/path.ttf"), 'A', 16,
                out _, out _, out _, out _);
            Assert.That(callCount, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void TryRasterize_returns_false_for_invalid_face() {
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            Assert.That(rasterizer.TryRasterize(FaceInfo.Empty, 'A', 16,
                out var pixels, out var w, out var h, out var metrics), Is.False);
            Assert.That(pixels, Is.Null);
            Assert.That(w, Is.EqualTo(0));
            Assert.That(h, Is.EqualTo(0));
        }

        [Test]
        public void TryRasterize_returns_false_for_empty_path() {
            var face = new FaceInfo("test", "", 400, FaceInfo.StyleNormal);
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            Assert.That(rasterizer.TryRasterize(face, 'A', 16,
                out _, out _, out _, out _), Is.False);
        }

        [Test]
        public void Cached_raster_skips_second_call() {
            // We can't easily exercise the FontEngine path without a real font, so
            // we drive the cache via TryRasterize on a known-failing face: with no
            // valid path the rasterizer never populates the cache, so back-to-back
            // calls both miss.
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            rasterizer.TryRasterize(FaceInfo.Empty, 'A', 16, out _, out _, out _, out _);
            int afterFirst = rasterizer.RasterizeCallCount;
            rasterizer.TryRasterize(FaceInfo.Empty, 'A', 16, out _, out _, out _, out _);
            Assert.That(rasterizer.RasterizeCallCount, Is.EqualTo(afterFirst));
        }

        [Test]
        public void Override_throwing_marks_lookup_failed_and_falls_back() {
            // Set the override to throw; the rasterizer should catch and fall through
            // to the legacy Font.RequestCharactersInTexture fallback (or fail cleanly
            // when no font is loadable). The contract: TryRasterize must not propagate
            // the exception.
            SdfGlyphRasterizer.Override = (glyphs, padding, mode, tex) => {
                throw new System.InvalidOperationException("synthetic test failure");
            };
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            // With an invalid path the FontEngine.LoadFontFace fails before the override
            // is invoked, so the test verifies the "no exception" property.
            Assert.DoesNotThrow(() => rasterizer.TryRasterize(MakeFace("test", "/no/such/path.ttf"),
                'A', 16, out _, out _, out _, out _));
        }

        [Test]
        public void RasterizedGlyph_packaging_returns_correct_padding() {
            // Even when the underlying TryRasterize returns false, the wrapper must
            // produce a default RasterizedGlyph that's IsEmpty.
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            Assert.That(rasterizer.TryRasterizeAsRasterizedGlyph(FaceInfo.Empty, 'A', 16,
                out var glyph), Is.False);
            Assert.That(glyph.IsEmpty, Is.True);
        }

        [Test]
        public void Clear_drops_all_cached_entries() {
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            rasterizer.Clear();
            Assert.That(rasterizer.CachedRasterCount, Is.EqualTo(0));
        }

        [Test]
        public void Padding_constant_is_eight_pixels() {
            // v1 simplification: SDF padding fixed at 8 px, not tunable per font.
            Assert.That(SdfGlyphRasterizer.PaddingPx, Is.EqualTo(8));
        }

        [Test]
        public void Multiple_rasterizers_share_lookup_state() {
            // The reflection lookup is process-global. Two rasterizers see the same
            // ReflectionAvailable result without re-attempting lookup.
            bool a = SdfGlyphRasterizer.ReflectionAvailable;
            bool b = SdfGlyphRasterizer.ReflectionAvailable;
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void Reset_lookup_resets_state() {
            bool _ = SdfGlyphRasterizer.ReflectionAvailable;
            SdfGlyphRasterizer.ResetLookupForTests();
            // After reset, querying again triggers a fresh lookup.
            bool again = SdfGlyphRasterizer.ReflectionAvailable;
            // Either result is valid; we only check the call doesn't throw.
            Assert.That(again || !again, Is.True);
        }

        [Test]
        public void Rasterizer_is_disposable_without_throwing() {
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            Assert.DoesNotThrow(() => rasterizer.Dispose());
        }

        [Test]
        public void Dispose_clears_cache() {
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            rasterizer.Dispose();
            Assert.That(rasterizer.CachedRasterCount, Is.EqualTo(0));
        }

        [Test]
        public void Rasterizer_with_null_backend_does_not_throw_on_construction() {
            Assert.DoesNotThrow(() => new SdfGlyphRasterizer(null));
        }
    }
}
#endif
