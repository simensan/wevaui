using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Positioning;
using Weva.Layout.Tables;

namespace Weva.Tests.Layout {
    // BlockFlowAdjuster is `internal static`; the test assembly sees it via
    // the InternalsVisibleTo grant in Runtime/Css/Selectors/AssemblyInfo.cs.
    // It's the post-flex / post-grid fix-up that shifts in-flow following
    // siblings by a height delta and (when the parent has auto height)
    // grows the parent and recurses upward. The walk halts at the document
    // root, at a fixed/absolutely-positioned changed box, at a flex/grid/
    // table container, or once it sees an ancestor with an explicit height.
    public class BlockFlowAdjusterTests {
        static BlockBox MakeBlock(double x, double y, double w, double h) {
            return new BlockBox { X = x, Y = y, Width = w, Height = h };
        }

        static BlockBox MakeBlockWithExplicitHeight(double x, double y, double w, double h, string heightCss) {
            var box = MakeBlock(x, y, w, h);
            box.Style = new ComputedStyle(new Element("div"));
            box.Style.Set("height", heightCss);
            return box;
        }

        [Test]
        public void Zero_delta_is_a_no_op() {
            var parent = MakeBlock(0, 0, 100, 200);
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 50, 100, 25);
            parent.AddChild(changed);
            parent.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 0);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
            Assert.That(parent.Height, Is.EqualTo(200).Within(0.0001));
        }

        [Test]
        public void Subepsilon_delta_is_a_no_op() {
            // BlockFlowAdjuster ignores |delta| < 0.01 to avoid sub-pixel
            // jitter cascades. Pin that threshold.
            var parent = MakeBlock(0, 0, 100, 200);
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 50, 100, 25);
            parent.AddChild(changed);
            parent.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 0.005);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
        }

        [Test]
        public void Null_changed_box_is_silently_ignored() {
            // Defensive entry point: BlockFlowAdjuster is called from
            // FlexLayout / GridLayout. A null guard prevents crashes when a
            // container's pre/post height comparison runs on a transiently-
            // detached tree.
            Assert.DoesNotThrow(() => BlockFlowAdjuster.PropagateHeightDelta(null, 10));
        }

        [Test]
        public void Positive_delta_shifts_following_in_flow_siblings_down() {
            var parent = MakeBlock(0, 0, 100, 200);
            var first = MakeBlock(0, 0, 100, 50);
            var second = MakeBlock(0, 50, 100, 25);
            var third = MakeBlock(0, 75, 100, 25);
            parent.AddChild(first);
            parent.AddChild(second);
            parent.AddChild(third);

            BlockFlowAdjuster.PropagateHeightDelta(first, 10);

            // first itself is NOT shifted; following siblings shift by +10.
            Assert.That(first.Y, Is.EqualTo(0).Within(0.0001));
            Assert.That(second.Y, Is.EqualTo(60).Within(0.0001));
            Assert.That(third.Y, Is.EqualTo(85).Within(0.0001));
        }

        [Test]
        public void Negative_delta_pulls_following_in_flow_siblings_up() {
            var parent = MakeBlock(0, 0, 100, 200);
            var first = MakeBlock(0, 0, 100, 50);
            var second = MakeBlock(0, 50, 100, 25);
            parent.AddChild(first);
            parent.AddChild(second);

            BlockFlowAdjuster.PropagateHeightDelta(first, -10);

            Assert.That(second.Y, Is.EqualTo(40).Within(0.0001));
        }

        [Test]
        public void Absolutely_positioned_changed_box_does_not_propagate() {
            // An absolutely-positioned box is OOF; its layout change cannot
            // shift in-flow siblings.
            var parent = MakeBlock(0, 0, 100, 200);
            var changed = MakeBlock(0, 0, 100, 50);
            changed.Position = PositionType.Absolute;
            var sibling = MakeBlock(0, 50, 100, 25);
            parent.AddChild(changed);
            parent.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 20);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
            Assert.That(parent.Height, Is.EqualTo(200).Within(0.0001));
        }

        [Test]
        public void Fixed_position_changed_box_does_not_propagate() {
            var parent = MakeBlock(0, 0, 100, 200);
            var changed = MakeBlock(0, 0, 100, 50);
            changed.Position = PositionType.Fixed;
            var sibling = MakeBlock(0, 50, 100, 25);
            parent.AddChild(changed);
            parent.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 20);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
        }

        [Test]
        public void OOF_following_siblings_are_skipped_when_shifting() {
            // Absolutely-positioned following siblings are NOT pushed down by
            // a preceding in-flow sibling's height change.
            var parent = MakeBlock(0, 0, 100, 200);
            var first = MakeBlock(0, 0, 100, 50);
            var inflowSibling = MakeBlock(0, 50, 100, 25);
            var absSibling = MakeBlock(0, 75, 100, 25);
            absSibling.Position = PositionType.Absolute;
            parent.AddChild(first);
            parent.AddChild(inflowSibling);
            parent.AddChild(absSibling);

            BlockFlowAdjuster.PropagateHeightDelta(first, 10);

            Assert.That(inflowSibling.Y, Is.EqualTo(60).Within(0.0001));
            Assert.That(absSibling.Y, Is.EqualTo(75).Within(0.0001));
        }

        [Test]
        public void Auto_height_parent_absorbs_delta_and_recurses() {
            var grand = MakeBlock(0, 0, 100, 300); // auto (no style -> treated as auto)
            var parent = MakeBlock(0, 0, 100, 200);
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 200, 100, 25);
            grand.AddChild(parent);
            grand.AddChild(sibling);
            parent.AddChild(changed);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            // Parent has auto height -> grows by 10, then recurses to the
            // grandparent, which shifts `sibling` by +10.
            Assert.That(parent.Height, Is.EqualTo(210).Within(0.0001));
            Assert.That(sibling.Y, Is.EqualTo(210).Within(0.0001));
        }

        [Test]
        public void Explicit_height_parent_stops_upward_propagation() {
            var grand = MakeBlock(0, 0, 100, 300);
            var parent = MakeBlockWithExplicitHeight(0, 0, 100, 200, "200px");
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 200, 100, 25);
            grand.AddChild(parent);
            grand.AddChild(sibling);
            parent.AddChild(changed);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            // Parent's height is pinned by CSS; it does NOT grow, and the
            // walk stops -- sibling stays put on the grandparent.
            Assert.That(parent.Height, Is.EqualTo(200).Within(0.0001));
            Assert.That(sibling.Y, Is.EqualTo(200).Within(0.0001));
        }

        [Test]
        public void Auto_keyword_height_is_treated_as_auto() {
            var grand = MakeBlock(0, 0, 100, 300);
            var parent = MakeBlockWithExplicitHeight(0, 0, 100, 200, "auto");
            var changed = MakeBlock(0, 0, 100, 50);
            grand.AddChild(parent);
            parent.AddChild(changed);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            Assert.That(parent.Height, Is.EqualTo(210).Within(0.0001));
        }

        [Test]
        public void Flex_parent_halts_propagation_immediately() {
            // The fix-up is for the BLOCK formatting context. Flex / grid /
            // table containers run their own algorithms; BlockFlowAdjuster
            // must not touch siblings inside them.
            var flex = new FlexBox { X = 0, Y = 0, Width = 100, Height = 200 };
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 50, 100, 25);
            flex.AddChild(changed);
            flex.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
            Assert.That(flex.Height, Is.EqualTo(200).Within(0.0001));
        }

        [Test]
        public void Grid_parent_halts_propagation_immediately() {
            var grid = new GridBox { X = 0, Y = 0, Width = 100, Height = 200 };
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 50, 100, 25);
            grid.AddChild(changed);
            grid.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
        }

        [Test]
        public void Table_parent_halts_propagation_immediately() {
            var table = new TableBox { X = 0, Y = 0, Width = 100, Height = 200 };
            var changed = MakeBlock(0, 0, 100, 50);
            var sibling = MakeBlock(0, 50, 100, 25);
            table.AddChild(changed);
            table.AddChild(sibling);

            BlockFlowAdjuster.PropagateHeightDelta(changed, 10);

            Assert.That(sibling.Y, Is.EqualTo(50).Within(0.0001));
        }
    }
}
