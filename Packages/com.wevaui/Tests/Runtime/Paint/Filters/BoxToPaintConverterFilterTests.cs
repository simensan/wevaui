using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Tests.Paint.Filters {
    public class BoxToPaintConverterFilterTests {
        static BlockBox BlockWithStyle(double x, double y, double w, double h, ComputedStyle style) {
            var bb = new BlockBox();
            bb.Style = style;
            bb.X = x; bb.Y = y; bb.Width = w; bb.Height = h;
            return bb;
        }

        static ComputedStyle MakeStyle() => new ComputedStyle(new Element("div"));

        static List<PaintCommand> Commands(Box root) {
            return new BoxToPaintConverter().Convert(root).Commands;
        }

        [Test]
        public void Box_with_no_filter_emits_no_PushFilter() {
            var s = MakeStyle();
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            foreach (var c in cmds) {
                Assert.That(c, Is.Not.InstanceOf<PushFilterCommand>());
                Assert.That(c, Is.Not.InstanceOf<PopFilterCommand>());
            }
        }

        [Test]
        public void Filter_none_emits_no_PushFilter() {
            var s = MakeStyle();
            s.Set("filter", "none");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            foreach (var c in cmds) {
                Assert.That(c, Is.Not.InstanceOf<PushFilterCommand>());
            }
        }

        [Test]
        public void Filter_present_wraps_with_PushPop() {
            var s = MakeStyle();
            s.Set("filter", "blur(5px)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            Assert.That(cmds[0], Is.InstanceOf<PushFilterCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopFilterCommand>());
            var push = (PushFilterCommand)cmds[0];
            Assert.That(push.Filters.Functions.Count, Is.EqualTo(1));
            Assert.That(push.Filters.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(push.Bounds.Width, Is.EqualTo(50));
            Assert.That(push.Bounds.Height, Is.EqualTo(50));
        }

        [Test]
        public void Filter_plus_opacity_nest_filter_outer() {
            var s = MakeStyle();
            s.Set("filter", "blur(2px)");
            s.Set("opacity", "0.5");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            // Filter is outermost: PushFilter, ..., PushOpacity, ..., PopOpacity, ..., PopFilter
            Assert.That(cmds[0], Is.InstanceOf<PushFilterCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopFilterCommand>());
            int pushOpacityIdx = -1, popOpacityIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is PushOpacityCommand) pushOpacityIdx = i;
                if (cmds[i] is PopOpacityCommand) popOpacityIdx = i;
            }
            Assert.That(pushOpacityIdx, Is.GreaterThan(0));
            Assert.That(popOpacityIdx, Is.LessThan(cmds.Count - 1));
            Assert.That(pushOpacityIdx, Is.LessThan(popOpacityIdx));
        }

        [Test]
        public void Filter_plus_transform_nest_filter_outer() {
            var s = MakeStyle();
            s.Set("filter", "blur(2px)");
            s.Set("transform", "translate(5px, 5px)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            // EmitWrappersFresh merges filter + transform into a single
            // PushFilter(scopeBoxTransform: xf) — the filter pass applies
            // the transform during its offscreen composite, avoiding a
            // separate PushTransform / PopTransform pair. See
            // BoxToPaintConverter.EmitWrappersFresh's branch around L838.
            Assert.That(cmds[0], Is.InstanceOf<PushFilterCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopFilterCommand>());
            // The fused PushFilter MUST carry the transform; otherwise the
            // box renders unrotated/untranslated inside the filter pass.
            var pf = (PushFilterCommand)cmds[0];
            Assert.That(pf.ScopeBoxTransform, Is.Not.EqualTo(Transform2D.Identity),
                "PushFilter must carry the per-box transform when filter+transform are combined.");
        }

        [Test]
        public void Filter_with_transform_and_opacity_all_wrap() {
            var s = MakeStyle();
            s.Set("filter", "blur(3px)");
            s.Set("transform", "translate(1px, 2px)");
            s.Set("opacity", "0.5");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            // Sequence with the filter+transform fusion: PushFilter(xf),
            // PushOpacity, FillRect, PopOpacity, PopFilter.
            Assert.That(cmds[0], Is.InstanceOf<PushFilterCommand>());
            Assert.That(cmds[1], Is.InstanceOf<PushOpacityCommand>());
            Assert.That(cmds[cmds.Count - 2], Is.InstanceOf<PopOpacityCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopFilterCommand>());
            var pf = (PushFilterCommand)cmds[0];
            Assert.That(pf.ScopeBoxTransform, Is.Not.EqualTo(Transform2D.Identity));
        }

        // RECALIBRATED (was a long-standing known-red pinning the pre-decision
        // contract): a LONE `filter: drop-shadow()` deliberately does NOT open
        // a filter scope — it emits a synthetic DrawShadow inline (visually
        // identical for opaque rectangles, no offscreen-RT composite, and the
        // scope's composite painted OVER later siblings: story-bubble's silver
        // `.frame` covered the dark `.inner`). See the isLoneDropShadow opt-out
        // in BoxToPaintConverter. Combined chains still record the filter —
        // pinned by DropShadow_in_combined_chain_recorded_in_push_filter below.
        [Test]
        public void DropShadow_lone_filter_emits_synthetic_shadow_not_filter_scope() {
            var s = MakeStyle();
            s.Set("filter", "drop-shadow(2px 4px 8px black)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            Assert.That(cmds.Exists(c => c is PushFilterCommand), Is.False,
                "lone drop-shadow must NOT open a filter scope");
            var shadowCmd = cmds.Find(c => c is DrawShadowCommand) as DrawShadowCommand;
            Assert.That(shadowCmd, Is.Not.Null, "synthetic DrawShadow expected");
            Assert.That(shadowCmd.Shadow.OffsetX, Is.EqualTo(2));
            Assert.That(shadowCmd.Shadow.OffsetY, Is.EqualTo(4));
            Assert.That(shadowCmd.Shadow.BlurRadius, Is.EqualTo(8));
            Assert.That(shadowCmd.Shadow.Inset, Is.False);
        }

        [Test]
        public void DropShadow_in_combined_chain_recorded_in_push_filter() {
            // With a second function in the chain the synthetic shortcut must
            // NOT fire — the real filter scope carries the DropShadowFilter.
            var s = MakeStyle();
            s.Set("filter", "drop-shadow(2px 4px 8px black) blur(3px)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            var push = cmds.Find(c => c is PushFilterCommand) as PushFilterCommand;
            Assert.That(push, Is.Not.Null, "combined chain must open a filter scope");
            Assert.That(push.Filters.Functions.Count, Is.EqualTo(2));
            var ds = push.Filters.Functions[0] as DropShadowFilter;
            Assert.That(ds, Is.Not.Null, "drop-shadow recorded first in the chain");
            Assert.That(ds.OffsetX, Is.EqualTo(2));
            Assert.That(ds.OffsetY, Is.EqualTo(4));
            Assert.That(ds.BlurRadius, Is.EqualTo(8));
            Assert.That(cmds.Exists(c => c is DrawShadowCommand), Is.False,
                "no synthetic shadow when the chain goes through the filter scope");
        }

        [Test]
        public void Multiple_filters_in_single_chain_one_PushFilter() {
            var s = MakeStyle();
            s.Set("filter", "blur(2px) brightness(1.1) contrast(1.2)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(box);
            int pushFilterCount = 0, popFilterCount = 0;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilterCount++;
                if (c is PopFilterCommand) popFilterCount++;
            }
            Assert.That(pushFilterCount, Is.EqualTo(1));
            Assert.That(popFilterCount, Is.EqualTo(1));
            var push = (PushFilterCommand)cmds[0];
            Assert.That(push.Filters.Functions.Count, Is.EqualTo(3));
        }

        // Regression: a child with `filter: blur(...)` must not erase the
        // parent's own FillRect from the paint stream. The bug surfaced when
        // match3.css's `.bg-aurora { filter: blur(40px); ... }` caused the
        // body's dark gradient background to render as solid white in the
        // URP backend — Submit(PushFilterCommand) was calling
        // Batcher.PushOpacity(1f) which forced a batch flush and pushed an
        // OpacityLayer marker, both of which interacted badly with the URP
        // pass's batch sequencing and dropped the upstream body bg quad.
        // The converter contract is "the parent's bg quad lands in the list
        // before the child's PushFilter"; this pins it so a future converter
        // optimization can't accidentally drop it.
        [Test]
        public void Parent_background_FillRect_precedes_child_PushFilter() {
            var parentStyle = MakeStyle();
            parentStyle.Set("background-color", "red");
            var parent = BlockWithStyle(0, 0, 200, 200, parentStyle);

            var childStyle = MakeStyle();
            childStyle.Set("filter", "blur(20px)");
            childStyle.Set("background-color", "transparent");
            var child = BlockWithStyle(0, 0, 200, 200, childStyle);

            parent.AddChild(child);
            var cmds = Commands(parent);

            int parentFillIdx = -1;
            int childPushFilterIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (parentFillIdx < 0 && cmds[i] is FillRectCommand fr
                    && fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor
                    && fr.Brush.Color.R > 0.5f && fr.Brush.Color.G < 0.1f) {
                    parentFillIdx = i;
                }
                if (cmds[i] is PushFilterCommand) {
                    childPushFilterIdx = i;
                    break;
                }
            }
            Assert.That(parentFillIdx, Is.GreaterThanOrEqualTo(0),
                "Parent's red background FillRect must be present in the paint stream.");
            Assert.That(childPushFilterIdx, Is.GreaterThan(parentFillIdx),
                "Parent's FillRect must precede the child's PushFilter so the child's filter " +
                "(even when unimplemented) cannot affect the parent's bg.");
        }

        [Test]
        public void Filter_on_parent_and_child_both_pushed_nested() {
            var parentStyle = MakeStyle();
            parentStyle.Set("filter", "blur(4px)");
            var childStyle = MakeStyle();
            childStyle.Set("filter", "blur(2px)");
            childStyle.Set("background-color", "red");
            var parent = BlockWithStyle(0, 0, 100, 100, parentStyle);
            var child = BlockWithStyle(0, 0, 50, 50, childStyle);
            parent.AddChild(child);
            var cmds = Commands(parent);
            int pushFilters = 0, popFilters = 0;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilters++;
                if (c is PopFilterCommand) popFilters++;
            }
            Assert.That(pushFilters, Is.EqualTo(2));
            Assert.That(popFilters, Is.EqualTo(2));
            // Outermost is parent's filter.
            Assert.That(cmds[0], Is.InstanceOf<PushFilterCommand>());
            Assert.That(((PushFilterCommand)cmds[0]).Filters.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopFilterCommand>());
        }

        [Test]
        public void Brightness_filter_on_gradient_background_folds_into_gradient_stops() {
            var s = MakeStyle();
            s.Set("filter", "brightness(1.5)");
            s.Set("background-image", "linear-gradient(90deg, red, blue)");
            var box = BlockWithStyle(0, 0, 50, 50, s);

            var cmds = Commands(box);
            int pushFilters = 0;
            FillRectCommand fill = null;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilters++;
                if (c is FillRectCommand fr) fill = fr;
            }

            Assert.That(pushFilters, Is.EqualTo(0));
            Assert.That(fill, Is.Not.Null);
            Assert.That(fill.Brush.Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(fill.Brush.GradientValue.Stops[0].Color.R, Is.EqualTo(1.5f).Within(1e-6));
            Assert.That(fill.Brush.GradientValue.Stops[1].Color.B, Is.EqualTo(1.5f).Within(1e-6));
        }

        [Test]
        public void Brightness_filter_on_url_background_keeps_filter_scope() {
            var s = MakeStyle();
            s.Set("filter", "brightness(1.5)");
            s.Set("background-image", "url(hero.png)");
            var box = BlockWithStyle(0, 0, 50, 50, s);

            var cmds = Commands(box);
            int pushFilters = 0;
            foreach (var c in cmds) if (c is PushFilterCommand) pushFilters++;

            Assert.That(pushFilters, Is.EqualTo(1));
        }
    }
}
