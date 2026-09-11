// ABI minor 36: the host's safe-area insets reach env(safe-area-inset-*)
// through the C# wrapper, and a change restyles a live document.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeSafeAreaTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"p\"></div><div id=\"q\"></div>");
            _doc.SetCss("body{margin:0}"
                + "#p{padding-top:env(safe-area-inset-top);padding-left:env(safe-area-inset-left, 3px);height:10px}"
                + "#q{margin-top:calc(env(safe-area-inset-bottom) + 2px);height:10px}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        private NativeBounds Bounds(string selector)
        {
            _doc.Update(0);
            Assert.That(_doc.TryGetBounds(_doc.Query(selector), out NativeBounds b), selector + " exists");
            return b;
        }

        [Test]
        public void Default_IsZeroNotTheFallback()
        {
            Assert.That(Bounds("#p").Height, Is.EqualTo(10));
            Assert.That(Bounds("#p").X, Is.EqualTo(0));
            Assert.That(Bounds("#q").Y, Is.EqualTo(12));
        }

        [Test]
        public void Insets_PadAndRestyle()
        {
            _doc.SetSafeAreaInsets(44, 0, 20, 8);
            Assert.That(Bounds("#p").Height, Is.EqualTo(54));
            Assert.That(Bounds("#q").Y, Is.EqualTo(54 + 22));
            _doc.SetSafeAreaInsets(0, 0, 0, 0);
            Assert.That(Bounds("#p").Height, Is.EqualTo(10));
        }
    }
}
