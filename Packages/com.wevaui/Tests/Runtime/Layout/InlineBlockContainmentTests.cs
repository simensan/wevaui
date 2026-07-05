// CSS Containment L2 §3.3 + CSS Sizing L4 §5 — NUnit coverage for
// inline-block atoms (and inline-flex/inline-grid atoms) honouring
// `contain: inline-size` and `contain-intrinsic-width`.
//
// THE GAP (B15 closing note): inline-block shrink-to-fit widths are
// measured in InlineLayout.MakeAtomItem by reading laid-out LineBox
// widths directly, bypassing PositioningPass.MaxContentWidth and its
// HasInlineSize guard. This file pins the fix: the guard is now applied
// inside MakeAtomItem before the LineBox-scanning loop so auto-width
// inline-blocks collapse to frame (+ contain-intrinsic-width placeholder)
// when inline-size containment is active.
//
// Tests:
//   1. Inline-block auto-width collapses to frame under contain:inline-size
//   2. Inline-block with padding: auto-width = padding+border only
//   3. contain-intrinsic-width placeholder added to inline-block frame
//   4. Explicit width unaffected by inline-size containment
//   5. min-width floor still applies after collapse
//   6. max-width cap still applies after collapse
//   7. Un-contained inline-block NOT collapsed (regression pin)
//   8. contain:size implies inline-size — inline-block collapses
//   9. contain:strict implies inline-size — inline-block collapses
//  10. content-visibility:hidden implies inline-size — inline-block collapses
//  11. contain:content does NOT imply inline-size — inline-block NOT collapsed
//  12. inline-flex atom collapses auto-width under contain:inline-size
//  13. inline-flex with contain-intrinsic-width uses placeholder

