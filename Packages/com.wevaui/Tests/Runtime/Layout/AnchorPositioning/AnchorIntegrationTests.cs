using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.AnchorPositioning {
    public class AnchorIntegrationTests {
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

        [Test]
        public void Anchor_name_registers_box_in_context() {
            const string css = @"
                .anchor { width: 100px; height: 30px; anchor-name: --tip; }
                .filler { height: 50px; }
            ";
            var (root, _, ctx) = Build(
                @"<div class=""filler""></div><div class=""anchor""></div>",
                css);
            Assert.That(ctx.Anchors.TryResolve("--tip", out var entry), Is.True);
            Assert.That(entry.Anchor, Is.Not.Null);
        }

        [Test]
        public void Tooltip_top_anchor_bottom_positions_below_anchor() {
            const string css = @"
                body { margin: 0; padding: 0; }
                .anchor { width: 100px; height: 30px; anchor-name: --tip; }
                .tip {
                    position: absolute;
                    position-anchor: --tip;
                    top: anchor(bottom);
                    left: anchor(left);
                    width: 80px;
                    height: 20px;
                }
            ";
            var (root, _, ctx) = Build(
                @"<div class=""anchor""></div><div class=""tip""></div>",
                css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip, Is.Not.Null);
            // Anchor sits at Y=0 and is 30 high; expect tooltip Y to be at 30.
            // Body has body { margin: 8 } from UA, so absolute origin offset is the parent's box.
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(30).Within(0.5));
        }

        [Test]
        public void Anchor_offset_is_added_to_resolved_pixels() {
            const string css = @"
                .anchor { width: 100px; height: 30px; anchor-name: --tip; }
                .tip {
                    position: absolute;
                    position-anchor: --tip;
                    top: anchor(bottom + 8px);
                    width: 80px;
                    height: 20px;
                }
            ";
            var (root, _, ctx) = Build(
                @"<div class=""anchor""></div><div class=""tip""></div>",
                css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(38).Within(0.5));
        }

        [Test]
        public void Multiple_anchors_with_same_name_last_wins() {
            const string css = @"
                .a1 { width: 50px; height: 20px; anchor-name: --x; }
                .a2 { width: 50px; height: 30px; anchor-name: --x; }
            ";
            var (_, _, ctx) = Build(
                @"<div class=""a1""></div><div class=""a2""></div>",
                css);
            ctx.Anchors.TryResolve("--x", out var entry);
            Assert.That(entry.Anchor.Height, Is.EqualTo(30));
        }

        [Test]
        public void Missing_anchor_resolves_to_zero_offset() {
            const string css = @"
                .tip {
                    position: absolute;
                    position-anchor: --does-not-exist;
                    top: anchor(bottom);
                    width: 80px;
                    height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip, Is.Not.Null);
            Assert.That(tip.OffsetTop, Is.Not.Null);
            Assert.That(tip.OffsetTop.Value, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Anchor_left_resolves_to_anchor_left_edge() {
            const string css = @"
                body { margin: 0; }
                .anchor { width: 100px; height: 30px; anchor-name: --tip; margin-left: 50px; }
                .tip {
                    position: absolute;
                    position-anchor: --tip;
                    left: anchor(left);
                    top: anchor(bottom);
                    width: 80px;
                    height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""anchor""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.X + ParentX(tip), Is.EqualTo(50).Within(0.5));
        }

        [Test]
        public void Anchor_top_resolves_to_anchor_top_edge() {
            const string css = @"
                body { margin: 0; }
                .filler { height: 30px; }
                .anchor { width: 100px; height: 50px; anchor-name: --tip; }
                .tip {
                    position: absolute;
                    position-anchor: --tip;
                    top: anchor(top);
                    width: 80px; height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""filler""></div><div class=""anchor""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(30).Within(0.5));
        }

        [Test]
        public void Anchor_with_explicit_inline_name_overrides_position_anchor() {
            const string css = @"
                body { margin: 0; }
                .a { width: 50px; height: 50px; anchor-name: --a; }
                .b { width: 50px; height: 30px; anchor-name: --b; margin-top: 100px; }
                .tip {
                    position: absolute;
                    position-anchor: --a;
                    top: anchor(--b bottom);
                    width: 80px; height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""a""></div><div class=""b""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            // --b sits at y=50 + 100 margin = 150, height 30 → bottom edge at 180.
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(180).Within(0.5));
        }

        [Test]
        public void Anchor_center_resolves_axis_aware() {
            const string css = @"
                body { margin: 0; }
                .a { width: 100px; height: 50px; anchor-name: --a; margin-left: 100px; }
                .tip {
                    position: absolute;
                    position-anchor: --a;
                    left: anchor(center);
                    top: anchor(center);
                    width: 80px; height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""a""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            // Anchor X = 100, width 100 → center at 150.
            Assert.That(tip.X + ParentX(tip), Is.EqualTo(150).Within(0.5));
            // Anchor Y = 0, height 50 → center at 25.
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(25).Within(0.5));
        }

        [Test]
        public void Anchor_negative_offset_subtracts_from_edge() {
            const string css = @"
                body { margin: 0; }
                .a { width: 100px; height: 50px; anchor-name: --a; }
                .tip {
                    position: absolute;
                    position-anchor: --a;
                    top: anchor(bottom - 5px);
                    width: 80px; height: 20px;
                }
            ";
            var (root, _, _) = Build(@"<div class=""a""></div><div class=""tip""></div>", css);
            var tip = FindByClass(root, "tip");
            Assert.That(tip.Y + ParentY(tip), Is.EqualTo(45).Within(0.5));
        }

        static double ParentX(Box b) {
            double x = 0;
            for (var p = b.Parent; p != null; p = p.Parent) x += p.X;
            return x;
        }

        static double ParentY(Box b) {
            double y = 0;
            for (var p = b.Parent; p != null; p = p.Parent) y += p.Y;
            return y;
        }
    }
}
