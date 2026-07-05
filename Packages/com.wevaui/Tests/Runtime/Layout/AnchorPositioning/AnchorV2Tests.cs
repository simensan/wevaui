using NUnit.Framework;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.AnchorPositioning {
    public class AnchorV2Tests {
        static Box FindByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null) {
                    var raw = b.Element.ClassName;
                    if (!string.IsNullOrEmpty(raw)) {
                        foreach (var c in raw.Split(' ')) {
                            if (c == cls) return b;
                        }
                    }
                }
            }
            return null;
        }

        // ---- anchor-size() parser ----

        [Test]
        public void AnchorSize_parses_axis_only() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(width)", out var call), Is.True);
            Assert.That(call.AnchorName, Is.Null);
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Width));
        }

        [Test]
        public void AnchorSize_parses_name_and_axis() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(--btn width)", out var call), Is.True);
            Assert.That(call.AnchorName, Is.EqualTo("--btn"));
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Width));
        }

        [Test]
        public void AnchorSize_parses_height_axis() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(--btn height)", out var call), Is.True);
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Height));
        }

        [Test]
        public void AnchorSize_parses_inline_alias() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(inline)", out var call), Is.True);
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Width));
        }

        [Test]
        public void AnchorSize_parses_block_alias() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(block)", out var call), Is.True);
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Height));
        }

        [Test]
        public void AnchorSize_parses_name_only() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(--btn)", out var call), Is.True);
            Assert.That(call.AnchorName, Is.EqualTo("--btn"));
            Assert.That(call.Axis, Is.EqualTo(AnchorSizeAxis.Inferred));
        }

        [Test]
        public void AnchorSize_rejects_unknown_axis() {
            Assert.That(AnchorFunctionParser.TryParseSize("anchor-size(--btn diag)", out _), Is.False);
        }

        // ---- anchor-size() resolution & wiring ----

        [Test]
        public void AnchorSize_width_resolves_from_anchor_box() {
            const string css = @"
                .btn { width: 120px; height: 30px; anchor-name: --btn; }
                .tip { position: absolute; position-anchor: --btn;
                       width: anchor-size(--btn width); height: 20px; }
            ";
            var (root, _, _) = Build(@"<div class=""btn""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip, Is.Not.Null);
            Assert.That(tip.Width, Is.EqualTo(120).Within(0.5));
        }

        [Test]
        public void AnchorSize_implicit_anchor_uses_position_anchor() {
            const string css = @"
                .btn { width: 80px; height: 30px; anchor-name: --btn; }
                .tip { position: absolute; position-anchor: --btn;
                       width: anchor-size(width); height: 20px; }
            ";
            var (root, _, _) = Build(@"<div class=""btn""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.Width, Is.EqualTo(80).Within(0.5));
        }

        [Test]
        public void AnchorSize_height_resolves_axis() {
            const string css = @"
                .btn { width: 80px; height: 50px; anchor-name: --btn; }
                .tip { position: absolute; position-anchor: --btn;
                       width: 60px; height: anchor-size(--btn height); }
            ";
            var (root, _, _) = Build(@"<div class=""btn""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.Height, Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void AnchorSize_unknown_anchor_falls_back_to_initial() {
            const string css = @"
                .tip { position: absolute; position-anchor: --missing;
                       width: anchor-size(--missing width); height: 20px; }
            ";
            var (root, _, _) = Build(@"<div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip, Is.Not.Null);
            // Anchor isn't found — width left unresolved → block default fills container.
            Assert.That(tip.Width, Is.GreaterThan(0));
        }

        // ---- position-try-fallbacks parsing ----

        [Test]
        public void PositionTryFallbacks_parse_flip_block() {
            var list = PositionTryFallbacks.Parse("flip-block");
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0], Is.EqualTo(PositionTryFallbacks.Strategy.FlipBlock));
        }

        [Test]
        public void PositionTryFallbacks_parse_combined() {
            var list = PositionTryFallbacks.Parse("flip-block, flip-inline, flip-block flip-inline");
            Assert.That(list, Has.Count.EqualTo(3));
            Assert.That(list[0], Is.EqualTo(PositionTryFallbacks.Strategy.FlipBlock));
            Assert.That(list[1], Is.EqualTo(PositionTryFallbacks.Strategy.FlipInline));
            Assert.That(list[2], Is.EqualTo(PositionTryFallbacks.Strategy.FlipBlockInline));
        }

        [Test]
        public void PositionTryFallbacks_parse_unknown_keyword_dropped() {
            var list = PositionTryFallbacks.Parse("flip-block, mystery");
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0], Is.EqualTo(PositionTryFallbacks.Strategy.FlipBlock));
        }

        // ---- position-try-fallbacks layout ----

        [Test]
        public void TryFallback_flip_block_fires_when_bottom_overflows() {
            // Anchor sits near the bottom, so `top: anchor(bottom)` would put
            // the tip below the viewport. flip-block should swap to placing it
            // above the anchor (`bottom: anchor(top)`).
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 80px; height: 30px; anchor-name: --tip;
                          margin-top: 290px; }
                .tip {
                    position: absolute; position-anchor: --tip;
                    top: anchor(bottom); left: anchor(left);
                    width: 50px; height: 50px;
                    position-try-fallbacks: flip-block;
                }
            ";
            var (root, _, ctx) = Build(@"<div class=""anchor""></div><div class=""tip""></div>",
                                       css, viewportHeight: 320);
            var tip = FindByClass(root, "tip");
            // Anchor top = 290, bottom = 320; tip would land at y=320 (off-viewport).
            // flip-block: top<->bottom — `bottom: anchor(bottom)` places the tip's
            // bottom edge at the anchor's bottom edge, so the tip itself is
            // anchor.bottom-tip.height = 320 - 50 = 270.
            double absY = AbsY(tip);
            Assert.That(absY + tip.Height, Is.LessThanOrEqualTo(320 + 0.5));
        }

        [Test]
        public void TryFallback_flip_inline_fires_when_right_overflows() {
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 60px; height: 30px; anchor-name: --t;
                          margin-left: 350px; }
                .tip {
                    position: absolute; position-anchor: --t;
                    top: anchor(bottom); left: anchor(right);
                    width: 80px; height: 20px;
                    position-try-fallbacks: flip-inline;
                }
            ";
            var (root, _, _) = Build(@"<div class=""anchor""></div><div class=""tip""></div>",
                                     css, viewportWidth: 400);
            var tip = FindByClass(root, "tip");
            // Anchor left = 350, right = 410. left:anchor(right) → x=410 (off-viewport
            // for w=80). flip-inline swaps left<->right so tip.right = anchor.right
            // edge — tip should fit inside the viewport now.
            double absX = AbsX(tip);
            Assert.That(absX + tip.Width, Is.LessThanOrEqualTo(400 + 0.5));
        }

        [Test]
        public void TryFallback_combined_flip_when_both_axes_overflow() {
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 60px; height: 30px; anchor-name: --t;
                          margin-top: 290px; margin-left: 350px; }
                .tip {
                    position: absolute; position-anchor: --t;
                    top: anchor(bottom); left: anchor(right);
                    width: 80px; height: 80px;
                    position-try-fallbacks: flip-block flip-inline;
                }
            ";
            var (root, _, _) = Build(@"<div class=""anchor""></div><div class=""tip""></div>",
                                     css, viewportWidth: 400, viewportHeight: 320);
            var tip = FindByClass(root, "tip");
            double absX = AbsX(tip);
            double absY = AbsY(tip);
            Assert.That(absX, Is.GreaterThanOrEqualTo(-0.5));
            Assert.That(absY, Is.GreaterThanOrEqualTo(-0.5));
            Assert.That(absX + tip.Width, Is.LessThanOrEqualTo(400 + 0.5));
            Assert.That(absY + tip.Height, Is.LessThanOrEqualTo(320 + 0.5));
        }

        [Test]
        public void TryFallback_no_fit_retains_original() {
            // Anchor at right edge, tip wider than viewport — neither fallback
            // can fit. Original position should be retained.
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 60px; height: 30px; anchor-name: --t;
                          margin-left: 350px; }
                .tip {
                    position: absolute; position-anchor: --t;
                    top: anchor(bottom); left: anchor(right);
                    width: 500px; height: 20px;
                    position-try-fallbacks: flip-block, flip-inline, flip-block flip-inline;
                }
            ";
            var (root, _, _) = Build(@"<div class=""anchor""></div><div class=""tip""></div>",
                                     css, viewportWidth: 400);
            var tip = FindByClass(root, "tip");
            // Tip is wider than viewport, no fallback fits. We just check the
            // tip still has the original left position (~410) — i.e. the
            // restore branch fired without leaving a partial mutation.
            double absX = AbsX(tip);
            Assert.That(absX, Is.EqualTo(410).Within(0.5));
        }

        [Test]
        public void TryFallback_no_overflow_no_change() {
            // Tip fits originally — fallback list should be a no-op.
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 60px; height: 30px; anchor-name: --t; }
                .tip {
                    position: absolute; position-anchor: --t;
                    top: anchor(bottom); left: anchor(left);
                    width: 50px; height: 20px;
                    position-try-fallbacks: flip-block;
                }
            ";
            var (root, _, _) = Build(@"<div class=""anchor""></div><div class=""tip""></div>",
                                     css, viewportWidth: 400, viewportHeight: 200);
            var tip = FindByClass(root, "tip");
            // Anchor at (0,0) 60x30. Tip top: anchor(bottom) = 30. Should NOT flip.
            Assert.That(AbsY(tip), Is.EqualTo(30).Within(0.5));
            Assert.That(AbsX(tip), Is.EqualTo(0).Within(0.5));
        }

        static double AbsX(Box b) {
            double x = 0;
            for (var p = b; p != null; p = p.Parent) x += p.X;
            return x;
        }
        static double AbsY(Box b) {
            double y = 0;
            for (var p = b; p != null; p = p.Parent) y += p.Y;
            return y;
        }
    }
}
