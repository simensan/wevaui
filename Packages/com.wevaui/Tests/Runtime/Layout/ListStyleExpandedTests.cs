using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Coverage for the CSS Lists 3 / Counter Styles 3 expansion of
    // list-style: additional `list-style-type` identifiers beyond
    // disc / decimal / none, the `list-style-position` longhand (inside /
    // outside), the `list-style-image` longhand (URL replaces glyph),
    // and the `list-style` shorthand parsing tokens in any order.
    //
    // The disc / decimal / none core case lives in ListStyleTests; this
    // file only pins the newly added behaviour.
    public class ListStyleExpandedTests {
        // The shared BuiltinUserAgent doesn't declare default
        // list-style-type rules — mirror ListStyleTests by providing them
        // locally so tests that depend on `<ul>` / `<ol>` defaults work.
        const string ListUA = "ul { list-style-type: disc; } ol { list-style-type: decimal; }";

        // Box-tree probe shared with ListStyleTests: returns the marker
        // (inline-block BlockBox with no Element identity) at the start of
        // the named li, or null when no marker was injected.
        static BlockBox FindMarker(Box root, int liIndex = 0) {
            int seen = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    if (seen == liIndex) {
                        if (bb.Children.Count == 0) return null;
                        var first = bb.Children[0];
                        if (first is BlockBox marker && marker.Element == null && marker.IsInlineBlock) {
                            return marker;
                        }
                        return null;
                    }
                    seen++;
                }
            }
            return null;
        }

        static string MarkerText(BlockBox marker) {
            if (marker == null || marker.Children.Count == 0) return null;
            return (marker.Children[0] as TextRun)?.Text;
        }

        static Element FirstLi(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == "li") return b.Element;
            }
            return null;
        }

        // ─── list-style-type variants (CSS Counter Styles 3 §6) ────────

        [Test]
        public void List_style_type_circle_renders_white_bullet() {
            // Counter Styles 3: `circle` → U+25E6 WHITE BULLET.
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: circle\">x</li></ul>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(MarkerText(marker), Is.EqualTo("◦"));
        }

        [Test]
        public void List_style_type_square_renders_black_small_square() {
            // Counter Styles 3: `square` → U+25AA BLACK SMALL SQUARE.
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: square\">x</li></ul>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(MarkerText(marker), Is.EqualTo("▪"));
        }

        [Test]
        public void List_style_type_decimal_leading_zero_pads_to_two_digits() {
            // Counter Styles 3 §6: decimal-leading-zero pads numbers below
            // 10 with a leading "0". The three-item list spans the boundary
            // around 9/10 indirectly — we only need 1..3 to verify padding.
            var (root, _) = BuildBoxesOnly(
                "<ol style=\"list-style-type: decimal-leading-zero\"><li>a</li><li>b</li><li>c</li></ol>", ListUA);
            string[] expected = { "01.", "02.", "03." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    Assert.That(marker, Is.Not.Null);
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run.Text, Is.EqualTo(expected[idx]));
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(3));
        }

        [Test]
        public void List_style_type_lower_roman_renders_roman_numerals() {
            // Counter Styles 3: 1..5 → i, ii, iii, iv, v.
            var (root, _) = BuildBoxesOnly(
                "<ol style=\"list-style-type: lower-roman\"><li>a</li><li>b</li><li>c</li><li>d</li><li>e</li></ol>",
                ListUA);
            string[] expected = { "i.", "ii.", "iii.", "iv.", "v." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run.Text, Is.EqualTo(expected[idx]));
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(5));
        }

        [Test]
        public void List_style_type_upper_roman_renders_uppercase() {
            var (root, _) = BuildBoxesOnly(
                "<ol style=\"list-style-type: upper-roman\"><li>a</li><li>b</li></ol>", ListUA);
            string[] expected = { "I.", "II." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run.Text, Is.EqualTo(expected[idx]));
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(2));
        }

        [Test]
        public void List_style_type_lower_alpha_renders_a_b_c() {
            var (root, _) = BuildBoxesOnly(
                "<ol style=\"list-style-type: lower-alpha\"><li>1</li><li>2</li><li>3</li></ol>",
                ListUA);
            string[] expected = { "a.", "b.", "c." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run.Text, Is.EqualTo(expected[idx]));
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(3));
        }

        [Test]
        public void List_style_type_upper_latin_is_alias_for_upper_alpha() {
            // Counter Styles 3 §6: `upper-latin` is a synonym for
            // `upper-alpha`. Render the first three to nail the alias.
            var (root, _) = BuildBoxesOnly(
                "<ol style=\"list-style-type: upper-latin\"><li>1</li><li>2</li><li>3</li></ol>",
                ListUA);
            string[] expected = { "A.", "B.", "C." };
            int idx = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    var marker = bb.Children[0] as BlockBox;
                    var run = marker.Children[0] as TextRun;
                    Assert.That(run.Text, Is.EqualTo(expected[idx]));
                    idx++;
                }
            }
            Assert.That(idx, Is.EqualTo(3));
        }

        // ─── list-style-position longhand ───────────────────────────────

        [Test]
        public void List_style_position_default_value_is_outside() {
            // Per CSS Lists 3 §3.1 the initial value is `outside`. The
            // longhand is inherited, so a bare `<li>` inside a `<ul>`
            // reads the initial through the cascade.
            var (root, styles) = BuildBoxesOnly(
                "<ul><li>x</li></ul>", ListUA);
            var li = FirstLi(root);
            Assert.That(li, Is.Not.Null);
            Assert.That(styles[li].Get("list-style-position"), Is.EqualTo("outside"));
        }

        [Test]
        public void List_style_position_inside_propagates_through_cascade() {
            // Authoring `inside` should leave the computed style at `inside`.
            // v1 has no separate inside/outside layout pass — the marker is
            // an in-flow inline-block in both cases — but the cascade value
            // must round-trip so style queries / future renderer passes see it.
            var (root, styles) = BuildBoxesOnly(
                "<ul><li style=\"list-style-position: inside\">x</li></ul>", ListUA);
            var li = FirstLi(root);
            Assert.That(styles[li].Get("list-style-position"), Is.EqualTo("inside"));
            // Marker still emitted.
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(MarkerText(marker), Is.EqualTo("•"));
        }

        // ─── list-style-image longhand ──────────────────────────────────

        [Test]
        public void List_style_image_replaces_text_glyph() {
            // Per CSS Lists 3 §3.3, a non-`none` list-style-image replaces
            // the marker glyph entirely. The marker BlockBox is still
            // emitted (so layout reserves a slot), but it has no TextRun
            // child and BlockBox.ListMarkerImage carries the resolved URL
            // for the paint pass to render.
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-image: url(bullet.png)\">x</li></ul>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null, "image marker must still emit a box");
            Assert.That(marker.ListMarkerImage, Is.EqualTo("url(bullet.png)"));
            // No TextRun glyph when the image is in effect.
            foreach (var c in marker.Children) {
                Assert.That(c, Is.Not.InstanceOf<TextRun>(),
                    "image marker should not also emit a text glyph");
            }
        }

        [Test]
        public void List_style_image_overrides_list_style_type() {
            // Even when an author writes both, image wins per §3.3.
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: square; list-style-image: url(b.png)\">x</li></ul>",
                ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.ListMarkerImage, Is.EqualTo("url(b.png)"));
            Assert.That(MarkerText(marker), Is.Null,
                "image must replace the text glyph entirely");
        }

        // ─── list-style shorthand ───────────────────────────────────────

        [Test]
        public void List_style_shorthand_expands_all_three_longhands() {
            // The shorthand should accept the canonical three-value author
            // syntax. All three longhands must be set, in any order.
            var (root, styles) = BuildBoxesOnly(
                "<ul><li style=\"list-style: square inside url(x.png)\">x</li></ul>", ListUA);
            var li = FirstLi(root);
            Assert.That(li, Is.Not.Null);
            Assert.That(styles[li].Get("list-style-type"), Is.EqualTo("square"));
            Assert.That(styles[li].Get("list-style-position"), Is.EqualTo("inside"));
            Assert.That(styles[li].Get("list-style-image"), Is.EqualTo("url(x.png)"));
        }

        [Test]
        public void List_style_shorthand_accepts_tokens_in_reversed_order() {
            // image first, then position, then type — the expander must
            // classify each token by its shape rather than its position.
            var (root, styles) = BuildBoxesOnly(
                "<ul><li style=\"list-style: url(y.png) outside upper-roman\">x</li></ul>", ListUA);
            var li = FirstLi(root);
            Assert.That(styles[li].Get("list-style-type"), Is.EqualTo("upper-roman"));
            Assert.That(styles[li].Get("list-style-position"), Is.EqualTo("outside"));
            Assert.That(styles[li].Get("list-style-image"), Is.EqualTo("url(y.png)"));
        }

        [Test]
        public void List_style_shorthand_none_suppresses_type_and_image() {
            // Per §3.4 `list-style: none` is the canonical way to suppress
            // the marker — it sets BOTH list-style-type AND list-style-image
            // to none, so no marker is rendered.
            var (root, styles) = BuildBoxesOnly(
                "<ul><li style=\"list-style: none\">x</li></ul>", ListUA);
            var li = FirstLi(root);
            Assert.That(styles[li].Get("list-style-type"), Is.EqualTo("none"));
            Assert.That(styles[li].Get("list-style-image"), Is.EqualTo("none"));
            // And no marker box is injected.
            BlockBox liBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") { liBox = bb; break; }
            }
            Assert.That(liBox, Is.Not.Null);
            foreach (var c in liBox.Children) {
                if (c is BlockBox bb && bb.Element == null && bb.IsInlineBlock) {
                    Assert.Fail("list-style: none must suppress the marker box");
                }
            }
        }
    }
}
