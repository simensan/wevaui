// ABI minor 28: the host's colour-scheme switch reaches light-dark() and
// prefers-color-scheme through the C# wrapper, and flipping it restyles a
// live document.
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeColorSchemeTests
    {
        private NativeDocument _doc;

        [SetUp]
        public void Open()
        {
            _doc = new NativeDocument(400, 300);
            _doc.LoadHtml("<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>");
            _doc.SetCss("body{margin:0}"
                + "#a{width:100px;height:20px;background-color:light-dark(rgb(255, 0, 0), rgb(0, 0, 255))}"
                + "#b{width:100px;height:20px;background-color:rgb(0, 255, 0)}"
                + "@media (prefers-color-scheme: dark){#b{display:none}}"
                + "#c{color-scheme:dark;color:light-dark(rgb(1, 1, 1), rgb(2, 2, 2))}");
            _doc.Update(0);
        }

        [TearDown]
        public void Close()
        {
            _doc.Dispose();
        }

        private string Computed(string selector, string property)
        {
            foreach (KeyValuePair<string, string> kv in _doc.ComputedStyle(_doc.Query(selector)))
            {
                if (kv.Key == property) return kv.Value;
            }
            return null;
        }

        [Test]
        public void Default_IsLight()
        {
            Assert.That(Computed("#a", "background-color"), Is.EqualTo("rgb(255, 0, 0)"));
            Assert.That(Computed("#b", "display"), Is.EqualTo("block"));
            Assert.That(Computed("#c", "color"), Is.EqualTo("rgb(2, 2, 2)"), "the element's own color-scheme wins");
        }

        [Test]
        public void Dark_SwitchesLightDarkAndMediaRules()
        {
            _doc.SetColorScheme(true);
            _doc.Update(0);
            Assert.That(Computed("#a", "background-color"), Is.EqualTo("rgb(0, 0, 255)"));
            Assert.That(Computed("#b", "display"), Is.EqualTo("none"));
            Assert.That(Computed("#c", "color"), Is.EqualTo("rgb(2, 2, 2)"));

            _doc.SetColorScheme(false);
            _doc.Update(0);
            Assert.That(Computed("#a", "background-color"), Is.EqualTo("rgb(255, 0, 0)"));
            Assert.That(Computed("#b", "display"), Is.EqualTo("block"));
        }

        [Test]
        public void Dark_ChangesWhatIsDrawn()
        {
            int light = _doc.Draws().Length;
            _doc.SetColorScheme(true);
            _doc.Update(0);
            Assert.That(_doc.Draws().Length, Is.LessThan(light), "#b is display:none under the dark scheme");
        }
    }
}
