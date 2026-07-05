using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    public class StackingContextTests {
        static StackingContext BuildTree(string html, string css = null) {
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            return new StackingContextBuilder().Build(root);
        }

        [Test]
        public void Root_creates_a_stacking_context() {
            var ctx = BuildTree("<div></div>");
            Assert.That(ctx, Is.Not.Null);
            Assert.That(ctx.Root, Is.Not.Null);
            Assert.That(ctx.ZIndex, Is.EqualTo(0));
        }

        [Test]
        public void Position_relative_with_zindex_creates_context() {
            var ctx = BuildTree(
                "<div class=\"pr\"></div>",
                ".pr { position: relative; z-index: 0; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
            Assert.That(ctx.ChildContexts[0].ZIndex, Is.EqualTo(0));
        }

        [Test]
        public void Position_relative_with_zindex_auto_does_not_create_context() {
            var ctx = BuildTree(
                "<div class=\"pr\"></div>",
                ".pr { position: relative; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(0));
            Assert.That(ctx.PositionedDescendantsZAuto.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Opacity_less_than_one_creates_context() {
            var ctx = BuildTree(
                "<div class=\"o\"></div>",
                ".o { opacity: 0.5; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        [Test]
        public void Transform_not_none_creates_context() {
            var ctx = BuildTree(
                "<div class=\"t\"></div>",
                ".t { transform: translate(10px, 0); }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        [Test]
        public void Fixed_creates_context_regardless_of_zindex() {
            var ctx = BuildTree(
                "<div class=\"f\"></div>",
                ".f { position: fixed; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        [Test]
        public void Z_index_orders_within_same_context() {
            const string html = "<div id=\"a\"></div><div id=\"b\"></div><div id=\"c\"></div>";
            const string css = @"
                #a { position: relative; z-index: -1; height: 10px; }
                #b { position: relative; z-index: 1; height: 10px; }
                #c { position: relative; z-index: 0; height: 10px; }
            ";
            var ctx = BuildTree(html, css);
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(3));
            // ChildContexts is sorted ascending by z, so [0]=-1, [1]=0, [2]=1.
            Assert.That(ctx.ChildContexts[0].ZIndex, Is.EqualTo(-1));
            Assert.That(ctx.ChildContexts[1].ZIndex, Is.EqualTo(0));
            Assert.That(ctx.ChildContexts[2].ZIndex, Is.EqualTo(1));
        }

        [Test]
        public void Document_order_is_tiebreak_for_equal_zindex() {
            const string html = "<div id=\"first\"></div><div id=\"second\"></div>";
            const string css = @"
                #first { position: relative; z-index: 5; height: 10px; }
                #second { position: relative; z-index: 5; height: 10px; }
            ";
            var ctx = BuildTree(html, css);
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(2));
            Assert.That(ctx.ChildContexts[0].Root.Element.Id, Is.EqualTo("first"));
            Assert.That(ctx.ChildContexts[1].Root.Element.Id, Is.EqualTo("second"));
        }

        [Test]
        public void Inner_zindex_does_not_affect_outer_ordering() {
            const string html = @"<div id=""outer""><div id=""inner""></div></div><div id=""sib""></div>";
            const string css = @"
                #outer { position: relative; z-index: 0; }
                #inner { position: relative; z-index: 1000; height: 10px; }
                #sib { position: relative; z-index: 1; height: 10px; }
            ";
            var ctx = BuildTree(html, css);
            // Outer (z=0) should sort BEFORE sib (z=1) — inner's z=1000 only matters
            // inside outer's own context.
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(2));
            Assert.That(ctx.ChildContexts[0].Root.Element.Id, Is.EqualTo("outer"));
            Assert.That(ctx.ChildContexts[1].Root.Element.Id, Is.EqualTo("sib"));
            // Inside outer's context the inner with z=1000 is recorded as a positive child.
            Assert.That(ctx.ChildContexts[0].PositionedDescendantsZPositive.Count, Is.EqualTo(1));
        }

        [Test]
        public void Negative_auto_and_positive_z_buckets_populated() {
            const string html = @"<div id=""n""></div><div id=""a""></div><div id=""p""></div>";
            const string css = @"
                #n { position: relative; z-index: -1; height: 10px; }
                #a { position: relative; height: 10px; }
                #p { position: relative; z-index: 1; height: 10px; }
            ";
            var ctx = BuildTree(html, css);
            Assert.That(ctx.PositionedDescendantsZNegative.Count, Is.EqualTo(1));
            Assert.That(ctx.PositionedDescendantsZAuto.Count, Is.EqualTo(1));
            Assert.That(ctx.PositionedDescendantsZPositive.Count, Is.EqualTo(1));
        }

        [Test]
        public void PaintOrderTraversal_emits_back_to_front() {
            const string html = @"<div id=""back""></div><div id=""mid""></div><div id=""front""></div>";
            const string css = @"
                #back { position: relative; z-index: -1; height: 10px; }
                #mid { position: relative; z-index: 0; height: 10px; }
                #front { position: relative; z-index: 5; height: 10px; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var ctx = new StackingContextBuilder().Build(root);
            var ordered = new List<Box>(PaintOrderTraversal.Enumerate(ctx));
            // Find the back / mid / front boxes' order in the paint list.
            int Index(string id) {
                for (int i = 0; i < ordered.Count; i++) {
                    if (ordered[i].Element != null && ordered[i].Element.Id == id) return i;
                }
                return -1;
            }
            Assert.That(Index("back"), Is.LessThan(Index("mid")));
            Assert.That(Index("mid"), Is.LessThan(Index("front")));
        }

        // `isolation: isolate` establishes a stacking context without changing
        // position. PositionedExtensions.CreatesStackingContext reads the value
        // and CssProperties registers `isolation` (initial: auto), so the
        // cascade now stores the author-supplied value and the check fires.
        [Test]
        public void Isolation_isolate_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"i\"></div>",
                ".i { isolation: isolate; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        // CSS Will Change 1 §3: a `will-change: transform` hint promotes
        // the element into its own stacking context — the engine treats it
        // as if `transform` were already non-`none` for compositing
        // purposes. Mirrors Chromium / WebKit / Firefox behaviour.
        [Test]
        public void Will_change_transform_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"w\"></div>",
                ".w { will-change: transform; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        // CSS Will Change 1 §3: the token list must mirror every property
        // this engine already promotes to SC creator. `backdrop-filter`
        // creates an SC on its own, so `will-change: backdrop-filter`
        // must too — and the multi-token form (comma-separated) must
        // promote when any single token names an SC-creating property.
        [Test]
        public void Will_change_mirrors_other_sc_creating_properties() {
            var ctxBackdrop = BuildTree(
                "<div class=\"wb\"></div>",
                ".wb { will-change: backdrop-filter; }");
            Assert.That(ctxBackdrop.ChildContexts.Count, Is.EqualTo(1));

            var ctxMulti = BuildTree(
                "<div class=\"wm\"></div>",
                ".wm { will-change: transform, isolation; }");
            Assert.That(ctxMulti.ChildContexts.Count, Is.EqualTo(1));
        }

        // `filter: none` is the initial value and must not promote the
        // element. Guards against an over-eager check that fires on any
        // declared filter regardless of value.
        [Test]
        public void Filter_none_does_not_create_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"f\"></div>",
                ".f { filter: none; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(0));
        }

        // CSS Filter Effects 1 §2.1: any filter value other than `none`
        // creates a stacking context, regardless of whether the engine
        // currently rasterises the filter.
        [Test]
        public void Filter_blur_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"f\"></div>",
                ".f { filter: blur(4px); }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        [Test]
        public void Backdrop_filter_blur_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"bf\"></div>",
                ".bf { backdrop-filter: blur(4px); }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        // CSS Compositing 1 §5.1: any `mix-blend-mode` other than `normal`
        // creates a stacking context. The engine does not yet composite the
        // blend in the paint pipeline, but the box-tree topology must already
        // match spec so descendants group correctly when painting lands.
        [Test]
        public void Mix_blend_mode_multiply_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"m\"></div>",
                ".m { mix-blend-mode: multiply; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        // Regression: the initial value `normal` must NOT promote — guards
        // against an over-eager check that fires on any declared blend mode.
        [Test]
        public void Mix_blend_mode_normal_does_not_create_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"m\"></div>",
                ".m { mix-blend-mode: normal; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(0));
        }

        // CSS Containment 2 §3: `contain: paint` clips painting to the
        // contained box and establishes a stacking context. Same is true
        // for `layout`, `strict`, and `content`.
        [Test]
        public void Contain_paint_creates_stacking_context() {
            var ctx = BuildTree(
                "<div class=\"c\"></div>",
                ".c { contain: paint; }");
            Assert.That(ctx.ChildContexts.Count, Is.EqualTo(1));
        }

        // CSS 2.1 Appendix E z-order step 3 splits into 3a (block-level
        // backgrounds+borders), 3b (floats — full painting), and 3c (in-flow
        // inline-level — full painting). For a block container that mixes
        // a left float with surrounding inline text, the paint enumeration
        // must visit block backgrounds FIRST, then the float, THEN the inline
        // content — so floats sit on top of block backgrounds but inline text
        // overlaps the float (Bug B4).
        [Test]
        public void Float_paints_between_block_background_and_inline_content() {
            // The float's blockified container sits next to inline content
            // (the surrounding text + an anonymous wrapping block). All three
            // share the same parent block-formatting context. We assert the
            // emission order across the flat paint list.
            const string html =
                "<div id=\"wrap\">" +
                "before<span id=\"flt\" style=\"float:left;width:20px;height:20px\"></span>after" +
                "</div>";
            const string css = "#wrap { width: 200px; }";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var sc = new StackingContextBuilder().Build(root);
            var ordered = new List<Box>(PaintOrderTraversal.Enumerate(sc));

            int wrapIdx = -1, floatIdx = -1, firstInlineIdx = -1;
            for (int i = 0; i < ordered.Count; i++) {
                var b = ordered[i];
                if (wrapIdx < 0 && b is BlockBox wbb && !(b is AnonymousBlockBox)
                    && b.Element != null && b.Element.Id == "wrap") {
                    wrapIdx = i;
                }
                if (floatIdx < 0 && b is BlockBox fbb && fbb.IsFloat) {
                    floatIdx = i;
                }
                if (firstInlineIdx < 0
                    && (b is TextRun || b is LineBox || b is InlineBox)) {
                    firstInlineIdx = i;
                }
            }
            Assert.That(wrapIdx, Is.GreaterThanOrEqualTo(0), "wrap block not emitted");
            Assert.That(floatIdx, Is.GreaterThanOrEqualTo(0), "float not emitted");
            Assert.That(firstInlineIdx, Is.GreaterThanOrEqualTo(0), "no inline content emitted");
            // 3a → 3b → 3c: block background BEFORE float BEFORE inline content.
            Assert.That(wrapIdx, Is.LessThan(floatIdx),
                "block background must paint before float (step 3a < 3b)");
            Assert.That(floatIdx, Is.LessThan(firstInlineIdx),
                "float must paint before inline content (step 3b < 3c)");
        }

        // Regression: when there are NO floats in the subtree, the relative
        // ordering of block-level boxes and the inline-level content they
        // contain must remain stable in document order — block parents still
        // come before their own inline children. This guards against the
        // split-walk re-ordering block backgrounds past inline content of
        // earlier-document-order block siblings.
        [Test]
        public void Pure_inline_content_without_floats_keeps_document_order() {
            const string html =
                "<div id=\"a\">alpha</div>" +
                "<div id=\"b\">bravo</div>";
            var (root, _, _) = Build(html, null, viewportWidth: 800);
            var sc = new StackingContextBuilder().Build(root);
            var ordered = new List<Box>(PaintOrderTraversal.Enumerate(sc));

            int aIdx = -1, bIdx = -1, alphaIdx = -1, bravoIdx = -1;
            for (int i = 0; i < ordered.Count; i++) {
                var box = ordered[i];
                if (box is BlockBox bb && !(box is AnonymousBlockBox) && box.Element != null) {
                    if (box.Element.Id == "a" && aIdx < 0) aIdx = i;
                    else if (box.Element.Id == "b" && bIdx < 0) bIdx = i;
                }
                if (box is TextRun tr) {
                    if (alphaIdx < 0 && tr.Text != null && tr.Text.Contains("alpha")) alphaIdx = i;
                    if (bravoIdx < 0 && tr.Text != null && tr.Text.Contains("bravo")) bravoIdx = i;
                }
            }
            Assert.That(aIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(bIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(alphaIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(bravoIdx, Is.GreaterThanOrEqualTo(0));
            // Block A background comes before block B background (3a doc order).
            Assert.That(aIdx, Is.LessThan(bIdx));
            // Within step 3c, inline content still walks in document order: alpha
            // (inside A) precedes bravo (inside B).
            Assert.That(alphaIdx, Is.LessThan(bravoIdx));
            // A's inline content paints before B's background (since A's inline
            // text and B's background interleave only when 3a-of-B follows the
            // entire 3a/3b/3c of an earlier sibling — but here both blocks
            // share the same ancestor SC, so 3a emits BOTH blocks before
            // 3c emits any inline text). The crucial doc-order invariant we
            // assert is that B's background precedes alpha's text? No: with
            // the per-pass ordering, ALL block backgrounds emit before ANY
            // inline content. So bIdx < alphaIdx.
            Assert.That(bIdx, Is.LessThan(alphaIdx),
                "all step-3a block backgrounds precede any step-3c inline content");
        }

        // K6: `CreatesStackingContext` previously took a dead `isRoot` parameter
        // that every call site hard-coded to `false`. The parameter has been
        // removed; these two tests pin the new no-arg signature so the dead
        // branch cannot silently come back.

        // A typical inflow stacking-context creator (opacity < 1) must still be
        // recognised through the parameterless overload.
        [Test]
        public void CreatesStackingContext_no_args_detects_opacity_creator() {
            var (root, _, _) = Build(
                "<div id=\"o\"></div>",
                "#o { opacity: 0.5; }",
                viewportWidth: 800);
            var target = FirstById(root, "o");
            Assert.That(target, Is.Not.Null, "test setup: #o must exist");
            Assert.That(target.CreatesStackingContext(), Is.True);
        }

        // A non-positioned, default-styled box must NOT be reported as a
        // stacking-context creator under the simplified signature — guards
        // against an over-eager replacement that returned true for everything.
        [Test]
        public void CreatesStackingContext_no_args_rejects_plain_inflow_box() {
            var (root, _, _) = Build(
                "<div id=\"plain\"></div>",
                null,
                viewportWidth: 800);
            var target = FirstById(root, "plain");
            Assert.That(target, Is.Not.Null, "test setup: #plain must exist");
            Assert.That(target.CreatesStackingContext(), Is.False);
        }

        [Test]
        public void Non_positioned_descendants_separate_from_positioned() {
            const string html = @"<div id=""a""></div><div id=""b""></div>";
            const string css = @"
                #a { height: 10px; }
                #b { position: relative; height: 10px; }
            ";
            var ctx = BuildTree(html, css);
            // a is non-positioned in-flow, b is positioned with z=auto (no own context).
            bool aInNon = false, bInZAuto = false;
            foreach (var box in ctx.NonPositionedDescendants) {
                if (box.Element != null && box.Element.Id == "a") aInNon = true;
            }
            foreach (var box in ctx.PositionedDescendantsZAuto) {
                if (box.Element != null && box.Element.Id == "b") bInZAuto = true;
            }
            Assert.That(aInNon, Is.True);
            Assert.That(bInZAuto, Is.True);
        }
    }
}
