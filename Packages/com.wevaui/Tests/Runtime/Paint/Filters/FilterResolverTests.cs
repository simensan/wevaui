using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class FilterResolverTests {
        const double Eps = 1e-6;
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Null_style_returns_empty() {
            var c = FilterResolver.Resolve(null, Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }

        [Test]
        public void Filter_absent_returns_empty() {
            var s = Style();
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }

        [Test]
        public void Filter_none_returns_empty() {
            var s = Style();
            s.Set("filter", "none");
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }

        [Test]
        public void Filter_blur_present_returns_chain() {
            var s = Style();
            s.Set("filter", "blur(5px)");
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.IsEmpty, Is.False);
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
        }

        [Test]
        public void Multi_filter_chain_returned() {
            var s = Style();
            s.Set("filter", "blur(2px) brightness(1.1) contrast(1.2)");
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(3));
        }

        [Test]
        public void DropShadow_currentcolor_resolves_to_style_color() {
            var s = Style();
            s.Set("color", "blue");
            s.Set("filter", "drop-shadow(2px 4px)");
            var c = FilterResolver.Resolve(s, Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.Color.B, Is.GreaterThan(0.5f));
            Assert.That(f.Color.R, Is.LessThan(0.1f));
        }

        [Test]
        public void DropShadow_explicit_currentcolor_keyword_resolves_to_style_color() {
            var s = Style();
            s.Set("color", "red");
            s.Set("filter", "drop-shadow(2px 4px 8px currentColor)");
            var c = FilterResolver.Resolve(s, Ctx());
            var f = (DropShadowFilter)c.Functions[0];
            Assert.That(f.Color.R, Is.GreaterThan(0.5f));
            Assert.That(f.Color.B, Is.LessThan(0.1f));
        }

        [Test]
        public void Bad_filter_text_throws_FilterParseException() {
            // v1 documented behavior: bad filter text throws (resolver does not swallow).
            var s = Style();
            s.Set("filter", "wobble(50%)");
            Assert.Throws<FilterParseException>(() => FilterResolver.Resolve(s, Ctx()));
        }

        [Test]
        public void Empty_filter_string_returns_empty() {
            var s = Style();
            s.Set("filter", "");
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }
    }
}
