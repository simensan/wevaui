using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // text-shadow shares its parsing shape with box-shadow minus `inset` and
    // `spread`. These tests lock in the small differences and the shadow
    // ordering rule (first listed paints on top).
    public class TextShadowResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("p"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void None_yields_empty_array() {
            var s = Style();
            s.Set("text-shadow", "none");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(0));
        }

        [Test]
        public void Empty_returns_empty() {
            var s = Style();
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(0));
        }

        [Test]
        public void Two_lengths_only_parses_offset_with_zero_blur() {
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "1px 2px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].OffsetX, Is.EqualTo(1).Within(1e-6));
            Assert.That(arr[0].OffsetY, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Three_lengths_parses_offset_and_blur() {
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "1px 2px 3px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(3).Within(1e-6));
        }

        [Test]
        public void Color_after_lengths_resolves() {
            var s = Style();
            s.Set("text-shadow", "2px 2px 0 red");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(arr[0].Color.G, Is.LessThan(0.05f));
        }

        [Test]
        public void Color_before_lengths_also_works() {
            // CSS spec allows color either before or after the lengths.
            var s = Style();
            s.Set("text-shadow", "blue 2px 2px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Color.B, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Default_color_falls_back_to_color_property() {
            // Per CSS spec when text-shadow has no color, the current `color`
            // property is used (currentColor).
            var s = Style();
            s.Set("color", "red");
            s.Set("text-shadow", "2px 2px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(arr[0].Color.G, Is.LessThan(0.05f));
        }

        [Test]
        public void Multiple_shadows_split_on_commas() {
            var s = Style();
            s.Set("text-shadow", "1px 1px red, 2px 2px blue, 3px 3px green");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(3));
            Assert.That(arr[0].OffsetX, Is.EqualTo(1).Within(1e-6));
            Assert.That(arr[1].OffsetX, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[2].OffsetX, Is.EqualTo(3).Within(1e-6));
        }

        [Test]
        public void Negative_offsets_supported() {
            // Shadows above-left of glyphs are common for chiseled-look HUD
            // text. Negative offsets are valid per CSS.
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "-1px -1px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].OffsetX, Is.EqualTo(-1).Within(1e-6));
            Assert.That(arr[0].OffsetY, Is.EqualTo(-1).Within(1e-6));
        }

        [Test]
        public void Negative_blur_clamps_to_zero() {
            // CSS spec: blur-radius cannot be negative — clamp at 0 instead
            // of failing the parse. (Matches Firefox/Chrome behavior.)
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "1px 1px -3px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Em_offsets_resolve_against_base_font_size() {
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "0.25em 0.25em");
            var ctx = LengthContext.Default;
            ctx.BaseFontSizePx = 32;
            var arr = TextShadowResolver.ResolveTextShadow(s, ctx);
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].OffsetX, Is.EqualTo(8).Within(1e-6));
            Assert.That(arr[0].OffsetY, Is.EqualTo(8).Within(1e-6));
        }

        [Test]
        public void Resolve_into_pool_appends() {
            var s = Style();
            s.Set("color", "black");
            s.Set("text-shadow", "1px 1px black, 2px 2px red");
            var pool = new System.Collections.Generic.List<TextShadow>();
            pool.Add(new TextShadow(99, 99, 0, default)); // sentinel
            bool emitted = TextShadowResolver.ResolveTextShadowInto(s, Ctx(), pool);
            Assert.That(emitted, Is.True);
            Assert.That(pool.Count, Is.EqualTo(3)); // sentinel + 2 shadows
            Assert.That(pool[0].OffsetX, Is.EqualTo(99));
            Assert.That(pool[1].OffsetX, Is.EqualTo(1).Within(1e-6));
            Assert.That(pool[2].OffsetX, Is.EqualTo(2).Within(1e-6));
        }

        [Test]
        public void Single_length_is_invalid() {
            var s = Style();
            s.Set("text-shadow", "1px");
            var arr = TextShadowResolver.ResolveTextShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(0));
        }
    }
}
