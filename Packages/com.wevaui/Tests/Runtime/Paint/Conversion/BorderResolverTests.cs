using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    public class BorderResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void All_four_edges_set_independently() {
            var s = Style();
            s.Set("color", "black");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "1px");
            s.Set("border-top-color", "red");
            s.Set("border-right-style", "solid");
            s.Set("border-right-width", "2px");
            s.Set("border-right-color", "green");
            s.Set("border-bottom-style", "solid");
            s.Set("border-bottom-width", "3px");
            s.Set("border-bottom-color", "blue");
            s.Set("border-left-style", "solid");
            s.Set("border-left-width", "4px");
            s.Set("border-left-color", "white");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Style, Is.EqualTo(BorderStyle.Solid));
            Assert.That(b.Top.Width, Is.EqualTo(1).Within(1e-6));
            Assert.That(b.Right.Width, Is.EqualTo(2).Within(1e-6));
            Assert.That(b.Bottom.Width, Is.EqualTo(3).Within(1e-6));
            Assert.That(b.Left.Width, Is.EqualTo(4).Within(1e-6));
        }

        [Test]
        public void Single_edge_with_longhands_emits_one_active_edge() {
            var s = Style();
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "5px");
            s.Set("border-top-color", "red");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Style, Is.EqualTo(BorderStyle.Solid));
            Assert.That(b.Right.Style, Is.EqualTo(BorderStyle.None));
            Assert.That(b.Bottom.Style, Is.EqualTo(BorderStyle.None));
            Assert.That(b.Left.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Style_none_yields_no_stroke() {
            var s = Style();
            s.Set("border-top-style", "none");
            s.Set("border-top-width", "5px");
            s.Set("border-top-color", "red");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.IsNone, Is.True);
        }

        [Test]
        public void Default_color_falls_back_to_color_property() {
            var s = Style();
            s.Set("color", "red");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "1px");
            s.Set("border-top-color", "currentcolor");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Color.R, Is.GreaterThan(0.5f));
            Assert.That(b.Top.Color.G, Is.LessThan(0.05f));
        }

        [Test]
        public void Width_zero_yields_no_stroke() {
            var s = Style();
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "0px");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Mixed_widths_per_edge() {
            var s = Style();
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "thin");
            s.Set("border-right-style", "solid");
            s.Set("border-right-width", "medium");
            s.Set("border-bottom-style", "solid");
            s.Set("border-bottom-width", "thick");
            s.Set("border-left-style", "solid");
            s.Set("border-left-width", "10px");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Width, Is.EqualTo(1).Within(1e-6));
            Assert.That(b.Right.Width, Is.EqualTo(3).Within(1e-6));
            Assert.That(b.Bottom.Width, Is.EqualTo(5).Within(1e-6));
            Assert.That(b.Left.Width, Is.EqualTo(10).Within(1e-6));
        }

        [Test]
        public void Dashed_dotted_double_styles_round_trip() {
            var s = Style();
            s.Set("border-top-style", "dashed");
            s.Set("border-top-width", "1px");
            s.Set("border-right-style", "dotted");
            s.Set("border-right-width", "1px");
            s.Set("border-bottom-style", "double");
            s.Set("border-bottom-width", "1px");
            s.Set("border-left-style", "solid");
            s.Set("border-left-width", "1px");
            var b = BorderResolver.ResolveBorders(s, Ctx());
            Assert.That(b.Top.Style, Is.EqualTo(BorderStyle.Dashed));
            Assert.That(b.Right.Style, Is.EqualTo(BorderStyle.Dotted));
            Assert.That(b.Bottom.Style, Is.EqualTo(BorderStyle.Double));
            Assert.That(b.Left.Style, Is.EqualTo(BorderStyle.Solid));
        }
    }
}
