using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    // CSS Fonts L4 §5.2 — FontResolver face-selection integration tests.
    //
    // These tests verify that the FontResolver weight/style axis matching is
    // wired correctly end-to-end (registration → Resolve → FaceInfo.Path).
    //
    // NOTE: These tests run ONLY via the Unity test bridge (Unity Test Runner /
    // com.unity.test-framework), NOT via the headless TestVerifyAll runner
    // (Runtime/Text/** is excluded from that csproj). The reviewer must run
    // them manually or via CI in Unity. See the W1 inc 4 dispatch summary.
    [TestFixture]
    public class FontResolverFaceSelectionTests {
        [SetUp]
        public void Reset() {
            FontResolver.ClearRegistered();
            // No system defaults during these tests — all resolution is through
            // the registered face list so we're not polluted by OS fonts.
            FontResolver.SetSystemDefaults(new Dictionary<string, string>());
            FontResolver.DefaultFamily = "Inter";
            // Register a typical @font-face stack:
            //   Inter Regular  — weight 100-600, non-italic
            //   Inter Bold     — weight 700-900, non-italic
            //   Inter Italic   — weight 100-900, italic
            FontResolver.RegisterFontFace("Inter", "/fonts/Inter-Regular.ttf",    100f, 600f, false);
            FontResolver.RegisterFontFace("Inter", "/fonts/Inter-Bold.ttf",       700f, 900f, false);
            FontResolver.RegisterFontFace("Inter", "/fonts/Inter-Italic.ttf",     100f, 900f, true);
        }

        // --- RegisterFont back-compat -----------------------------------------

        [Test]
        public void RegisterFont_registers_full_range_normal_face() {
            FontResolver.RegisterFont("Mono", "/fonts/Mono.ttf");
            var face = FontResolver.Resolve(new FontHandle("Mono", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Mono.ttf"));
        }

        [Test]
        public void RegisterFont_back_compat_also_serves_bold_weight() {
            // A single-entry (full-range) registration via the old API should
            // serve both normal and bold requests (the single face covers 1-1000).
            FontResolver.RegisterFont("Mono", "/fonts/Mono.ttf");
            var face = FontResolver.Resolve(new FontHandle("Mono", 16, 700, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Mono.ttf"));
        }

        // --- exact weight range containment -----------------------------------

        [Test]
        public void Weight_400_resolves_to_regular_face() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Regular.ttf"));
        }

        [Test]
        public void Weight_700_resolves_to_bold_face() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 700, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Bold.ttf"));
        }

        [Test]
        public void Weight_600_resolves_to_regular_face_at_range_max() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 600, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Regular.ttf"));
        }

        // --- italic selection -------------------------------------------------

        [Test]
        public void Italic_style_resolves_to_italic_face() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Italic));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Italic.ttf"));
            Assert.That(face.StyleFlags, Is.EqualTo(FaceInfo.StyleItalic));
        }

        [Test]
        public void Bold_italic_resolves_to_italic_face_with_weight_700() {
            // weight 700, italic — italic face covers 100-900 so it wins.
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 700, FontStyle.Italic));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Italic.ttf"));
        }

        [Test]
        public void Oblique_style_treated_as_italic() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Oblique));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Italic.ttf"));
            Assert.That(face.StyleFlags, Is.EqualTo(FaceInfo.StyleOblique));
        }

        // --- directional weight fallback --------------------------------------

        [Test]
        public void Weight_650_above_regular_range_resolves_to_bold() {
            // 650 is not in any range. Desired ≥ 600 → prefer heavier → Bold(700).
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 650, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Bold.ttf"));
        }

        [Test]
        public void Weight_100_below_regular_range_resolves_to_regular() {
            // 100 is at the bottom of Regular range (100-600 is exact hit).
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 100, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Regular.ttf"));
        }

        // --- idempotent registration ------------------------------------------

        [Test]
        public void Duplicate_registration_does_not_duplicate_entry() {
            // Register the same face twice; weight 400 should still hit Regular.
            FontResolver.RegisterFontFace("Inter", "/fonts/Inter-Regular.ttf", 100f, 600f, false);
            FontResolver.RegisterFontFace("Inter", "/fonts/Inter-Regular.ttf", 100f, 600f, false);
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Regular.ttf"));
        }

        // --- family fallback chain --------------------------------------------

        [Test]
        public void Unknown_family_falls_back_to_default_family() {
            // "Nope" is not registered; default is "Inter".
            var face = FontResolver.Resolve(new FontHandle("Nope", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("Inter"));
            Assert.That(face.Path, Is.EqualTo("/fonts/Inter-Regular.ttf"));
        }

        [Test]
        public void Comma_list_picks_first_matching_family() {
            FontResolver.RegisterFont("Fallback", "/fonts/Fallback.ttf");
            var face = FontResolver.Resolve(
                new FontHandle("DoesNotExist, Fallback, Inter", 16, 400, FontStyle.Normal));
            Assert.That(face.Family, Is.EqualTo("Fallback"));
        }

        // --- weight passed through to FaceInfo --------------------------------

        [Test]
        public void FaceInfo_weight_reflects_requested_weight_not_face_midpoint() {
            var face = FontResolver.Resolve(new FontHandle("Inter", 16, 800, FontStyle.Normal));
            Assert.That(face.Weight, Is.EqualTo(800));
        }
    }
}
