// CSS Containment L2 §3.3 / L3 inline-size — NUnit coverage for
// `contain: inline-size` (and the inline-axis half of `contain: size`).
//
// Spec behaviour under test:
//   • The element's INTRINSIC inline-size contributions (min-content / max-content
//     width) are computed as if it were empty — auto/shrink-to-fit widths collapse
//     to padding + border only.
//   • Explicit width / min-width / max-width still apply.
//   • Block axis is unaffected by `inline-size`.
//   • `contain: size` covers BOTH axes, so it also implies inline-size.
//   • `contain: strict` implies size, so it also implies inline-size.
//   • `content-visibility: hidden` implies size (§4.2), which implies inline-size.
//   • Contents still lay out inside the box (and can overflow).
//
// Regression pins:
//   • `contain: none` / `contain: content` — no width collapse.
//   • `contain: size` still collapses block-axis (height) as before.

using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class InlineSizeContainmentTests {

        // ─── Helpers ──────────────────────────────────────────────────────────

        static BlockBox FirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null &&
                    bb.Element.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        static BlockBox FirstByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    var c = bb.Element.GetAttribute("class") ?? "";
                    if (c == cls || c.StartsWith(cls + " ") || c.EndsWith(" " + cls)
                        || c.Contains(" " + cls + " ")) return bb;
                }
            }
            return null;
        }

        // ─── ContainmentResolver unit tests — inline-size implication chain ───

        [Test]
        public void ContainmentResolver_size_implies_inline_size() {
            // CSS Containment §3.3: `contain: size` covers BOTH axes.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "size");
            Assert.That(ContainmentResolver.HasInlineSize(s), Is.True,
                "contain:size must imply HasInlineSize (covers both axes)");
        }

        [Test]
        public void ContainmentResolver_strict_implies_inline_size() {
            // `strict` = layout+paint+size+style; size covers both axes.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "strict");
            Assert.That(ContainmentResolver.HasInlineSize(s), Is.True,
                "contain:strict implies size which implies inline-size");
        }

        [Test]
        public void ContainmentResolver_content_visibility_hidden_implies_inline_size() {
            // content-visibility:hidden implies size containment (§4.2),
            // which in turn implies inline-size containment.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("content-visibility", "hidden");
            Assert.That(ContainmentResolver.HasInlineSize(s), Is.True,
                "content-visibility:hidden implies size which implies inline-size");
        }

        [Test]
        public void ContainmentResolver_content_does_not_imply_inline_size() {
            // `content` = layout+paint+style — does NOT include size.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "content");
            Assert.That(ContainmentResolver.HasInlineSize(s), Is.False,
                "contain:content has no size bit, so no inline-size containment");
        }

        [Test]
        public void ContainmentResolver_none_has_no_inline_size() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "none");
            Assert.That(ContainmentResolver.HasInlineSize(s), Is.False,
                "contain:none must not imply any containment");
        }

        // ─── Abs-pos shrink-to-fit collapses to frame under inline-size ───────

        [Test]
        public void InlineSize_abs_auto_width_collapses_to_zero_content() {
            // An abs-pos box with auto width and contain:inline-size must
            // shrink-to-fit to zero content width (border+padding only).
            // Without inline-size containment it would shrink to its text content.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                    padding: 0;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            // No padding/border → shrink-to-fit width must be 0 (empty intrinsic).
            Assert.That(contained.Width, Is.EqualTo(0).Within(0.5),
                "abs-pos with contain:inline-size collapses auto width to 0 (no frame)");
        }

        [Test]
        public void InlineSize_abs_auto_width_with_padding_equals_frame() {
            // Padding+border still contribute to the border-box width.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                    padding: 8px;
                    border: 2px solid black;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            // frame = PadLeft + PadRight + BorderLeft + BorderRight = 8+8+2+2 = 20px
            Assert.That(contained.Width, Is.EqualTo(20).Within(0.5),
                "padding+border contribute to border-box width even under inline-size containment");
        }

        [Test]
        public void InlineSize_abs_explicit_width_preserved() {
            // Explicit width overrides shrink-to-fit even under inline-size containment.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                    width: 150px;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            Assert.That(contained.Width, Is.EqualTo(150).Within(0.5),
                "explicit width is not overridden by inline-size containment");
        }

        [Test]
        public void InlineSize_abs_min_width_floors_collapsed_width() {
            // min-width still applies as a floor even after intrinsic collapses to 0.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                    min-width: 60px;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            Assert.That(contained.Width, Is.EqualTo(60).Within(0.5),
                "min-width floors the collapsed shrink-to-fit under inline-size containment");
        }

        // ─── Block-axis unaffected by inline-size containment ─────────────────

        [Test]
        public void InlineSize_does_not_collapse_auto_height() {
            // `contain: inline-size` must NOT collapse the block axis.
            // Auto height should still wrap the child's height.
            const string css = @"
                #container { width: 200px; contain: inline-size; }
                #child { height: 80px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>",
                css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            Assert.That(container.Height, Is.EqualTo(80).Within(0.001),
                "contain:inline-size must NOT collapse auto height (block axis unaffected)");
        }

        [Test]
        public void Size_collapses_both_axes_but_inline_size_only_collapses_inline() {
            // `contain: size` collapses both axes.
            // `contain: inline-size` only collapses the inline axis.
            // Verify: an element with contain:inline-size has the height of its child;
            // an element with contain:size has height 0.
            const string css = @"
                #with-size { width: 200px; contain: size; }
                #with-inline-size { width: 200px; contain: inline-size; }
                .child { height: 50px; }
            ";
            var (root, _, _) = Build(@"
                <div id=""with-size""><div class=""child""></div></div>
                <div id=""with-inline-size""><div class=""child""></div></div>",
                css, 800);
            var withSize = FirstById(root, "with-size");
            var withInline = FirstById(root, "with-inline-size");
            Assert.That(withSize, Is.Not.Null);
            Assert.That(withInline, Is.Not.Null);
            Assert.That(withSize.Height, Is.EqualTo(0).Within(0.001),
                "contain:size collapses auto height to 0 (size containment covers block axis)");
            Assert.That(withInline.Height, Is.EqualTo(50).Within(0.001),
                "contain:inline-size leaves auto height at child's height (block axis free)");
        }

        // ─── Children still lay out inside ───────────────────────────────────

        [Test]
        public void InlineSize_children_still_lay_out_inside_abs() {
            // Under inline-size containment, children still participate in layout
            // inside the box — they just don't expand the box's own intrinsic width.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                }
                #child { width: 100px; height: 50px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"child\"></div></div></div>",
                css, 800);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Width, Is.EqualTo(100).Within(0.5),
                "child inside inline-size-contained abs box still lays out at its own width");
            Assert.That(child.Height, Is.EqualTo(50).Within(0.5),
                "child inside inline-size-contained abs box still lays out at its own height");
        }

        // ─── contain:size implies inline-size containment ─────────────────────

        [Test]
        public void Size_containment_also_collapses_abs_auto_width() {
            // `contain: size` covers BOTH axes, so an abs-pos box with contain:size
            // and auto width must also shrink-to-fit to frame-only (just like inline-size).
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: size;
                    padding: 5px;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            // frame = 5+5+0+0 = 10px
            Assert.That(contained.Width, Is.EqualTo(10).Within(0.5),
                "contain:size (both axes) collapses abs auto width to frame (10px padding)");
        }

        [Test]
        public void Strict_containment_also_collapses_abs_auto_width() {
            // `contain: strict` expands to layout+paint+size+style, so it also
            // implies inline-size containment.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: strict;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            Assert.That(contained.Width, Is.EqualTo(0).Within(0.5),
                "contain:strict (includes size) collapses abs auto width to frame (0 — no padding)");
        }

        // ─── content-visibility:hidden implies inline-size containment ─────────

        [Test]
        public void ContentVisibilityHidden_also_collapses_abs_auto_width() {
            // content-visibility:hidden implies size containment, which implies
            // inline-size containment.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    content-visibility: hidden;
                    padding: 6px;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            // frame = 6+6+0+0 = 12px
            Assert.That(contained.Width, Is.EqualTo(12).Within(0.5),
                "content-visibility:hidden (implies size) collapses abs auto width to frame");
        }

        // ─── Regression: contain:none / contain:content do NOT collapse width ──
        //
        // NOTE: MaxContentWidth walks LineBox/InlineBlock/Flex/Grid children —
        // a plain block child with no text produces 0 max-content. To get a
        // non-zero intrinsic we use an inline-block child (atom with its own
        // laid-out width) rather than a flow block with a width property.

        [Test]
        public void Contain_none_abs_does_not_collapse_width() {
            // Regression pin: contain:none must not cause any width collapse.
            // We use an inline-block child so MaxContentWidth sees a non-zero atom.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: none;
                }
                #inner { display: inline-block; width: 120px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            // Without inline-size containment the abs box shrinks to its max-content
            // (the 120px inline-block child).
            Assert.That(contained.Width, Is.EqualTo(120).Within(0.5),
                "contain:none must not collapse abs auto width");
        }

        [Test]
        public void Contain_content_abs_does_not_collapse_width() {
            // Regression pin: contain:content = layout+paint+style (no size).
            // Auto width should still shrink to the content's max-content width.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: content;
                }
                #inner { display: inline-block; width: 120px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            Assert.That(contained.Width, Is.EqualTo(120).Within(0.5),
                "contain:content (no size bit) must not collapse abs auto width");
        }

        // ─── Float shrink-to-fit collapses under inline-size containment ───────

        [Test]
        public void InlineSize_float_auto_width_collapses_to_frame() {
            // A float with auto width and contain:inline-size shrinks to its
            // padding+border only (zero content intrinsic).
            const string css = @"
                #container { width: 500px; }
                #flt {
                    float: left;
                    contain: inline-size;
                    padding: 4px;
                }
                #inner { width: 150px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"flt\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var flt = FirstById(root, "flt");
            Assert.That(flt, Is.Not.Null);
            // frame = 4+4+0+0 = 8px
            Assert.That(flt.Width, Is.EqualTo(8).Within(0.5),
                "float with contain:inline-size must shrink to padding+border only");
        }

        [Test]
        public void InlineSize_float_auto_width_no_frame_collapses_to_zero() {
            // Float with contain:inline-size and no padding/border → width = 0.
            const string css = @"
                #container { width: 500px; }
                #flt { float: left; contain: inline-size; }
                #inner { width: 150px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"flt\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var flt = FirstById(root, "flt");
            Assert.That(flt, Is.Not.Null);
            Assert.That(flt.Width, Is.EqualTo(0).Within(0.5),
                "float with contain:inline-size and no frame collapses auto width to 0");
        }

        // ─── Flex item base size collapses under inline-size containment ───────

        [Test]
        public void InlineSize_flex_item_shrinks_parent_intrinsic() {
            // When a block flex item has contain:inline-size, its max-content
            // contribution to the parent flex container's intrinsic sizing is
            // just its frame — not its child content.  We verify by placing the
            // flex container as an abs-pos box and checking its shrink-to-fit width.
            const string css = @"
                #ref { position: relative; width: 600px; height: 600px; }
                #flex {
                    position: absolute; top: 0; left: 0;
                    display: flex; flex-direction: row;
                }
                #item { contain: inline-size; padding: 3px; }
                #inner { width: 200px; height: 20px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"flex\"><div id=\"item\"><div id=\"inner\"></div></div></div></div>",
                css, 800);
            var flex = FirstById(root, "flex");
            Assert.That(flex, Is.Not.Null);
            // item frame = 3+3+0+0 = 6px; flex row with one item → container
            // shrink-to-fit width = item frame + flex frame (0 here) = 6.
            Assert.That(flex.Width, Is.EqualTo(6).Within(1),
                "abs-pos flex container width reflects contained item's frame only");
        }

        // ─── max-width cap still applies ──────────────────────────────────────

        [Test]
        public void InlineSize_max_width_caps_explicit_width() {
            // max-width is applied on top of inline-size containment.
            const string css = @"
                #ref { position: relative; width: 400px; height: 400px; }
                #contained {
                    position: absolute; top: 0; left: 0;
                    contain: inline-size;
                    width: 300px; max-width: 100px;
                }
                #inner { width: 200px; height: 10px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"ref\"><div id=\"contained\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var contained = FirstById(root, "contained");
            Assert.That(contained, Is.Not.Null);
            Assert.That(contained.Width, Is.EqualTo(100).Within(0.5),
                "max-width caps the explicit width even with contain:inline-size");
        }
    }
}
