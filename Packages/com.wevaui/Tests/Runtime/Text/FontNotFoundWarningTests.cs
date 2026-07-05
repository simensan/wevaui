using NUnit.Framework;
using Weva.Diagnostics;
using Weva.Paint;
using Weva.Text.Sdf;
using Weva.Text.TextCore;

namespace Weva.Tests.Text {
    // A CSS font-family that names a specific (non-generic) font which can't be
    // resolved must surface a console warning so authors know their font wasn't
    // found and the stack fell through. Generic keywords (sans-serif, …) are
    // meant to hit the engine default and must stay silent.
    public class FontNotFoundWarningTests {
        sealed class NullFaceLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                face = FaceInfo.Empty;
                return false;
            }
        }

        static string ExpectedDetail(string fam) =>
            "font-family '" + fam + "' could not be resolved (not a system font and not " +
            "registered via TmpFontAssetRegistry.RegisterFontAsset or FontResolver.RegisterFont) — " +
            "falling back to the next family in the stack";

        [Test]
        public void Named_unresolved_family_emits_font_not_found_warning() {
            UICssDiagnostics.ResetForTests();
            var loader = new FontLoader(new NullFaceLoader(), null);
            loader.Load("Definitely Not Installed 98765", FontStyle.Normal, 400);
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("font-not-found", ExpectedDetail("Definitely Not Installed 98765")),
                Is.True, "a named font that resolves to nothing should warn");
        }

        [Test]
        public void Generic_family_does_not_warn() {
            UICssDiagnostics.ResetForTests();
            var loader = new FontLoader(new NullFaceLoader(), null);
            loader.Load("sans-serif", FontStyle.Normal, 400);
            Assert.That(UICssDiagnostics.HasEmittedForTests("font-not-found", ExpectedDetail("sans-serif")),
                Is.False, "the sans-serif generic is meant to hit the engine default — no warning");
        }

        [Test]
        public void Warning_dedupes_across_repeated_lookups() {
            UICssDiagnostics.ResetForTests();
            var loader = new FontLoader(new NullFaceLoader(), null);
            loader.Load("Repeated Missing Face", FontStyle.Normal, 400);
            loader.Load("Repeated Missing Face", FontStyle.Normal, 400);
            loader.Load("Repeated Missing Face", FontStyle.Normal, 400);
            // One unique (source, detail) pair regardless of how many lookups.
            Assert.That(UICssDiagnostics.EmittedCountForTests(), Is.EqualTo(1));
        }
    }
}
