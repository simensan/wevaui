#if UNITY_2023_1_OR_NEWER
using NUnit.Framework;
using Weva.Text.Sdf;

namespace Weva.Tests.Text {
    // ComputeWeightBias must synthesize faux-bold only for the weight GAP above
    // the face's natural weight, so an already-bold face (a Sniglet ExtraBold
    // registered as 800) asked to render 800 gets NO synthesis — it was being
    // double-bolded (fat stems, closed counters) vs Chrome. SdfGlyphAtlasAdapter
    // is internal-accessible via the runtime's InternalsVisibleTo grant.
    public class FauxBoldWeightTests {
        [Test]
        public void Regular_face_at_regular_has_no_bias() {
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(400, 400), Is.EqualTo(0f));
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(400), Is.EqualTo(0f));
        }

        [Test]
        public void Already_bold_face_is_not_double_bolded() {
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(800, 800), Is.EqualTo(0f),
                "ExtraBold face asked for 800 must not faux-bold");
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(700, 700), Is.EqualTo(0f));
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(700, 900), Is.EqualTo(0f),
                "requesting lighter than the face never thickens");
        }

        [Test]
        public void Regular_face_still_synthesizes_bold()
        {
            // 400 face asked for 700 → the calibrated bold bias (~0.075).
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(700, 400), Is.EqualTo(0.075f).Within(0.0005f));
            // Backward-compatible: the single-arg overload (FontEngine path)
            // equals the gap form with a regular face.
            Assert.That(SdfGlyphAtlasAdapter.ComputeWeightBias(800),
                Is.EqualTo(SdfGlyphAtlasAdapter.ComputeWeightBias(800, 400)).Within(1e-6f));
        }

        [Test]
        public void Partial_gap_synthesizes_partial_bias() {
            // 700 face asked for 900 synthesizes only the +200 gap: > 0 but less
            // than synthesizing 900 from a regular face.
            float partial = SdfGlyphAtlasAdapter.ComputeWeightBias(900, 700);
            Assert.That(partial, Is.GreaterThan(0f));
            Assert.That(partial, Is.LessThan(SdfGlyphAtlasAdapter.ComputeWeightBias(900, 400)));
        }
    }
}
#endif
