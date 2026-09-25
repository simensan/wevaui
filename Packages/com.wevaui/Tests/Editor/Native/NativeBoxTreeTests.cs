// ABI minor 31: the box tree as a list through the wrapper, for an outline
// overlay and a box-tree view.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeBoxTreeTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\"><span id=\"s\">hi</span></div>");
            _doc.SetCss("body{margin:0}#a{padding:5px;border:2px solid #000;margin:3px;width:100px}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        [Test]
        public void Boxes_ListTheTreeInOrderWithEdgesAndText()
        {
            weva_box[] boxes = _doc.Boxes();
            Assert.That(boxes.Length, Is.GreaterThanOrEqualTo(5));
            Assert.That(boxes[0].parent, Is.EqualTo(WevaNative.WEVA_BOX_NONE));
            for (int i = 1; i < boxes.Length; i++) Assert.That(boxes[i].parent, Is.LessThan((uint)i), "parents come first");
            uint a = _doc.Query("#a");
            int aAt = -1, textAt = -1;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i].element == a && boxes[i].kind == (uint)weva_box_kind.WEVA_BOX_BLOCK) aAt = i;
                if (boxes[i].kind == (uint)weva_box_kind.WEVA_BOX_TEXT && NativeDocument.TextOf(boxes[i]) == "hi") textAt = i;
            }
            Assert.That(aAt, Is.GreaterThanOrEqualTo(0));
            Assert.That(textAt, Is.GreaterThanOrEqualTo(0));
            Assert.That(_doc.TryGetBounds(a, out NativeBounds b));
            Assert.That(boxes[aAt].x, Is.EqualTo(b.X));
            Assert.That(boxes[aAt].width, Is.EqualTo(b.Width));
            Assert.That(boxes[aAt].padding_left, Is.EqualTo(5.0));
            Assert.That(boxes[aAt].border_top, Is.EqualTo(2.0));
            Assert.That(boxes[textAt].element, Is.EqualTo(_doc.Query("#s")), "a text run names its element");
            Assert.That(boxes[boxes[textAt].parent].kind, Is.EqualTo((uint)weva_box_kind.WEVA_BOX_LINE), "under its line");
        }
    }
}
