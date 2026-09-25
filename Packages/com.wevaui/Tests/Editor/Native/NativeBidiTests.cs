// The core orders a line's runs by their bidi levels (UAX #9 through CSS
// Writing Modes 3 §2). Span bounds through the C# wrapper show the order; the
// glyphs inside a Hebrew run are TextCore's business, which shapes none of
// them right-to-left, so only positions are checked here.
using NUnit.Framework;
using UnityEngine;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeBidiTests
    {
        private const string Abg = "\u05D0\u05D1\u05D2";
        private const string Dhv = "\u05D3\u05D4\u05D5";
        private NativeDocument _doc;
        private UnityFontBackend _fonts;

        [SetUp]
        public void Open()
        {
            Font font = Resources.Load<Font>("Fonts/Weva-Default");
            Assume.That(font, Is.Not.Null);
            _doc = new NativeDocument(400, 300);
            _fonts = new UnityFontBackend();
            _fonts.Install(_doc, _fonts.Adopt(font));
            _doc.LoadHtml(
                "<div id=l><span id=la>abc</span> <span id=lb>" + Abg + "</span> <span id=lc>def</span></div>"
                + "<div id=r><span id=ra>abc</span> <span id=rb>" + Abg + "</span></div>"
                + "<div id=r2><span id=r2a>" + Abg + "</span> <span id=r2b>" + Dhv + "</span></div>"
                + "<div id=p><span id=o><span id=oa>abc</span> <span id=ob>def</span></span> <span id=oc>ghi</span></div>");
            _doc.SetCss("html,body{margin:0;width:400px;font-size:16px}#r,#r2{direction:rtl}#o{unicode-bidi:bidi-override;direction:rtl}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _fonts.Dispose();
            _doc.Dispose();
        }

        private double X(string selector)
        {
            Assert.That(_doc.TryGetBounds(_doc.Query(selector), out NativeBounds b), selector + " exists");
            return b.X;
        }

        [Test]
        public void LeftToRight_KeepsLogicalOrder()
        {
            Assert.That(X("#la"), Is.LessThan(X("#lb")));
            Assert.That(X("#lb"), Is.LessThan(X("#lc")));
        }

        [Test]
        public void RightToLeft_PlacesRunsVisually()
        {
            Assert.That(X("#rb"), Is.LessThan(X("#ra")), "the Latin word, logically first, sits right of the Hebrew");
            Assert.That(_doc.TryGetBounds(_doc.Query("#ra"), out NativeBounds ra));
            Assert.That(ra.X + ra.Width, Is.EqualTo(400).Within(0.5), "and the line hugs the right edge");
            Assert.That(X("#r2b"), Is.LessThan(X("#r2a")), "two Hebrew words: the logically first is the rightmost");
        }

        [Test]
        public void BidiOverride_ReversesTheSpan()
        {
            Assert.That(X("#ob"), Is.LessThan(X("#oa")));
            Assert.That(X("#oa"), Is.LessThan(X("#oc")));
        }
    }
}
