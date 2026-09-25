// ABI minor 35: what an update changed, per element, and the structure counter.
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeChangeTests
    {
        [Test]
        public void ChangedElements_ListTheRestyledOnes()
        {
            using (var doc = new NativeDocument(400, 300))
            {
                doc.LoadHtml("<div id=\"a\"></div><div id=\"b\"></div>");
                doc.SetCss("body{margin:0}div{width:100px;height:20px}");
                doc.Update(0);
                ulong structure = doc.StructureVersion;
                doc.SetCss("body{margin:0}div{width:100px;height:20px}#a{background:red}#b{width:50px}");
                doc.Update(0);
                weva_element_change[] changes = doc.ChangedElements();
                int aKind = 0, bKind = 0;
                foreach (weva_element_change c in changes)
                {
                    if (c.element == doc.Query("#a")) aKind = c.kind;
                    if (c.element == doc.Query("#b")) bKind = c.kind;
                }
                Assert.That(aKind, Is.EqualTo((int)weva_change_kind.WEVA_CHANGE_PAINT));
                Assert.That(bKind, Is.GreaterThanOrEqualTo((int)weva_change_kind.WEVA_CHANGE_LAYOUT));
                Assert.That(doc.StructureVersion, Is.EqualTo(structure));
                doc.ReloadHtml("<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>");
                doc.Update(0);
                Assert.That(doc.StructureVersion, Is.GreaterThan(structure));
            }
        }
    }
}
