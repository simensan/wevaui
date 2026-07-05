#if UNITY_2023_1_OR_NEWER
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.TextCore {
    // Regression (INPUTTEST-TITLE-BASELINE): UnityFontEngineBackend.LoadFace used
    // to hard-code unitsPerEm = 1000 while FontEngine.GetFaceInfo() — read right
    // after LoadFontFace(), before any SetFaceSize() — reports the face's
    // DESIGN-unit metrics whose em IS faceInfo.pointSize (2048 for Weva-Default;
    // ascentLine 2146, lineHeight 2701). Dividing design-unit ascent by 1000
    // instead of the real ~2048 made every SDF-path ascent / line-height ~2.05x
    // too large. Large text (which routes to the SDF baker, not ATG's small-text
    // coverage) then had its baseline pushed ~one ascent below the line box, so
    // the inputtest 30px/800-weight title rendered a full line low and overlapped
    // the hint beneath it. The fix reads faceInfo.pointSize as the em.
    public class UnityFontEngineBackendMetricsTests {
        const string BundledFont = "Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default.ttf";

        [Test]
        public void LoadFace_uses_real_units_per_em_so_ascent_ratio_is_sane() {
            var backend = new UnityFontEngineBackend();
            var face = new FaceInfo("weva-default-upem-test", BundledFont, 400, FaceInfo.StyleNormal);
            Assert.That(backend.LoadFace(face, out var metrics), Is.True,
                "bundled Weva-Default.ttf must load through the FontEngine backend");

            // A real text font has ascent ≈ 0.7–1.3 em and line-height ≈ 1.0–1.8 em.
            // The hard-coded-1000 bug produced ascent/em ≈ 2.15 and lineHeight/em ≈ 2.7.
            double ascentRatio = metrics.Ascent / metrics.UnitsPerEm;
            double lineRatio = metrics.LineHeight / metrics.UnitsPerEm;
            Assert.That(ascentRatio, Is.InRange(0.6, 1.4),
                $"ascent/em should be ~1 em, got {ascentRatio:F3} (em={metrics.UnitsPerEm}, ascent={metrics.Ascent})");
            Assert.That(lineRatio, Is.InRange(0.9, 1.9),
                $"lineHeight/em should be ~1.2 em, got {lineRatio:F3}");
        }

        [Test]
        public void Scaled_ascent_at_30px_is_about_one_em_not_double() {
            var backend = new UnityFontEngineBackend();
            var face = new FaceInfo("weva-default-upem-test2", BundledFont, 400, FaceInfo.StyleNormal);
            Assert.That(backend.LoadFace(face, out var metrics), Is.True);
            // ascent(30) = Ascent * 30 / UnitsPerEm. With the bug this was ~64
            // (2.15 em); a correct face gives ~31 (≈1 em). Pin well below 45 so
            // the doubled-ascent regression can't slip back in.
            double ascent30 = metrics.Ascent * 30.0 / metrics.UnitsPerEm;
            Assert.That(ascent30, Is.LessThan(45.0),
                $"30px ascent should be ~31px, got {ascent30:F2}");
            Assert.That(ascent30, Is.GreaterThan(20.0), $"30px ascent unexpectedly tiny: {ascent30:F2}");
        }
    }
}
#endif
