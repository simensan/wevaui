using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    public class BoxShadowResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Single_shadow_offset_only() {
            var s = Style();
            s.Set("color", "black");
            s.Set("box-shadow", "2px 4px");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].OffsetX, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[0].OffsetY, Is.EqualTo(4).Within(1e-6));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(0).Within(1e-6));
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(0).Within(1e-6));
            Assert.That(arr[0].Inset, Is.False);
        }

        [Test]
        public void With_blur_and_spread() {
            var s = Style();
            s.Set("box-shadow", "2px 4px 6px 8px red");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].OffsetX, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[0].OffsetY, Is.EqualTo(4).Within(1e-6));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(6).Within(1e-6));
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(8).Within(1e-6));
            Assert.That(arr[0].Color.R, Is.GreaterThan(0.5f));
        }

        [Test]
        public void Inset_keyword_marks_shadow_inset() {
            var s = Style();
            s.Set("box-shadow", "inset 1px 1px 4px black");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Inset, Is.True);
        }

        [Test]
        public void Multiple_shadows_split_on_commas() {
            var s = Style();
            s.Set("box-shadow", "1px 1px red, 2px 2px blue, 3px 3px green");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(3));
            Assert.That(arr[0].OffsetX, Is.EqualTo(1).Within(1e-6));
            Assert.That(arr[1].OffsetX, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[2].OffsetX, Is.EqualTo(3).Within(1e-6));
        }

        [Test]
        public void None_yields_empty_array() {
            var s = Style();
            s.Set("box-shadow", "none");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(0));
        }

        [Test]
        public void Default_color_falls_back_to_color_property() {
            var s = Style();
            s.Set("color", "red");
            s.Set("box-shadow", "2px 2px");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Color.R, Is.GreaterThan(0.5f));
            Assert.That(arr[0].Color.G, Is.LessThan(0.05f));
        }

        [Test]
        public void Empty_shadow_yields_no_results() {
            var s = Style();
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(0));
        }

        [Test]
        public void Inset_with_blur_spread_and_color() {
            var s = Style();
            s.Set("box-shadow", "inset 1px 2px 3px 4px rgb(10, 20, 30)");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Inset, Is.True);
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(4).Within(1e-6));
        }

        // ── §3.2 edge cases — large blur, negative spread, negative blur ──

        [Test]
        public void Negative_spread_radius_is_preserved_as_signed_value() {
            // CSS Backgrounds & Borders L3 §7.2 — spread can be negative
            // (shrinks the shadow). The resolver must preserve the sign so
            // the renderer can inset the shadow rect.
            var s = Style();
            s.Set("box-shadow", "0 0 8px -4px red");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(-4).Within(1e-6),
                "negative spread must be preserved as a signed value");
        }

        [Test]
        public void Negative_spread_with_inset_preserves_sign() {
            // Inset + negative spread is valid; result is an inner shadow
            // grown OUTWARD by |spread| from the box edge.
            var s = Style();
            s.Set("box-shadow", "inset 2px 2px 4px -1px black");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].Inset, Is.True);
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(-1).Within(1e-6));
        }

        [Test]
        public void Negative_blur_is_clamped_or_treated_as_zero() {
            // CSS Backgrounds & Borders L3 §7.2 — "Negative values are
            // invalid" for blur-radius. The resolver should either drop the
            // shadow OR clamp blur to 0. This test pins whichever the
            // engine chooses; the contract is "must not be negative in the
            // emitted shadow record".
            var s = Style();
            s.Set("box-shadow", "0 0 -4px red");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            // Engine may drop entirely (arr.Length == 0) OR clamp blur to 0.
            if (arr.Length > 0) {
                Assert.That(arr[0].BlurRadius, Is.GreaterThanOrEqualTo(0),
                    "blur must not be negative in the emitted shadow record; got "
                    + arr[0].BlurRadius);
            }
        }

        [Test]
        public void Large_blur_radius_passes_through_resolver_unchanged() {
            // Very large blur values shouldn't be silently clamped at the
            // resolver layer; rendering may clamp to a max-sigma for
            // performance, but the resolved record carries the author value.
            var s = Style();
            s.Set("box-shadow", "0 0 999px black");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(999).Within(1e-6),
                "huge blur passes through resolver; clamping is renderer concern");
        }

        [Test]
        public void Zero_offset_zero_blur_negative_spread_yields_inverted_shadow_record() {
            // `box-shadow: 0 0 0 -2px red` — invisible (spread shrinks to
            // zero or below) but a valid declaration. Resolver should emit
            // the record so layout/paint can decide whether to draw.
            var s = Style();
            s.Set("box-shadow", "0 0 0 -2px red");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1),
                "even visually-invisible shadow records propagate through resolver");
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(-2).Within(1e-6));
        }

        [Test]
        public void Negative_spread_larger_than_box_does_not_throw() {
            // Pathological: spread = -1000 on a small box. The resolver
            // must accept this and produce a (likely no-op) shadow record;
            // the renderer is responsible for clipping a degenerate rect.
            var s = Style();
            s.Set("box-shadow", "0 0 0 -1000px red");
            Assert.DoesNotThrow(() => {
                BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            });
        }

        [Test]
        public void Multi_shadow_with_negative_spread_per_layer() {
            // Per-layer state is preserved across a comma-list with negative
            // spread on one entry only.
            var s = Style();
            s.Set("box-shadow", "0 0 4px 2px red, 0 0 4px -2px blue");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(2));
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(2).Within(1e-6));
            Assert.That(arr[1].SpreadRadius, Is.EqualTo(-2).Within(1e-6));
        }

        [Test]
        public void Fractional_blur_and_spread_preserved() {
            // Half-pixel blur and spread are valid CSS — the resolver must
            // preserve subpixel precision (renderer rounds at draw time).
            var s = Style();
            s.Set("box-shadow", "0 0 1.5px 0.5px red");
            var arr = BoxShadowResolver.ResolveBoxShadow(s, Ctx());
            Assert.That(arr.Length, Is.EqualTo(1));
            Assert.That(arr[0].BlurRadius, Is.EqualTo(1.5).Within(1e-6));
            Assert.That(arr[0].SpreadRadius, Is.EqualTo(0.5).Within(1e-6));
        }

        // ── audit PF1: viewport churn must not permanently lock out caching ──

        [Test]
        public void Viewport_churn_does_not_permanently_lock_out_shadow_caching() {
            // The cache key includes the viewport (b107e622, so vw/vh shadows
            // resolve correctly). A window-resize drag therefore mints a new
            // key per frame. Under the old drop-new-on-overflow policy the
            // first 128 (dead) resize keys filled the cap and every shadow in
            // the document was locked out of caching for the process lifetime.
            BoxShadowResolver.ClearCacheForTests();
            var s = Style();
            s.Set("box-shadow", "2px 4px 6px red");
            var output = new List<BoxShadow>();

            // Simulate the resize drag: one declaration, 200 distinct viewports.
            var ctx = LengthContext.Default;
            for (int w = 0; w < 200; w++) {
                ctx.ViewportWidthPx = 800 + w;
                output.Clear();
                BoxShadowResolver.ResolveBoxShadowInto(s, ctx, output);
            }

            // After the churn, a never-seen key must still land in the cache.
            // Drop-new leaves the count frozen at the cap (the insert is
            // silently discarded); slice eviction either grows the count or
            // evicts a slice to make room — both observably change it.
            int before = BoxShadowResolver.CacheCountForTests;
            ctx.ViewportWidthPx = 5000;
            output.Clear();
            BoxShadowResolver.ResolveBoxShadowInto(s, ctx, output);
            Assert.That(BoxShadowResolver.CacheCountForTests, Is.Not.EqualTo(before),
                "a fresh key after cap-overflow churn must still be cached — a frozen count " +
                "means drop-new-on-overflow locked the cache for the process lifetime (audit PF1)");
            Assert.That(output.Count, Is.EqualTo(1), "resolution itself must stay correct");
        }
    }
}
