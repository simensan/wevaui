// ABI minor 32: a hot reload through the wrapper keeps the elements it can match.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeReloadTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\"><p id=\"p\">one</p></div><div id=\"b\">two</div>");
            _doc.SetCss("body{margin:0}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void ReloadHtml_KeepsMatchedHandlesAndAppliesTheRest()
        {
            uint a = _doc.Query("#a"), p = _doc.Query("#p");
            _doc.ReloadHtml("<div id=\"a\" class=\"new\"><p id=\"p\">uno</p><p id=\"q\">q</p></div>");
            _doc.Update(0);
            Assert.That(_doc.Query("#a"), Is.EqualTo(a));
            Assert.That(_doc.Query("#p"), Is.EqualTo(p));
            Assert.That(_doc.ElementAttribute(a, "class"), Is.EqualTo("new"));
            Assert.That(_doc.ElementText(p), Is.EqualTo("uno"));
            Assert.That(_doc.Query("#q"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
            Assert.That(_doc.Query("#b"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE));
        }
    }
}
