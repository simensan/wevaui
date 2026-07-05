using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Sdf {
    public class SdfFontMetricsTests {
        [SetUp]
        public void Reset() {
            // FontResolver is static global state; restore a known baseline so
            // tests that mutate it (Failed_face_load_returns_zero_for_metrics)
            // don't leak into peers — and so peers that rely on a populated
            // default mapping see one.
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/test/sans.ttf",
                ["serif"] = "/test/serif.ttf",
                ["monospace"] = "/test/mono.ttf"
            });
            FontResolver.DefaultFamily = "sans-serif";
            AtlasRegistry.Clear();
        }

        // Stub face loader: any (family, style, weight) request resolves to a
        // FaceInfo whose Family encodes the request. Different (family, style,
        // weight) tuples produce DIFFERENT FaceInfos so per-face caching can be
        // observed. The "advance scale" knob lets bold variants report wider
        // glyphs than regular, and italic encodes a different ascent.
        sealed class StubLoader : FontLoader.IFaceLoader {
            public int Calls;
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                Calls++;
                int styleFlags = style == FontStyle.Italic ? FaceInfo.StyleItalic
                                : style == FontStyle.Oblique ? FaceInfo.StyleOblique
                                : FaceInfo.StyleNormal;
                face = new FaceInfo(family, family + "/" + weight + "/" + styleFlags, weight, styleFlags);
                return true;
            }
        }

        // A backend that scales advance by weight (bold = wider) and skips a
        // configurable codepoint to simulate a missing glyph (for fallback tests).
        sealed class WeightedBackend : ITextCoreBackend {
            public uint MissingCp;
            public double BoldFactor = 1.2;
            public double KernAvAv;
            public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
                double upem = 1000;
                double ascent = face.StyleFlags == FaceInfo.StyleItalic ? 850 : 800;
                double descent = face.StyleFlags == FaceInfo.StyleItalic ? 220 : 200;
                metrics = new FaceMetrics(upem, ascent, descent, 0, 1200);
                return true;
            }
            public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
                if (!face.IsValid || codepoint == 0 || codepoint == MissingCp) { advancePx = 0; return false; }
                double base_ = 0.5 * fontSize;
                if (face.Weight >= 600) base_ *= BoldFactor;
                advancePx = base_;
                return true;
            }
            public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
                int w = (int)System.Math.Max(1, fontSize * 0.5) + 4;
                int h = (int)System.Math.Max(1, fontSize) + 4;
                glyph = new RasterizedGlyph(new byte[w * h], w, h, 2,
                    new GlyphMetrics(0.5 * fontSize, 0, 0.8 * fontSize, 0.5 * fontSize, fontSize));
                return true;
            }
        }

        static SdfFontMetrics MakeMetrics(out StubLoader loader, out WeightedBackend backend) {
            loader = new StubLoader();
            backend = new WeightedBackend();
            var fl = new FontLoader(loader, backend);
            return new SdfFontMetrics(fl, backend);
        }

        [Test]
        public void Loader_resolves_default_family_to_valid_face() {
            var sdf = MakeMetrics(out var loader, out _);
            var face = sdf.FaceFor("sans-serif", FontStyle.Normal, 400);
            Assert.That(face.IsValid, Is.True);
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
            Assert.That(loader.Calls, Is.GreaterThan(0));
        }

        [Test]
        public void Measure_text_returns_positive_width() {
            var sdf = MakeMetrics(out _, out _);
            double w = sdf.MeasureText("Hello", FontStyle.Normal, 400, "sans-serif", 16);
            Assert.That(w, Is.GreaterThan(0));
        }

        [Test]
        public void Measure_empty_returns_zero() {
            var sdf = MakeMetrics(out _, out _);
            Assert.That(sdf.MeasureText("", FontStyle.Normal, 400, "sans-serif", 16), Is.EqualTo(0));
            Assert.That(sdf.MeasureText(null, FontStyle.Normal, 400, "sans-serif", 16), Is.EqualTo(0));
        }

        [Test]
        public void LineHeight_matches_face_metrics() {
            var sdf = MakeMetrics(out _, out _);
            double lh = sdf.LineHeight(16);
            // LineHeight encoded in WeightedBackend: 1200 / 1000 * 16 = 19.2
            Assert.That(lh, Is.EqualTo(19.2).Within(1e-6));
        }

        [Test]
        public void Bold_advance_wider_than_regular() {
            var sdf = MakeMetrics(out _, out _);
            double regular = sdf.MeasureText("Hello", FontStyle.Normal, 400, "sans-serif", 16);
            double bold = sdf.MeasureText("Hello", FontStyle.Normal, 700, "sans-serif", 16);
            Assert.That(bold, Is.GreaterThan(regular));
        }

        [Test]
        public void Italic_ascent_differs_from_regular() {
            var sdf = MakeMetrics(out _, out _);
            var regularMetrics = sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            var italicMetrics = sdf.MetricsFor("sans-serif", FontStyle.Italic, 400);
            Assert.That(italicMetrics.Ascent(16), Is.Not.EqualTo(regularMetrics.Ascent(16)));
        }

        [Test]
        public void Distinct_styles_produce_distinct_faces() {
            var sdf = MakeMetrics(out _, out _);
            var regular = sdf.FaceFor("sans-serif", FontStyle.Normal, 400);
            var italic = sdf.FaceFor("sans-serif", FontStyle.Italic, 400);
            var bold = sdf.FaceFor("sans-serif", FontStyle.Normal, 700);
            Assert.That(regular, Is.Not.EqualTo(italic));
            Assert.That(regular, Is.Not.EqualTo(bold));
            Assert.That(italic, Is.Not.EqualTo(bold));
        }

        [Test]
        public void Per_face_metrics_cached_across_calls() {
            var sdf = MakeMetrics(out var loader, out _);
            sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            int after1 = loader.Calls;
            sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            Assert.That(loader.Calls, Is.EqualTo(after1));
            Assert.That(sdf.CachedFaceCount, Is.EqualTo(1));
        }

        [Test]
        public void Subpixel_precision_preserved_in_measure() {
            var sdf = MakeMetrics(out _, out _);
            // Measure 6 'A's at fontSize=15.5: each glyph 0.5*15.5 = 7.75, total = 46.5.
            double w = sdf.MeasureText("AAAAAA", FontStyle.Normal, 400, "sans-serif", 15.5);
            Assert.That(w, Is.EqualTo(46.5).Within(1e-9));
        }

        [Test]
        public void Kerning_provider_reduces_total_advance() {
            var sdf = MakeMetrics(out _, out _);
            sdf.WithKernProvider((face, l, r, fs) => (l == 'A' && r == 'V') || (l == 'V' && r == 'A') ? -2.0 : 0);
            double withoutKern = 6 * 8.0; // 6 chars * 8px each at 16px
            double withKern = sdf.MeasureText("AVAVAV", FontStyle.Normal, 400, "sans-serif", 16);
            Assert.That(withKern, Is.LessThan(withoutKern));
            // 6 glyphs at 8px each = 48; 5 pair-kerns of -2 each = -10; total = 38.
            Assert.That(withKern, Is.EqualTo(38).Within(1e-9));
        }

        // K3 — Prefix-fit measurement must agree with shaped width for the same
        // prefix, including kerning contributions. A per-glyph walk using
        // TryGetAdvance + GetKern (the same lookups Measure/MeasureText use
        // internally) is the reference contract: prefix.Sum(advance) + Sum(kern
        // between consecutive glyphs) == MeasureText(prefix). If this drifts
        // (e.g. a future LargestPrefixThatFits that skips kerning), line breaks
        // mispredict by tens of pixels on AV/Wa/To-style kern-heavy text.
        static double PrefixWalk(SdfFontMetrics sdf, string text, double fontSize) {
            // Mirrors SdfFontMetrics.MeasureText's documented walk: per-glyph
            // advance plus kerning between the previous and current codepoint.
            // Used by these K3 tests as the "what LargestPrefixThatFits ought
            // to compute" reference, independent of the bulk Measure path.
            if (string.IsNullOrEmpty(text)) return 0;
            var m = sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            double total = 0;
            uint prevCp = 0;
            int i = 0;
            while (i < text.Length) {
                char c = text[i];
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    len = 2;
                }
                if (m.TryGetAdvance(cp, fontSize, out double adv)) total += adv;
                if (prevCp != 0) total += sdf.GetKern(m, prevCp, cp, fontSize);
                prevCp = cp;
                i += len;
            }
            return total;
        }

        [Test]
        public void Prefix_walk_matches_shaped_width_for_known_kerning_pair_AV() {
            var sdf = MakeMetrics(out _, out _);
            sdf.WithKernProvider((face, l, r, fs) => (l == 'A' && r == 'V') ? -2.0 : 0);
            // "AV" has one known kerning pair. Both the per-glyph prefix walk
            // and the shaped-width call must include that -2 correction.
            double shaped = sdf.MeasureText("AV", FontStyle.Normal, 400, "sans-serif", 16);
            double walked = PrefixWalk(sdf, "AV", 16);
            Assert.That(walked, Is.EqualTo(shaped).Within(1e-9));
            // Sanity: 8 + 8 + (-2) = 14, NOT 16 (which would indicate a kerning skip).
            Assert.That(shaped, Is.EqualTo(14).Within(1e-9));
        }

        [Test]
        public void Prefix_walk_matches_shaped_width_for_long_string_with_multiple_kern_pairs() {
            var sdf = MakeMetrics(out _, out _);
            // "AVATAR" has negative kerning between AV, VA, AT, TA, AR — five
            // pair adjustments. If the prefix walk skipped any one of them it
            // would drift from the shaped width by 2px (here) or by tens of px
            // at real font sizes.
            sdf.WithKernProvider((face, l, r, fs) => {
                if (l == 'A' && r == 'V') return -2;
                if (l == 'V' && r == 'A') return -2;
                if (l == 'A' && r == 'T') return -1;
                if (l == 'T' && r == 'A') return -1;
                if (l == 'A' && r == 'R') return -0.5;
                return 0;
            });
            const string text = "AVATAR";
            double shaped = sdf.MeasureText(text, FontStyle.Normal, 400, "sans-serif", 16);
            double walked = PrefixWalk(sdf, text, 16);
            Assert.That(walked, Is.EqualTo(shaped).Within(1e-9));
            // Every successive prefix must also agree (line-break callers slice
            // at arbitrary boundaries and rely on prefix.Measure == prefix-walk).
            for (int n = 1; n <= text.Length; n++) {
                string prefix = text.Substring(0, n);
                double prefixShaped = sdf.MeasureText(prefix, FontStyle.Normal, 400, "sans-serif", 16);
                double prefixWalked = PrefixWalk(sdf, prefix, 16);
                Assert.That(prefixWalked, Is.EqualTo(prefixShaped).Within(1e-9),
                    $"prefix '{prefix}' walked={prefixWalked} shaped={prefixShaped}");
            }
            // Sanity: 6 glyphs * 8 = 48; pair-kerns -2-2-1-1-0.5 = -6.5; total 41.5.
            Assert.That(shaped, Is.EqualTo(41.5).Within(1e-9));
        }

        [Test]
        public void Prefix_walk_matches_shaped_width_when_no_kerning_applies() {
            // Regression pin: when no kerning provider is wired (or no pair
            // matches), the walk degenerates to the pre-kerning advance sum and
            // must remain bit-identical to the shaped width. Locks in the
            // baseline behaviour so a future kerning refactor can't silently
            // change widths for plain text.
            var sdf = MakeMetrics(out _, out _);
            // No WithKernProvider — GetKern returns 0 for every pair.
            const string text = "iiiii"; // No Latin pair kerns in any reasonable font.
            double shaped = sdf.MeasureText(text, FontStyle.Normal, 400, "sans-serif", 16);
            double walked = PrefixWalk(sdf, text, 16);
            Assert.That(walked, Is.EqualTo(shaped).Within(1e-9));
            // 5 glyphs * 8px = 40, with zero kerning contribution.
            Assert.That(shaped, Is.EqualTo(40).Within(1e-9));

            // Also confirm: kerning provider wired but no pair matches "iiiii"
            // — still bit-identical.
            sdf.WithKernProvider((face, l, r, fs) => (l == 'A' && r == 'V') ? -2 : 0);
            double shapedAfterProvider = sdf.MeasureText(text, FontStyle.Normal, 400, "sans-serif", 16);
            Assert.That(shapedAfterProvider, Is.EqualTo(40).Within(1e-9));
        }

        [Test]
        public void TryGetAdvance_returns_per_codepoint_advance() {
            var sdf = MakeMetrics(out _, out _);
            Assert.That(sdf.TryGetAdvance('A', 16, out var adv), Is.True);
            Assert.That(adv, Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void Surrogate_pair_measured_once() {
            var sdf = MakeMetrics(out _, out _);
            string text = "a" + char.ConvertFromUtf32(0x1F600) + "b";
            double w = sdf.MeasureText(text, FontStyle.Normal, 400, "sans-serif", 16);
            // Backend reports 8 per glyph; 3 visible glyphs.
            Assert.That(w, Is.EqualTo(24).Within(1e-9));
        }

        [Test]
        public void InvalidateCaches_drops_face_metrics() {
            var sdf = MakeMetrics(out _, out _);
            sdf.MetricsFor("sans-serif", FontStyle.Normal, 400);
            Assert.That(sdf.CachedFaceCount, Is.EqualTo(1));
            sdf.InvalidateCaches();
            Assert.That(sdf.CachedFaceCount, Is.EqualTo(0));
        }

        [Test]
        public void Atlas_registered_on_face_load() {
            AtlasRegistry.Clear();
            var sdf = MakeMetrics(out _, out _);
            var face = sdf.FaceFor("sans-serif", FontStyle.Normal, 400);
            Assert.That(AtlasRegistry.GetAtlas(face), Is.Not.Null);
        }

        [Test]
        public void Failed_face_load_returns_zero_for_metrics() {
            var failingLoader = new FailingLoader();
            // Backend won't matter because face is invalid.
            var fl = new FontLoader(failingLoader, new WeightedBackend());
            var sdf = new SdfFontMetrics(fl, new WeightedBackend());
            // FontResolver fallback may still produce a placeholder face with empty path,
            // which the WeightedBackend would still measure. Force the resolver to also
            // fail by clearing all defaults beforehand.
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string>());
            // After clearing, resolver builds a placeholder face with empty path;
            // the backend (WeightedBackend) accepts any IsValid face, so we use a
            // backend that explicitly rejects faces with empty paths.
            var rejectingBackend = new RejectingBackend();
            sdf = new SdfFontMetrics(new FontLoader(failingLoader, rejectingBackend), rejectingBackend);
            double w = sdf.MeasureText("hi", FontStyle.Normal, 400, "Nope", 16);
            Assert.That(w, Is.EqualTo(0));
        }

        sealed class FailingLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                face = default; return false;
            }
        }

        sealed class RejectingBackend : ITextCoreBackend {
            public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
                if (string.IsNullOrEmpty(face.Path)) { metrics = default; return false; }
                metrics = new FaceMetrics(1000, 800, 200, 0, 1200);
                return true;
            }
            public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
                advancePx = 0; return false;
            }
            public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
                glyph = default; return false;
            }
        }

        [Test]
        public void Default_family_used_when_request_family_is_null() {
            var sdf = MakeMetrics(out _, out _);
            sdf.DefaultFamily = "sans-serif";
            var face = sdf.FaceFor(null, FontStyle.Normal, 400);
            Assert.That(face.Family, Is.EqualTo("sans-serif"));
        }
    }
}
