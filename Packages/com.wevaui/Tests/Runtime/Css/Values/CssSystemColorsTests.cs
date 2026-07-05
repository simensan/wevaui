using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssSystemColorsTests {
        static CssColor Resolve(string name) {
            Assert.That(CssColor.TryFromName(name, out var c), Is.True, "TryFromName failed for '" + name + "'");
            return c;
        }

        [Test]
        public void Canvas_is_white() {
            var c = Resolve("Canvas");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(255));
            Assert.That(c.A, Is.EqualTo(1f));
        }

        [Test]
        public void CanvasText_is_black() {
            var c = Resolve("CanvasText");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void ButtonText_is_black() {
            var c = Resolve("ButtonText");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void ButtonFace_is_light_grey() {
            var c = Resolve("ButtonFace");
            Assert.That(c.R, Is.EqualTo(221));
            Assert.That(c.G, Is.EqualTo(221));
            Assert.That(c.B, Is.EqualTo(221));
        }

        [Test]
        public void LinkText_is_classic_browser_blue() {
            var c = Resolve("LinkText");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(238));
        }

        [Test]
        public void VisitedText_is_purple() {
            var c = Resolve("VisitedText");
            Assert.That(c.R, Is.EqualTo(85));
            Assert.That(c.G, Is.EqualTo(26));
            Assert.That(c.B, Is.EqualTo(139));
        }

        [Test]
        public void Highlight_is_selection_blue() {
            var c = Resolve("Highlight");
            Assert.That(c.R, Is.EqualTo(180));
            Assert.That(c.G, Is.EqualTo(213));
            Assert.That(c.B, Is.EqualTo(254));
        }

        [Test]
        public void HighlightText_is_black() {
            var c = Resolve("HighlightText");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void Mark_is_yellow() {
            var c = Resolve("Mark");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void MarkText_is_black() {
            var c = Resolve("MarkText");
            Assert.That(c.R, Is.EqualTo(0));
            Assert.That(c.G, Is.EqualTo(0));
            Assert.That(c.B, Is.EqualTo(0));
        }

        [Test]
        public void GrayText_is_mid_grey() {
            var c = Resolve("GrayText");
            Assert.That(c.R, Is.EqualTo(128));
            Assert.That(c.G, Is.EqualTo(128));
            Assert.That(c.B, Is.EqualTo(128));
        }

        [Test]
        public void SelectedItem_matches_Highlight() {
            var sel = Resolve("SelectedItem");
            var hl = Resolve("Highlight");
            Assert.That(sel.R, Is.EqualTo(hl.R));
            Assert.That(sel.G, Is.EqualTo(hl.G));
            Assert.That(sel.B, Is.EqualTo(hl.B));
        }

        [Test]
        public void SelectedItemText_matches_HighlightText() {
            var sel = Resolve("SelectedItemText");
            var hl = Resolve("HighlightText");
            Assert.That(sel.R, Is.EqualTo(hl.R));
            Assert.That(sel.G, Is.EqualTo(hl.G));
            Assert.That(sel.B, Is.EqualTo(hl.B));
        }

        [Test]
        public void AccentColor_is_blue() {
            var c = Resolve("AccentColor");
            Assert.That(c.B, Is.GreaterThan(c.R));
            Assert.That(c.B, Is.GreaterThan(c.G));
        }

        [Test]
        public void AccentColorText_is_white() {
            var c = Resolve("AccentColorText");
            Assert.That(c.R, Is.EqualTo(255));
            Assert.That(c.G, Is.EqualTo(255));
            Assert.That(c.B, Is.EqualTo(255));
        }

        [Test]
        public void Lookup_is_ascii_case_insensitive() {
            Assert.That(CssColor.TryFromName("CANVAS", out var upper), Is.True);
            Assert.That(CssColor.TryFromName("canvas", out var lower), Is.True);
            Assert.That(CssColor.TryFromName("Canvas", out var mixed), Is.True);
            Assert.That(upper.R, Is.EqualTo(lower.R));
            Assert.That(upper.G, Is.EqualTo(lower.G));
            Assert.That(upper.B, Is.EqualTo(lower.B));
            Assert.That(mixed.R, Is.EqualTo(lower.R));
        }

        [Test]
        public void CanvasText_case_insensitive() {
            Assert.That(CssColor.TryFromName("canvastext", out var lower), Is.True);
            Assert.That(CssColor.TryFromName("CanvasText", out var camel), Is.True);
            Assert.That(CssColor.TryFromName("CANVASTEXT", out var upper), Is.True);
            Assert.That(camel.R, Is.EqualTo(lower.R));
            Assert.That(upper.G, Is.EqualTo(lower.G));
        }
    }
}
