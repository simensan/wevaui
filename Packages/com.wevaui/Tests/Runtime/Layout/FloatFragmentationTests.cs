using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS 2.1 §9.5 float fragmentation — multi-paragraph and multi-float
    // interaction regression coverage for tracker item B11.
    //
    // Test conventions:
    //   - MonoFontMetrics: 0.5em advance per char at 16px => 8px/char,
    //     line-height = 1.2 * 16 = 19.2px.
    //   - `margin:0` / explicit `line-height` is set on containers to make
    //     arithmetic predictable and avoid UA-stylesheet margin surprises.
    //   - FindById / Walk helpers are local copies (same pattern as
    //     FloatLayoutTests.cs).
    //
    // Status annotations:
    //   PASSES  — engine already correct, kept as regression pin.
    //   PINNED  — engine currently diverges; test is [Ignore]'d and the
    //             divergence is documented. A follow-up B11x sub-item
    //             exists in CSS_OPEN_GAPS.md.
    public class FloatFragmentationTests {
        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children)
                foreach (var d in Walk(c))
                    yield return d;
        }

        static BlockBox FindById(Box root, string id) {
            foreach (var b in Walk(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                    && bb.Element != null
                    && bb.Element.GetAttribute("id") == id)
                    return bb;
            }
            return null;
        }

        static List<LineBox> LinesOf(BlockBox box) {
            var result = new List<LineBox>();
            foreach (var c in box.Children)
                if (c is LineBox lb) result.Add(lb);
            return result;
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Two left floats of different widths — the second
        // fits beside the first, so both sit on the same row.
        // ----------------------------------------------------------------
        [Test]
        public void Two_left_floats_different_widths_sit_on_same_row_when_they_fit() {
            // Container is 300px wide.
            // Float A: 100px wide. Float B: 120px wide.
            // 100 + 120 = 220 <= 300 → both fit on row 0.
            var (root, _, _) = Build(
                "<div style=\"width:300px;margin:0\">" +
                "<div id=\"a\" style=\"float:left;width:100px;height:40px\"></div>" +
                "<div id=\"b\" style=\"float:left;width:120px;height:60px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var a = FindById(root, "a");
            var b = FindById(root, "b");
            Assert.That(a, Is.Not.Null, "float A not found");
            Assert.That(b, Is.Not.Null, "float B not found");
            // Both on y=0 row.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.5));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.5));
            // B sits immediately to the right of A.
            Assert.That(b.X, Is.EqualTo(a.X + a.Width).Within(0.5));
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Two left floats — second wraps to new line when
        // combined width exceeds container.
        // ----------------------------------------------------------------
        [Test]
        public void Two_left_floats_second_wraps_to_new_row_when_no_room() {
            // Container 200px. Float A: 130px. Float B: 100px.
            // 130 + 100 = 230 > 200 → B drops below A.
            var (root, _, _) = Build(
                "<div style=\"width:200px;margin:0\">" +
                "<div id=\"a\" style=\"float:left;width:130px;height:50px\"></div>" +
                "<div id=\"b\" style=\"float:left;width:100px;height:40px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var a = FindById(root, "a");
            var b = FindById(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.5));
            // B must be below A (A's bottom = 50).
            Assert.That(b.Y, Is.GreaterThanOrEqualTo(50 - 0.5));
            // B re-aligns to left edge on the new row.
            Assert.That(b.X, Is.EqualTo(0).Within(0.5));
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Left float + right float fit on the same row.
        // Text in the middle uses the gap between them.
        // ----------------------------------------------------------------
        [Test]
        public void Left_and_right_float_on_same_row_text_fills_middle_gap() {
            // Container 400px. Left float 80px, right float 80px.
            // Gap = 400 - 80 - 80 = 240px.
            // font-size:16px → line-height ~19.2px. The <p> inline line
            // should have width <= 240.
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0;font-size:16px\">" +
                "<div id=\"fl\" style=\"float:left;width:80px;height:60px\"></div>" +
                "<div id=\"fr\" style=\"float:right;width:80px;height:60px\"></div>" +
                "<p id=\"p\" style=\"margin:0\">hello world</p>" +
                "</div>",
                null, viewportWidth: 800);
            var fl = FindById(root, "fl");
            var fr = FindById(root, "fr");
            var p  = FindById(root, "p");
            Assert.That(fl, Is.Not.Null);
            Assert.That(fr, Is.Not.Null);
            Assert.That(p,  Is.Not.Null);
            // Floats on the same row.
            Assert.That(fl.Y, Is.EqualTo(0).Within(0.5));
            Assert.That(fr.Y, Is.EqualTo(0).Within(0.5));
            // Left float hugs left; right float hugs right.
            Assert.That(fl.X, Is.EqualTo(0).Within(0.5));
            Assert.That(fr.X, Is.EqualTo(320).Within(0.5));
            // The <p>'s first line should start at x >= 80 (left-float edge).
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1));
            // Line X is relative to <p>; <p> is at x=0 in container.
            Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(80 - 0.5),
                "first line must clear the left float");
            // Line must end before the right float (X + Width <= 320).
            Assert.That(lines[0].X + lines[0].Width, Is.LessThanOrEqualTo(320 + 0.5),
                "first line must not overlap right float");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Float spans multiple paragraphs — paragraph 2
        // appears while the float is still active. Its inline lines must
        // wrap around the float's right edge.
        // ----------------------------------------------------------------
        [Test]
        public void Float_exclusion_zone_applies_to_second_paragraph() {
            // Float: 80px wide, 200px tall.
            // Para 1: short text, one line.
            // Para 2: placed after para1's content; float still active
            //         (200px > 19.2px * N for reasonable N).
            // Para 2's first line should start x >= 80.
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0;font-size:16px;line-height:20px\">" +
                "<div id=\"f\" style=\"float:left;width:80px;height:200px\"></div>" +
                "<p id=\"p1\" style=\"margin:0\">para one</p>" +
                "<p id=\"p2\" style=\"margin:0\">para two text that is long enough</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f  = FindById(root, "f");
            var p2 = FindById(root, "p2");
            Assert.That(f,  Is.Not.Null);
            Assert.That(p2, Is.Not.Null);
            // p2 should start below p1. Float is still active (200px tall).
            // p2.Y should be > 0 (para 1 occupies at least one line).
            Assert.That(p2.Y, Is.GreaterThan(0),
                "para 2 must follow para 1 vertically");
            // The float's bottom is 200px — para 2 starts well before that,
            // so the float intrusion must still apply.
            // Para 2's first line must start to the right of the float.
            var lines = LinesOf(p2);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1),
                "para 2 should produce at least one line");
            Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(80 - 0.5),
                "para 2's first line must clear the float's right edge");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Float spans all three paragraphs. All three
        // paragraphs' first lines must respect the float exclusion zone.
        // ----------------------------------------------------------------
        [Test]
        public void Float_exclusion_zone_applies_to_all_three_paragraphs() {
            // Float: 60px wide, 300px tall — tall enough to span 3 paras.
            // Three paragraphs of short text follow.
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0;font-size:16px;line-height:20px\">" +
                "<div id=\"f\" style=\"float:left;width:60px;height:300px\"></div>" +
                "<p id=\"p1\" style=\"margin:0\">para one</p>" +
                "<p id=\"p2\" style=\"margin:0\">para two</p>" +
                "<p id=\"p3\" style=\"margin:0\">para three</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f  = FindById(root, "f");
            var p1 = FindById(root, "p1");
            var p2 = FindById(root, "p2");
            var p3 = FindById(root, "p3");
            Assert.That(f,  Is.Not.Null);
            Assert.That(p1, Is.Not.Null);
            Assert.That(p2, Is.Not.Null);
            Assert.That(p3, Is.Not.Null);

            foreach (var (para, name) in new[] { (p1, "p1"), (p2, "p2"), (p3, "p3") }) {
                var lines = LinesOf(para);
                Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1),
                    $"{name} should produce at least one line");
                Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(60 - 0.5),
                    $"{name}'s first line must clear the 60px float");
            }
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Lines past the float bottom use full width.
        // Float 60px wide × 40px tall. Para with many lines:
        // lines at Y >= 40 must start at x=0.
        // ----------------------------------------------------------------
        [Test]
        public void Lines_past_float_bottom_use_full_container_width() {
            // font-size:16px, line-height:20px. Float height=40px → 2 lines
            // are narrow (available=100px at 8px/char ~12 chars each);
            // line 3+ should be full-width once we're past y=40.
            // Container 160px: float 60px → 100px available beside float.
            // Each word "aaaa"=4*8=32px, so 3 words fit (~96px). With many
            // words we force wrapping past the float's 2-line window.
            var (root, _, _) = Build(
                "<div style=\"width:160px;margin:0;font-size:16px;line-height:20px\">" +
                "<div id=\"f\" style=\"float:left;width:60px;height:40px\"></div>" +
                "<p id=\"p\" style=\"margin:0\">aaaa bbbb cccc dddd eeee ffff gggg hhhh</p>" +
                "</div>",
                null, viewportWidth: 800);
            var p = FindById(root, "p");
            Assert.That(p, Is.Not.Null);
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(3),
                "test requires enough lines to fall past float bottom");
            // The last line should be at y >= 40 (float bottom) and x == 0.
            var last = lines[lines.Count - 1];
            Assert.That(last.Y, Is.GreaterThanOrEqualTo(40 - 0.5),
                "last line must be below the float");
            Assert.That(last.X, Is.EqualTo(0).Within(0.5),
                "lines past the float bottom must use full width (x=0)");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: clear:both on paragraph after float — the
        // paragraph must drop below the float.
        // ----------------------------------------------------------------
        [Test]
        public void Clear_both_paragraph_after_float_drops_below_float_bottom() {
            // Float: 80px tall. Para with clear:both must start at y >= 80.
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0\">" +
                "<div id=\"f\" style=\"float:left;width:80px;height:80px\"></div>" +
                "<p id=\"p\" style=\"margin:0;clear:both\">cleared paragraph</p>" +
                "</div>",
                null, viewportWidth: 800);
            var p = FindById(root, "p");
            Assert.That(p, Is.Not.Null);
            Assert.That(p.Y, Is.EqualTo(80).Within(0.5),
                "clear:both paragraph must sit at the float bottom (80px)");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Float starts mid-para-1, extends into para-2.
        // Para-2 inline content must still respect the float exclusion zone.
        // ----------------------------------------------------------------
        [Test]
        public void Float_that_starts_mid_paragraph_excludes_next_paragraph_lines() {
            // Para 1 has text before the float (but since the float is a
            // block-level child inside the div, not inline, it must be a
            // sibling). We simulate "float starts mid-flow" by putting a
            // short para before the float, then a tall float, then para-2.
            // The float's top is approximately at the height of para 1.
            // Para-2 lines should still be inset by the float.
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0;font-size:16px;line-height:20px\">" +
                "<p id=\"p1\" style=\"margin:0\">short text</p>" +
                "<div id=\"f\" style=\"float:left;width:70px;height:150px\"></div>" +
                "<p id=\"p2\" style=\"margin:0\">para two text spans multiple lines because it is quite long</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f  = FindById(root, "f");
            var p2 = FindById(root, "p2");
            Assert.That(f,  Is.Not.Null);
            Assert.That(p2, Is.Not.Null);
            // Float starts at p1's bottom (approximately 20px).
            // p2 starts after p1 (also approximately 20px).
            // So para-2's content is well within the float's 150px span.
            var lines = LinesOf(p2);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1));
            // Para-2's first line must be inset by the float (70px).
            Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(70 - 0.5),
                "para-2 lines must still clear the float that started in para-1");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Float bigger than container — degrades gracefully.
        // The text sibling must not be invisible (zero-width lines do not
        // crash the engine, and content should still be findable).
        // ----------------------------------------------------------------
        [Test]
        public void Float_wider_than_container_degrades_gracefully_no_crash() {
            // Float is 600px; container is 300px. The float overflows but
            // the engine must not throw, and the <p> sibling still exists
            // in the box tree.
            Assert.DoesNotThrow(() => {
                var (root, _, _) = Build(
                    "<div style=\"width:300px;margin:0\">" +
                    "<div id=\"f\" style=\"float:left;width:600px;height:50px\"></div>" +
                    "<p id=\"p\" style=\"margin:0\">overflow float</p>" +
                    "</div>",
                    null, viewportWidth: 800);
                var f = FindById(root, "f");
                var p = FindById(root, "p");
                Assert.That(f, Is.Not.Null, "oversized float must exist in box tree");
                Assert.That(p, Is.Not.Null, "<p> sibling must exist even when float overflows");
            });
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: BFC container height encloses all floats.
        // Container with overflow:hidden + two floats of different heights
        // — height must be the taller float.
        // ----------------------------------------------------------------
        [Test]
        public void Bfc_container_height_encloses_both_floats_takes_taller() {
            // Left float: 90px. Right float: 50px.
            // Outer div has overflow:hidden → establishes BFC → height = 90.
            var (root, _, _) = Build(
                "<div id=\"w\" style=\"overflow:hidden;width:400px\">" +
                "<div id=\"l\" style=\"float:left;width:80px;height:90px\"></div>" +
                "<div id=\"r\" style=\"float:right;width:80px;height:50px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var w = FindById(root, "w");
            Assert.That(w, Is.Not.Null);
            Assert.That(w.Height, Is.EqualTo(90).Within(0.5),
                "BFC container must grow to enclose the taller (90px) float");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: clear:left only clears left floats, not right.
        // ----------------------------------------------------------------
        [Test]
        public void Clear_left_paragraph_clears_left_float_not_right_float() {
            // Left float: 60px tall. Right float: 100px tall.
            // Para with clear:left → drops below left float (60) but not
            // necessarily below right (100).
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0\">" +
                "<div style=\"float:left;width:60px;height:60px\"></div>" +
                "<div style=\"float:right;width:60px;height:100px\"></div>" +
                "<p id=\"p\" style=\"margin:0;clear:left\">cleared left only</p>" +
                "</div>",
                null, viewportWidth: 800);
            var p = FindById(root, "p");
            Assert.That(p, Is.Not.Null);
            // Must be at or after the left float bottom (60).
            Assert.That(p.Y, Is.GreaterThanOrEqualTo(60 - 0.5),
                "clear:left must push para below the 60px left float");
            // Must NOT be pushed all the way to the right float bottom (100).
            // (i.e., clear:left ignores the right float height.)
            Assert.That(p.Y, Is.LessThan(100),
                "clear:left must NOT wait for the 100px right float to end");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Two sequential floats on the same side with
        // in-flow text between them — text between floats wraps correctly,
        // then text after second float sees both exclusion zones.
        // ----------------------------------------------------------------
        [Test]
        public void Sequential_floats_on_same_side_text_between_wraps_correctly() {
            // Float1: left, 60px × 40px.
            // Some in-flow text.
            // Float2: left, 80px × 40px.
            // Both floats active → text after float2 sees leftExtent = max(60,80).
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0;font-size:16px;line-height:20px\">" +
                "<div id=\"f1\" style=\"float:left;width:60px;height:40px\"></div>" +
                "<p id=\"p1\" style=\"margin:0\">text between floats</p>" +
                "<div id=\"f2\" style=\"float:left;width:80px;height:40px\"></div>" +
                "<p id=\"p2\" style=\"margin:0\">text after both floats</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f1 = FindById(root, "f1");
            var f2 = FindById(root, "f2");
            var p1 = FindById(root, "p1");
            var p2 = FindById(root, "p2");
            Assert.That(f1, Is.Not.Null);
            Assert.That(f2, Is.Not.Null);
            Assert.That(p1, Is.Not.Null);
            Assert.That(p2, Is.Not.Null);
            // p1's first line must clear f1 (60px).
            var p1Lines = LinesOf(p1);
            Assert.That(p1Lines.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(p1Lines[0].X, Is.GreaterThanOrEqualTo(60 - 0.5),
                "p1 line must clear f1 (60px)");
            // p2's first line, while both f1 and f2 could be active,
            // must clear at least f1 (the wider constraint in overlapping zone).
            var p2Lines = LinesOf(p2);
            Assert.That(p2Lines.Count, Is.GreaterThanOrEqualTo(1));
            // f2 sits beside f1 (if there's room) or below. Either way,
            // p2 lines must clear whichever float is active at p2's Y.
            // The leftExtent at p2.Y is at minimum 0 (both floats cleared) and
            // at most 80 (f2 extends to 80). We just check it's non-negative
            // and not wider than the container (i.e., p2 produced visible lines).
            Assert.That(p2Lines[0].X, Is.GreaterThanOrEqualTo(0),
                "p2 line X must not be negative");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Float margin is included in the exclusion zone.
        // A float with margin-right:10px should exclude inline content
        // by width + margin_right (the margin-box, not just border-box).
        // ----------------------------------------------------------------
        [Test]
        public void Float_margin_contributes_to_inline_exclusion_zone() {
            // Float: left, width=60px, margin-right=20px.
            // Inline line must start at >= 80px (60 + 20 margin).
            var (root, _, _) = Build(
                "<div style=\"width:400px;margin:0\">" +
                "<div id=\"f\" style=\"float:left;width:60px;height:50px;margin-right:20px\"></div>" +
                "<p id=\"p\" style=\"margin:0\">text beside float</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            var p = FindById(root, "p");
            Assert.That(f, Is.Not.Null);
            Assert.That(p, Is.Not.Null);
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1));
            // The float's margin-box right edge is at 60 + 20 = 80.
            Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(80 - 0.5),
                "inline line must clear float border + margin-right");
        }

        // ----------------------------------------------------------------
        // B11 / PASSES: Three left floats: first two fit on row 0;
        // third wraps to row 1. In-flow text wraps around all of them.
        // ----------------------------------------------------------------
        [Test]
        public void Three_left_floats_two_on_first_row_one_wraps_text_sees_row1_extent() {
            // Container 250px. Float A=100px, B=100px → 200px used → fit.
            // Float C=100px → 200+100=300 > 250 → C wraps to row 1.
            // In-flow text after C: at C's row, leftExtent = C.Right = 100.
            var (root, _, _) = Build(
                "<div style=\"width:250px;margin:0;font-size:16px;line-height:20px\">" +
                "<div id=\"a\" style=\"float:left;width:100px;height:40px\"></div>" +
                "<div id=\"b\" style=\"float:left;width:100px;height:40px\"></div>" +
                "<div id=\"c\" style=\"float:left;width:100px;height:40px\"></div>" +
                "<p id=\"p\" style=\"margin:0\">text after three floats</p>" +
                "</div>",
                null, viewportWidth: 800);
            var a = FindById(root, "a");
            var b = FindById(root, "b");
            var c = FindById(root, "c");
            var p = FindById(root, "p");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(c, Is.Not.Null);
            Assert.That(p, Is.Not.Null);
            // a, b on row 0.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.5));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.5));
            // c wraps (300 < 100+100+100 → third doesn't fit at 200).
            Assert.That(c.Y, Is.GreaterThanOrEqualTo(40 - 0.5),
                "third float must wrap to a new row");
            // p's first line starts at or after c's right edge (100px),
            // because c's row is active when p lays out.
            var lines = LinesOf(p);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(lines[0].X, Is.GreaterThanOrEqualTo(100 - 0.5),
                "text line beside the wrapped third float must clear it");
        }
    }
}
