using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // TG1 — OverflowResolver direct coverage.
    // CSS Overflow L4 §6: decides clip eligibility (ShouldClip) and
    // resolves overflow-clip-margin (shorthand + per-side longhands +
    // <visual-box>? <length>? grammar). overflow-clip-margin only
    // applies on an `overflow: clip` axis — `visible` must ignore it.
    public class OverflowResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Overflow_visible_does_not_clip() {
            var s = Style();
            s.Set("overflow", "visible");
            Assert.That(OverflowResolver.ShouldClip(s), Is.False);
            Assert.That(OverflowResolver.IsOverflowClip(s), Is.False);
        }

        [Test]
        public void Overflow_hidden_auto_scroll_clip_all_should_clip() {
            foreach (var kw in new[] { "hidden", "auto", "scroll", "clip" }) {
                var s = Style();
                s.Set("overflow", kw);
                Assert.That(OverflowResolver.ShouldClip(s), Is.True,
                    "overflow:" + kw + " must report ShouldClip=true");
            }
            // Only `clip` should report IsOverflowClip — the other three
            // clip at the padding box but do NOT honour overflow-clip-margin.
            var clipOnly = Style();
            clipOnly.Set("overflow", "clip");
            Assert.That(OverflowResolver.IsOverflowClip(clipOnly), Is.True);
            var hidden = Style();
            hidden.Set("overflow", "hidden");
            Assert.That(OverflowResolver.IsOverflowClip(hidden), Is.False);
        }

        [Test]
        public void Overflow_clip_with_clip_margin_8px_resolves_to_8() {
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "8px");
            // Shorthand reads as 8 px on the side-less resolver.
            Assert.That(OverflowResolver.ResolveClipMargin(s, Ctx()),
                Is.EqualTo(8.0).Within(1e-6));
            // And it propagates to every per-side reader (the shorthand
            // applies to all four sides per CSS Overflow L4 §6).
            Assert.That(OverflowResolver.ResolveClipMarginTop(s, Ctx()),
                Is.EqualTo(8.0).Within(1e-6));
            Assert.That(OverflowResolver.ResolveClipMarginBottom(s, Ctx()),
                Is.EqualTo(8.0).Within(1e-6));
        }

        [Test]
        public void Per_side_clip_margin_top_longhand_honored() {
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-top", "12px");
            Assert.That(OverflowResolver.ResolveClipMarginTop(s, Ctx()),
                Is.EqualTo(12.0).Within(1e-6));
            // Other sides fall back to the shorthand (0px initial) when
            // no longhand is set on that side.
            Assert.That(OverflowResolver.ResolveClipMarginRight(s, Ctx()),
                Is.EqualTo(0.0).Within(1e-6));
            Assert.That(OverflowResolver.ResolveClipMarginBottom(s, Ctx()),
                Is.EqualTo(0.0).Within(1e-6));
            Assert.That(OverflowResolver.ResolveClipMarginLeft(s, Ctx()),
                Is.EqualTo(0.0).Within(1e-6));
        }

        [Test]
        public void Visible_ignores_clip_margin_regression_pin() {
            // CSS Overflow L4 §6: overflow-clip-margin only takes effect
            // when an overflow axis is `clip`. With `visible`, ShouldClip
            // must be false even if a margin value is declared.
            var s = Style();
            s.Set("overflow", "visible");
            s.Set("overflow-clip-margin", "16px");
            Assert.That(OverflowResolver.ShouldClip(s), Is.False);
            Assert.That(OverflowResolver.IsOverflowClip(s), Is.False);
            // Resolver still returns the margin value (the consumer is
            // expected to gate on IsOverflowClip), but ShouldClip is
            // the public predicate paint asks before clipping at all.
        }

        [Test]
        public void Per_axis_overflow_x_or_y_alone_triggers_clip() {
            // Setting only overflow-x to hidden must still report
            // ShouldClip = true (the resolver ORs across all three
            // longhand reads).
            var sx = Style();
            sx.Set("overflow-x", "hidden");
            Assert.That(OverflowResolver.ShouldClip(sx), Is.True);
            var sy = Style();
            sy.Set("overflow-y", "scroll");
            Assert.That(OverflowResolver.ShouldClip(sy), Is.True);
        }

        [Test]
        public void Clip_margin_visual_box_keyword_resolves_per_side() {
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "content-box 4px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxTop(s),
                Is.EqualTo(OverflowClipMarginBox.ContentBox));
            Assert.That(OverflowResolver.ResolveClipMargin(s, Ctx()),
                Is.EqualTo(4.0).Within(1e-6));
            // Default for the shorthand when keyword omitted is padding-box.
            var s2 = Style();
            s2.Set("overflow", "clip");
            s2.Set("overflow-clip-margin", "2px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxTop(s2),
                Is.EqualTo(OverflowClipMarginBox.PaddingBox));
        }

        [Test]
        public void Null_style_returns_safe_defaults() {
            Assert.That(OverflowResolver.ShouldClip(null), Is.False);
            Assert.That(OverflowResolver.IsOverflowClip(null), Is.False);
            Assert.That(OverflowResolver.ResolveClipMargin(null, Ctx()),
                Is.EqualTo(0.0).Within(1e-6));
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxTop(null),
                Is.EqualTo(OverflowClipMarginBox.PaddingBox));
        }
    }
}
