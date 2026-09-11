// ABI minor 29: the CSS cursor under the pointer through the wrapper, so a
// host can show the hand over a button and the I-beam over text.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeCursorTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"p\">x</div><div id=\"g\">y</div><input id=\"i\" type=\"text\"><div id=\"d\">plain</div>");
            _doc.SetCss("body{margin:0}div,input{display:block;width:200px;height:20px}#p{cursor:pointer}#g{cursor:url(hand.png), grab}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        private string Over(string selector, double dx)
        {
            Assert.That(_doc.TryGetBounds(_doc.Query(selector), out NativeBounds b));
            _doc.SetPointer(b.X + dx, b.Y + b.Height / 2);
            _doc.Update(0);
            return _doc.Cursor;
        }

        [Test]
        public void Cursor_FollowsThePointer()
        {
            Assert.That(Over("#p", 100), Is.EqualTo("pointer"));
            Assert.That(Over("#g", 100), Is.EqualTo("grab"), "a url() list falls back to its keyword");
            Assert.That(Over("#i", 100), Is.EqualTo("text"), "auto over a text field");
            Assert.That(Over("#d", 2), Is.EqualTo("text"), "auto over the word");
            Assert.That(Over("#d", 150), Is.EqualTo("default"), "auto beside it");
        }

        [Test]
        public void Cursor_DefaultWithNoPointer_AndCursorAtAnyPoint()
        {
            Over("#p", 100);
            _doc.ClearPointer();
            _doc.Update(0);
            Assert.That(_doc.Cursor, Is.EqualTo("default"));
            Assert.That(_doc.CursorAt(100, 10), Is.EqualTo("pointer"));
        }
    }
}
