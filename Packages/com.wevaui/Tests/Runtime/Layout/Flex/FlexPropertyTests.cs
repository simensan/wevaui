using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Flex;

namespace Weva.Tests.Layout.Flex {
    public class FlexPropertyTests {
        static ComputedStyle Style(params (string, string)[] entries) {
            var s = new ComputedStyle(null);
            foreach (var (k, v) in entries) s.Set(k, v);
            return s;
        }

        [Test]
        public void Container_defaults_are_row_nowrap_flex_start_stretch_stretch() {
            var s = new ComputedStyle(null);
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.Direction, Is.EqualTo(FlexDirection.Row));
            Assert.That(p.Wrap, Is.EqualTo(FlexWrap.NoWrap));
            Assert.That(p.JustifyContent, Is.EqualTo(JustifyContent.FlexStart));
            Assert.That(p.AlignItems, Is.EqualTo(AlignItems.Stretch));
            Assert.That(p.AlignContent, Is.EqualTo(AlignContent.Stretch));
        }

        [Test]
        public void Container_flex_direction_column_parsed() {
            var s = Style(("flex-direction", "column"));
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.Direction, Is.EqualTo(FlexDirection.Column));
            Assert.That(p.IsRow, Is.False);
        }

        [Test]
        public void Container_flex_flow_shorthand_sets_direction_and_wrap() {
            var s = Style(("flex-flow", "column wrap"));
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.Direction, Is.EqualTo(FlexDirection.Column));
            Assert.That(p.Wrap, Is.EqualTo(FlexWrap.Wrap));
        }

        [Test]
        public void Container_gap_resolves_to_pixels() {
            var s = Style(("gap", "16px"));
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.RowGap, Is.EqualTo(16).Within(0.001));
            Assert.That(p.ColumnGap, Is.EqualTo(16).Within(0.001));
        }

        [Test]
        public void Container_row_gap_overrides_gap() {
            var s = Style(("gap", "8px"), ("row-gap", "20px"));
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.RowGap, Is.EqualTo(20).Within(0.001));
            Assert.That(p.ColumnGap, Is.EqualTo(8).Within(0.001));
        }

        [Test]
        public void Container_justify_align_parsed() {
            var s = Style(("justify-content", "center"), ("align-items", "flex-end"), ("align-content", "space-between"));
            var p = FlexProperties.From(s, LengthContext.Default);
            Assert.That(p.JustifyContent, Is.EqualTo(JustifyContent.Center));
            Assert.That(p.AlignItems, Is.EqualTo(AlignItems.FlexEnd));
            Assert.That(p.AlignContent, Is.EqualTo(AlignContent.SpaceBetween));
        }

        [Test]
        public void Item_defaults_grow_zero_shrink_one_basis_auto() {
            var s = new ComputedStyle(null);
            var p = FlexItemProperties.From(s, LengthContext.Default);
            Assert.That(p.Grow, Is.EqualTo(0).Within(0.001));
            Assert.That(p.Shrink, Is.EqualTo(1).Within(0.001));
            Assert.That(p.Basis.Kind, Is.EqualTo(FlexBasisKind.Auto));
            Assert.That(p.AlignSelf, Is.EqualTo(AlignSelf.Auto));
            Assert.That(p.Order, Is.EqualTo(0));
        }

        [Test]
        public void Item_flex_shorthand_resolves_into_grow_shrink_basis() {
            var s = Style(("flex", "1 0 100px"));
            var p = FlexItemProperties.From(s, LengthContext.Default);
            Assert.That(p.Grow, Is.EqualTo(1).Within(0.001));
            Assert.That(p.Shrink, Is.EqualTo(0).Within(0.001));
            Assert.That(p.Basis.Kind, Is.EqualTo(FlexBasisKind.Length));
            Assert.That(p.Basis.Value, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Item_longhands_override_shorthand_when_set_after() {
            var s = Style(("flex", "1 1 0"), ("flex-grow", "3"));
            var p = FlexItemProperties.From(s, LengthContext.Default);
            Assert.That(p.Grow, Is.EqualTo(3).Within(0.001));
        }

        [Test]
        public void Item_order_parsed_as_integer() {
            var s = Style(("order", "-2"));
            var p = FlexItemProperties.From(s, LengthContext.Default);
            Assert.That(p.Order, Is.EqualTo(-2));
        }
    }
}