using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class InlineBlockContainmentTests {

        // ─── Helpers ─────────────────────────────────────────────────────────

        static BlockBox FirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null &&
                    bb.Element.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        // ─── 1. Plain inline-block: auto width collapses to zero under contain:inline-size ──

        [Test]
        public void InlineBlock_contain_inline_size_auto_width_collapses_to_frame() {
            // An inline-block with auto width and contain:inline-size must
            // shrink-to-fit to zero content width (only frame = border+padding).
            // Without the fix MakeAtomItem would measure the LineBox widths and
            // produce the content's intrinsic width instead.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null,
                "inline-block atom must be found");
            // No padding/border => frame = 0; auto width must collapse to 0.
            Assert.That(atom.Width, Is.EqualTo(0).Within(0.5),
                "inline-block with contain:inline-size must collapse auto width to 0 (frame only)");
        }

        // ─── 2. Inline-block with padding: auto width = padding frame ─────────

        [Test]
        public void InlineBlock_contain_inline_size_auto_width_with_padding_equals_frame() {
            // padding contributes to the border-box width even under containment.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                    padding: 8px;
                    border: 2px solid black;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // frame = 8+8+2+2 = 20px; content intrinsic must be ignored.
            Assert.That(atom.Width, Is.EqualTo(20).Within(0.5),
                "inline-block auto width under contain:inline-size equals padding+border frame");
        }

        // ─── 3. contain-intrinsic-width placeholder added to frame ───────────

        [Test]
        public void InlineBlock_contain_intrinsic_width_adds_placeholder_to_frame() {
            // When contain-intrinsic-width is set, the placeholder is used as
            // the content width instead of 0.  CSS Sizing L4 §5.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                    contain-intrinsic-width: 50px;
                    padding: 5px;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // frame = 5+5+0+0 = 10px; intrinsic-width placeholder = 50px;
            // auto width = placeholder + frame = 60px.
            Assert.That(atom.Width, Is.EqualTo(60).Within(0.5),
                "contain-intrinsic-width placeholder should be added to frame for inline-block");
        }

        // ─── 4. Explicit width unaffected by inline-size containment ─────────

        [Test]
        public void InlineBlock_contain_inline_size_explicit_width_preserved() {
            // When width is set explicitly, containment must not override it.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                    width: 150px;
                }
                #inner { width: 300px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            Assert.That(atom.Width, Is.EqualTo(150).Within(0.5),
                "explicit width must not be overridden by contain:inline-size on inline-block");
        }

        // ─── 5. min-width floors the collapsed width ──────────────────────────

        [Test]
        public void InlineBlock_contain_inline_size_min_width_floors_collapsed() {
            // min-width applies as a floor after containment collapses intrinsic to 0.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                    min-width: 40px;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            Assert.That(atom.Width, Is.EqualTo(40).Within(0.5),
                "min-width must floor the collapsed inline-block auto width");
        }

        // ─── 6. max-width caps the explicit width ────────────────────────────

        [Test]
        public void InlineBlock_contain_inline_size_max_width_caps_explicit() {
            // max-width is applied on top of containment when explicit width is set.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: inline-size;
                    width: 200px;
                    max-width: 80px;
                }
                #inner { width: 300px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            Assert.That(atom.Width, Is.EqualTo(80).Within(0.5),
                "max-width must cap the explicit width on an inline-block with contain:inline-size");
        }

        // ─── 7. Un-contained inline-block does NOT collapse (regression pin) ──

        [Test]
        public void InlineBlock_without_containment_is_not_collapsed() {
            // Regression pin: a plain inline-block without containment must still
            // shrink to the content's intrinsic width.
            const string css = @"
                #host { width: 500px; }
                #atom { display: inline-block; }
                #inner { width: 120px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // The atom should expand to fit its 120px inline-block child.
            Assert.That(atom.Width, Is.EqualTo(120).Within(0.5),
                "inline-block without containment must NOT have its auto width collapsed");
        }

        // ─── 8. contain:size implies inline-size ─────────────────────────────

        [Test]
        public void InlineBlock_contain_size_collapses_auto_width() {
            // contain:size covers both axes, so inline-block auto width should collapse.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    contain: size;
                    padding: 3px;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // frame = 3+3+0+0 = 6px; content collapses to 0.
            Assert.That(atom.Width, Is.EqualTo(6).Within(0.5),
                "contain:size (both axes) must collapse inline-block auto width to frame");
        }

        // ─── 9. contain:strict implies inline-size ────────────────────────────

        [Test]
        public void InlineBlock_contain_strict_collapses_auto_width() {
            // contain:strict = layout+paint+size+style; size covers both axes.
            const string css = @"
                #host { width: 500px; }
                #atom { display: inline-block; contain: strict; }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            Assert.That(atom.Width, Is.EqualTo(0).Within(0.5),
                "contain:strict (includes size) must collapse inline-block auto width to 0");
        }

        // ─── 10. content-visibility:hidden implies inline-size containment ────

        [Test]
        public void InlineBlock_content_visibility_hidden_collapses_auto_width() {
            // content-visibility:hidden implies size containment per §4.2,
            // which implies inline-size containment.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-block;
                    content-visibility: hidden;
                    padding: 7px;
                }
                #inner { width: 200px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // frame = 7+7+0+0 = 14px; content collapsed to 0.
            Assert.That(atom.Width, Is.EqualTo(14).Within(0.5),
                "content-visibility:hidden must collapse inline-block auto width to frame");
        }

        // ─── 11. contain:content does NOT collapse inline-block width ──────────

        [Test]
        public void InlineBlock_contain_content_does_not_collapse_width() {
            // contain:content = layout+paint+style (no size bit), so it must NOT
            // collapse the inline-block's auto width.
            const string css = @"
                #host { width: 500px; }
                #atom { display: inline-block; contain: content; }
                #inner { width: 120px; height: 10px; display: inline-block; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div id=\"inner\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            Assert.That(atom.Width, Is.EqualTo(120).Within(0.5),
                "contain:content has no size bit — inline-block auto width must NOT collapse");
        }

        // ─── 12. inline-flex atom collapses auto-width under contain:inline-size ─

        [Test]
        public void InlineFlex_contain_inline_size_collapses_auto_width() {
            // inline-flex atoms route through PositioningPass.MaxContentWidth
            // (the B7d fix). The MaxContentWidth HasInlineSize guard must fire and
            // collapse the intrinsic to frame. Pass ctx+fontSize so the guard can
            // also resolve contain-intrinsic-width hints (CSS Sizing L4 §5).
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-flex;
                    contain: inline-size;
                    padding: 6px;
                }
                .item { width: 80px; height: 20px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div class=\"item\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null,
                "inline-flex atom must be found");
            // frame = 6+6+0+0 = 12px; content collapses to 0.
            Assert.That(atom.Width, Is.EqualTo(12).Within(1.0),
                "inline-flex with contain:inline-size must collapse auto width to frame");
        }

        // ─── 13. inline-flex with contain-intrinsic-width uses placeholder ────

        [Test]
        public void InlineFlex_contain_intrinsic_width_uses_placeholder() {
            // When contain-intrinsic-width is set on an inline-flex with
            // contain:inline-size, the placeholder replaces zero as the content
            // width contribution.
            const string css = @"
                #host { width: 500px; }
                #atom {
                    display: inline-flex;
                    contain: inline-size;
                    contain-intrinsic-width: 30px;
                    padding: 5px;
                }
                .item { width: 80px; height: 20px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"host\"><div id=\"atom\"><div class=\"item\"></div></div></div>",
                css, 800);
            var atom = FirstById(root, "atom");
            Assert.That(atom, Is.Not.Null);
            // frame = 5+5+0+0 = 10px; placeholder = 30px; total = 40px.
            // MaxContentWidth for flex returns border-box (frame included), so:
            // MaxContentWidth = placeholder + frame = 40; then MakeAtomItem
            // subtracts frame to get content=30, then adds it back: fitted = 40.
            Assert.That(atom.Width, Is.EqualTo(40).Within(1.0),
                "inline-flex with contain-intrinsic-width should use placeholder as content width");
        }
    }
}
