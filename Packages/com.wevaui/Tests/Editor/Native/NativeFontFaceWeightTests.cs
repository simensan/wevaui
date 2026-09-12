// Which @font-face rule becomes a family's regular face. The Godot host makes
// the same decision in sync_css_font_faces (hosts/godot/src/weva_node.cpp);
// these pin the Unity side of it so the two cannot drift again without a red.
//
// The observable is width: Weva-Default-Bold is measurably wider than
// Weva-Default at the same size, so measuring a span in the CSS family says
// which file is serving it without reaching into the backend.
using NUnit.Framework;
using System.IO;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeFontFaceWeightTests
    {
        private const string FontDir = "Packages/com.wevaui/Runtime/Resources/Fonts";
        private const string Html = "<body><span id=t>Handgloves</span></body>";
        private const string Base = "body{margin:0;font-size:32px}#t{font-family:Probe}";

        // The width of #t when the Probe family is served by `rule`.
        private static double WidthOf(string rule)
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(800, 200))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.SetBasePath(Path.GetFullPath(FontDir));
                doc.LoadHtml(Html);
                doc.SetCss(rule + Base);
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1),
                    "the family is served at all (" + fonts.LastError + ")");
                doc.Update(0.0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds b));
                return b.Width;
            }
        }

        private static double Regular =>
            WidthOf("@font-face{font-family:Probe;src:url(Weva-Default.ttf)}");

        private static double Bold =>
            WidthOf("@font-face{font-family:Probe;src:url(Weva-Default-Bold.ttf)}");

        [Test]
        public void TheTwoProbeFilesAreTellableApart()
        {
            Assert.That(Bold, Is.GreaterThan(Regular + 1.0),
                "the whole file rests on bold being wider than regular");
        }

        [Test]
        public void A500FaceOnItsOwnServesTheFamily()
        {
            // Nothing closer exists, so CSS Fonts 4 matching resolves
            // font-weight:400 to the 500 face. Dropping it would leave the
            // text in the fallback font.
            double w = WidthOf("@font-face{font-family:Probe;font-weight:500;src:url(Weva-Default-Bold.ttf)}");
            Assert.That(w, Is.EqualTo(Bold).Within(0.01),
                "a lone non-400 face still serves the family");
        }

        [Test]
        public void A300FaceOnItsOwnServesTheFamily()
        {
            double w = WidthOf("@font-face{font-family:Probe;font-weight:300;src:url(Weva-Default-Bold.ttf)}");
            Assert.That(w, Is.EqualTo(Bold).Within(0.01));
        }

        [Test]
        public void AnExact400FaceWinsOverA500RegardlessOfOrder()
        {
            // The bug this pins: picking "the first face under 600" makes the
            // family's regular face depend on which rule the author wrote
            // first. 400 is the exact match and must win either way.
            double fourHundredFirst = WidthOf(
                "@font-face{font-family:Probe;font-weight:400;src:url(Weva-Default.ttf)}" +
                "@font-face{font-family:Probe;font-weight:500;src:url(Weva-Default-Bold.ttf)}");
            double fiveHundredFirst = WidthOf(
                "@font-face{font-family:Probe;font-weight:500;src:url(Weva-Default-Bold.ttf)}" +
                "@font-face{font-family:Probe;font-weight:400;src:url(Weva-Default.ttf)}");

            Assert.That(fourHundredFirst, Is.EqualTo(Regular).Within(0.01),
                "400 declared first serves the family");
            Assert.That(fiveHundredFirst, Is.EqualTo(Regular).Within(0.01),
                "400 declared second still serves the family");
            Assert.That(fiveHundredFirst, Is.EqualTo(fourHundredFirst).Within(0.01),
                "declaration order does not decide the regular face");
        }

        [Test]
        public void ABoldOnlyFamilyFallsBackToItsVariant()
        {
            double w = WidthOf("@font-face{font-family:Probe;font-weight:700;src:url(Weva-Default-Bold.ttf)}");
            Assert.That(w, Is.EqualTo(Bold).Within(0.01),
                "with only a bold face declared, it serves the family too");
        }

        // The Godot host releases a family whose @font-face the new stylesheet
        // dropped; font_face_tests.gd pins it there. Unity kept the family
        // registered for the life of the document.
        [Test]
        public void ReplacingTheStylesheetReleasesAFamilyItNoLongerDeclares()
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(800, 200))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.SetBasePath(Path.GetFullPath(FontDir));
                doc.LoadHtml(Html);

                doc.SetCss("@font-face{font-family:Probe;src:url(Weva-Default-Bold.ttf)}" + Base);
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1));
                doc.Update(0.0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds served));

                // Same document, a stylesheet with no @font-face at all.
                doc.SetCss(Base);
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(0), "nothing left to serve");
                doc.Update(0.0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds released));

                Assert.That(released.Width, Is.Not.EqualTo(served.Width).Within(0.01),
                    "the family is released, so #t falls back to the installed default");
            }
        }

        // Godot: "the game's own registration wins over @font-face".
        [Test]
        public void AGameRegisteredFamilyIsNotTakenOverByFontFace()
        {
            Font regular = Resources.Load<Font>("Fonts/Weva-Default");
            Font bold = Resources.Load<Font>("Fonts/Weva-Default-Bold");
            Assume.That(regular, Is.Not.Null);
            Assume.That(bold, Is.Not.Null);
            using (var doc = new NativeDocument(800, 200))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(regular));
                doc.SetBasePath(Path.GetFullPath(FontDir));
                doc.LoadHtml(Html);

                // The game claims Probe for the bold file.
                fonts.RegisterFontFamily(doc, "Probe", fonts.Adopt(bold));
                // The page then tries to claim it for the regular file.
                doc.SetCss("@font-face{font-family:Probe;src:url(Weva-Default.ttf)}" + Base);
                fonts.SyncCssFontFaces(doc);
                doc.Update(0.0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds b));

                Assert.That(b.Width, Is.EqualTo(Bold).Within(0.01),
                    "the game's face still serves the family, not the stylesheet's");
            }
        }
    }
}
