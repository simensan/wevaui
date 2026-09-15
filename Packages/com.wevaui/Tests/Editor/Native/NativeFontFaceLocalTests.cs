// ABI minor 37: a @font-face src list is tried in the author's order, and a
// local("Name") entry is an installed font.
using System;
using NUnit.Framework;
using System.IO;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeFontFaceLocalTests
    {
        [TestCase("src:url(old.ttf);src:url(\"round)font.ttf\")")]
        [TestCase(@"src:u\72 l(round\29 font.ttf)")]
        public void Sources_LoadDecodedUrlAndReplaceEarlierDescriptor(string source)
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            byte[] bold = File.ReadAllBytes("Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default-Bold.ttf");
            using (var doc = new NativeDocument(400, 100))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                var requests = new System.Collections.Generic.List<string>();
                doc.AssetReader = path => { requests.Add(path); return path == "fonts/round)font.ttf" ? bold : null; };
                doc.SetBasePath("fonts");
                doc.LoadHtml("<body><span id=t>Heavy AV words</span></body>");
                doc.SetCss("body{margin:0}#t{font:24px ReviewFace}@font-face{font-family:ReviewFace;" + source + "}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds before));
                requests.Clear();
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1), fonts.LastError);
                Assert.That(requests, Is.EqualTo(new[] { "fonts/round)font.ttf" }));
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds after));
                Assert.That(Math.Abs(after.Width - before.Width), Is.GreaterThan(0.1));
                string dump = Environment.GetEnvironmentVariable("WEVA_FONT_SOURCE_DUMP");
                if (!string.IsNullOrEmpty(dump))
                {
                    using (var renderer = new NativeDocumentRenderer())
                    {
                        Texture2D image = renderer.RenderToTexture(doc, 400, 100, Color.white);
                        try
                        {
                            Directory.CreateDirectory(dump);
                            File.WriteAllBytes(Path.Combine(dump, source.IndexOf('\\') >= 0 ? "escaped.png" : "replaced.png"), image.EncodeToPNG());
                        }
                        finally { UnityEngine.Object.DestroyImmediate(image); }
                    }
                }
            }
        }

        [Test]
        public void Local_FallsThroughToTheUrlAfterIt()
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(400, 100))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.SetBasePath(Path.GetFullPath("Packages/com.wevaui/Runtime/Resources/Fonts"));
                doc.LoadHtml("<body><span id=t>Heavy words</span></body>");
                doc.SetCss("@font-face{font-family:Heavy;src:local(\"Weva No Such Font 9f3\"), url(Weva-Default-Bold.ttf)}body{margin:0}");
                var faces = doc.FontFaces();
                Assert.That(faces.Count, Is.EqualTo(1));
                Assert.That(faces[0].Sources, Does.StartWith("local:Weva No Such Font 9f3|url:"));
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1), "the url() after the unknown local() serves the family");

                doc.SetCss("@font-face{font-family:Nowhere;src:local(\"Weva No Such Font 9f3\")}body{margin:0}");
                Assert.That(doc.FontFaces().Count, Is.EqualTo(1), "a local()-only rule is listed");
                Assert.That(doc.FontFaces()[0].Source, Is.Empty);
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(0), "and nobody has that font");
                Assert.That(fonts.LastError, Does.Contain("Nowhere"));
            }
        }

        // ABI minor 41: a family a page names in `font-family`, with no
        // @font-face behind it, resolves to the installed font of that name,
        // as in a browser -- unless the game registered the name itself.
        [Test]
        public void PageFamily_ResolvesToAnInstalledFont()
        {
            string[] installed;
            try { installed = Font.GetOSInstalledFontNames(); }
            catch (Exception) { Assert.Inconclusive("no OS font enumeration here"); return; }
            Assume.That(installed, Is.Not.Null.And.Not.Empty);
            string name = Array.IndexOf(installed, "Arial") >= 0 ? "Arial" : installed[0];
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(400, 100))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.LoadHtml("<body><span id=t>Heavy words</span></body>");
                doc.SetCss("body{margin:0}#t{font-family:\"" + name + "\", sans-serif}");
                Assert.That(doc.FontFamilyNames(), Is.EqualTo(new[] { name }), "the core lists the named family, not the generic");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds before));

                Assert.That(fonts.SyncInstalledFamilies(doc), Is.EqualTo(1), "the page's family is an installed font: " + name + " (" + fonts.LastError + ")");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds after));
                Assert.That(Math.Abs(after.Width - before.Width), Is.GreaterThan(0.01), "the text is measured with the installed face now, not the UI face");
                Assert.That(fonts.SyncInstalledFamilies(doc), Is.EqualTo(1), "a second sync keeps it");

                doc.SetCss("body{margin:0}#t{font-family:sans-serif}");
                Assert.That(fonts.SyncInstalledFamilies(doc), Is.EqualTo(0), "no named family: the installed one is released");
                Assert.That(fonts.InstalledFamilyCount, Is.EqualTo(0));

                fonts.RegisterFontFamily(doc, name, fonts.Adopt(font));
                doc.SetCss("body{margin:0}#t{font-family:\"" + name + "\"}");
                Assert.That(fonts.SyncInstalledFamilies(doc), Is.EqualTo(0), "the game's registration of that name wins");

                doc.SetCss("body{margin:0}#t{font-family:\"Weva No Such Font 9f3\"}");
                Assert.That(fonts.SyncInstalledFamilies(doc), Is.EqualTo(0), "a name nobody has is simply not served");
            }
        }

        [Test]
        public void Local_LoadsAnInstalledFontByName()
        {
            string[] installed;
            try { installed = Font.GetOSInstalledFontNames(); }
            catch (Exception) { Assert.Inconclusive("no OS font enumeration here"); return; }
            Assume.That(installed, Is.Not.Null.And.Not.Empty);
            string name = Array.IndexOf(installed, "Arial") >= 0 ? "Arial" : installed[0];
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            using (var doc = new NativeDocument(400, 100))
            using (var fonts = new UnityFontBackend())
            {
                fonts.Install(doc, fonts.Adopt(font));
                doc.LoadHtml("<body><span id=t>Heavy words</span></body>");
                doc.SetCss("@font-face{font-family:Sys;src:local(\"" + name + "\")}body{margin:0}#t{font-family:Sys}");
                Assert.That(fonts.SyncCssFontFaces(doc), Is.EqualTo(1), "an installed font by name: " + name + " (" + fonts.LastError + ")");
            }
        }
    }
}
