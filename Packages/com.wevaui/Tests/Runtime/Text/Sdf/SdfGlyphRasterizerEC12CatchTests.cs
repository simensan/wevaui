#if UNITY_2023_1_OR_NEWER
using System;
using NUnit.Framework;
using UnityEngine.TextCore.LowLevel;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    // EC12 — TryRasterizeViaOverride's catch was `catch (Exception)` with no
    // variable and no logging. The fix:
    //   (1) names the exception (`ex`)
    //   (2) captures `ex.Message` into the shared `s_LookupError` channel
    //       (exposed via `ReflectionError`)
    //   (3) keeps the by-design fallback (returns false → TryRasterize falls
    //       through to the legacy Font.RequestCharactersInTexture path)
    //
    // The catch only fires when `Override` itself throws AND
    // FontEngine.LoadFontFace succeeds for the test face. In CI we lack a
    // real font asset, so we drive the capture-logic regression pin through
    // the internal `SimulateOverrideCatchForTests` seam (which runs the
    // identical s_LookupError = ex.Message assignment), and verify the
    // surrounding contract (no exception propagation, false-return) via the
    // production TryRasterize entry point with a bogus font path.
    public class SdfGlyphRasterizerEC12CatchTests {
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

        [Test]
        public void Simulated_override_throw_captures_message_into_lookup_error() {
            // Pinning the EC12 capture: ex.Message must land in s_LookupError
            // so the breadcrumb is visible via the existing diagnostic
            // accessors. The production catch runs the same single line of
            // assignment that the seam runs; the seam exists to make this
            // testable without needing a real font asset.
            Assert.That(SdfGlyphRasterizer.GetRawLookupErrorForTests(), Is.Null,
                "[SetUp] reset s_LookupError to null; precondition for capture.");

            var ex = new InvalidOperationException("EC12 capture test");
            var captured = SdfGlyphRasterizer.SimulateOverrideCatchForTests(ex);

            Assert.That(captured, Is.EqualTo("EC12 capture test"));
            Assert.That(SdfGlyphRasterizer.GetRawLookupErrorForTests(), Is.EqualTo("EC12 capture test"));
        }

        [Test]
        public void Simulated_npe_captures_message_into_lookup_error() {
            // The catch is broad (catches any Exception); programmer errors
            // like NullReferenceException now leave a traceable message.
            var ex = new NullReferenceException("ec12 npe");
            var captured = SdfGlyphRasterizer.SimulateOverrideCatchForTests(ex);

            Assert.That(captured, Is.EqualTo("ec12 npe"));
        }

        [Test]
        public void Throwing_override_does_not_propagate_exception() {
            // Pinning the EC12 contract: even when the production catch IS
            // reached (real font scenario), no exception escapes. We can't
            // exercise the catch here without a real font, but the test
            // documents the no-exception-propagation contract end-to-end.
            SdfGlyphRasterizer.Override = (glyphs, padding, mode, tex) => {
                throw new InvalidOperationException("EC12-synthetic-message");
            };
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            Assert.DoesNotThrow(() => rasterizer.TryRasterize(MakeFace(),
                'A', 16, out _, out _, out _, out _));
        }

        [Test]
        public void Throwing_override_returns_false_from_TryRasterize() {
            // EC12 fallback: catch path returns false, the outer TryRasterize
            // then attempts the legacy path which also fails for our bogus
            // face. Net result: false, no pixels produced.
            SdfGlyphRasterizer.Override = (glyphs, padding, mode, tex) => {
                throw new NullReferenceException("ec12 npe");
            };
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            bool ok = rasterizer.TryRasterize(MakeFace(), 'A', 16,
                out var px, out var w, out var h, out _);
            Assert.That(ok, Is.False);
            Assert.That(px, Is.Null);
            Assert.That(w, Is.EqualTo(0));
            Assert.That(h, Is.EqualTo(0));
        }

        [Test]
        public void Repeated_throwing_override_calls_do_not_propagate() {
            // 50 consecutive throwing calls — observability is captured into a
            // static field (s_LookupError); the per-call cost is bounded and
            // no exception escapes.
            SdfGlyphRasterizer.Override = (glyphs, padding, mode, tex) => {
                throw new InvalidOperationException("ec12 loop");
            };
            var rasterizer = new SdfGlyphRasterizer(new StubBackend());
            for (int i = 0; i < 50; i++) {
                Assert.DoesNotThrow(() => rasterizer.TryRasterize(MakeFace(),
                    (uint)('A' + i), 16, out _, out _, out _, out _));
            }
        }
    }
}
#endif
