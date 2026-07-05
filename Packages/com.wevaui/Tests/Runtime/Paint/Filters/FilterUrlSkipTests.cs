using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class FilterUrlSkipTests {
        const double Eps = 1e-6;
        static LengthContext Ctx() => LengthContext.Default;
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        [Test]
        public void Parser_url_between_functions_is_skipped_others_apply() {
            FilterChain c = null;
            Assert.DoesNotThrow(() => {
                c = FilterParser.Parse("blur(4px) url(#sepia) brightness(0.8)", Ctx());
            });
            Assert.That(c, Is.Not.Null);
            Assert.That(c.IsEmpty, Is.False);
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
            Assert.That(((BlurFilter)c.Functions[0]).RadiusPx, Is.EqualTo(4).Within(Eps));
            Assert.That(((BrightnessFilter)c.Functions[1]).Amount, Is.EqualTo(0.8).Within(Eps));
        }

        [Test]
        public void Parser_quoted_url_function_is_skipped() {
            FilterChain c = null;
            Assert.DoesNotThrow(() => {
                c = FilterParser.Parse("blur(4px) url(\"sprites.svg#sepia\") brightness(0.8)", Ctx());
            });
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
        }

        [Test]
        public void Parser_url_at_end_still_emits_leading_functions() {
            var c = FilterParser.Parse("blur(2px) url(#x)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
        }

        [Test]
        public void Parser_url_only_yields_empty_chain() {
            var c = FilterParser.Parse("url(#sepia)", Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }

        [Test]
        public void Parser_regression_plain_blur_still_parses() {
            var c = FilterParser.Parse("blur(4px)", Ctx());
            Assert.That(c.Functions.Count, Is.EqualTo(1));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(((BlurFilter)c.Functions[0]).RadiusPx, Is.EqualTo(4).Within(Eps));
        }

        [Test]
        public void Resolver_url_between_functions_is_skipped_others_apply() {
            var s = Style();
            s.Set("filter", "blur(4px) url(#sepia) brightness(0.8)");
            FilterChain c = null;
            Assert.DoesNotThrow(() => { c = FilterResolver.Resolve(s, Ctx()); });
            Assert.That(c, Is.Not.Null);
            Assert.That(c.IsEmpty, Is.False);
            Assert.That(c.Functions.Count, Is.EqualTo(2));
            Assert.That(c.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(c.Functions[1], Is.InstanceOf<BrightnessFilter>());
        }

        [Test]
        public void Resolver_url_only_yields_empty_chain() {
            var s = Style();
            s.Set("filter", "url(#sepia)");
            var c = FilterResolver.Resolve(s, Ctx());
            Assert.That(c.IsEmpty, Is.True);
        }
    }
}
