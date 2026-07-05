using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class TextOverflowEllipsisTests {
        const string Ellipsis = "…";

        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        static LineBox FirstLine(BlockBox box) {
            foreach (var c in box.Children) if (c is LineBox lb) return lb;
            return null;
        }

        static string LineText(LineBox line) {
            var sb = new System.Text.StringBuilder();
            foreach (var c in line.Children) {
                if (c is TextRun tr) sb.Append(tr.Text);
            }
            return sb.ToString();
        }

        [Test]
        public void Long_text_in_clipping_nowrap_truncates_with_ellipsis() {
            // 16px mono, 8px per char. Width 80 = 10 chars max minus ellipsis.
            // "abcdefghijklmnopqrstuvwxyz" is 26 chars * 8 = 208 px > 80.
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            string text = LineText(line);
            Assert.That(text.EndsWith(Ellipsis), Is.True, "Expected line text to end with ellipsis, got: " + text);
            Assert.That(line.Width, Is.LessThanOrEqualTo(80 + 1e-6));
        }

        [Test]
        public void Short_text_no_truncation() {
            var (root, _, _) = Build(
                "<p style=\"width:200px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">hi</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            string text = LineText(line);
            Assert.That(text.Contains(Ellipsis), Is.False);
            Assert.That(text, Is.EqualTo("hi"));
        }

        [Test]
        public void Multiple_runs_ellipsis_at_end_of_last_visible_run() {
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">aa<span>bbbbbbbb</span>ccccccccc</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            string text = LineText(line);
            Assert.That(text.EndsWith(Ellipsis), Is.True, "Expected ellipsis at end, got: " + text);
        }

        [Test]
        public void Text_overflow_clip_default_no_ellipsis_added() {
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string text = LineText(line);
            Assert.That(text.Contains(Ellipsis), Is.False, "text-overflow defaults to clip; got: " + text);
        }

        [Test]
        public void Overflow_visible_no_ellipsis_even_with_text_overflow() {
            var (root, _, _) = Build(
                "<p style=\"width:80px;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string text = LineText(line);
            Assert.That(text.Contains(Ellipsis), Is.False);
        }

        [Test]
        public void Vertical_only_clipping_does_not_trigger_inline_ellipsis() {
            // CSS Text Overflow L3: text-overflow is inline-axis only. Clipping
            // on the block axis (overflow-y) must NOT force horizontal truncation.
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow-x:visible;overflow-y:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string text = LineText(line);
            Assert.That(text.Contains(Ellipsis), Is.False,
                "overflow-y clipping alone must not induce inline-axis ellipsis; got: " + text);

            // Regression: inline-axis (overflow-x) clipping continues to trigger ellipsis.
            var (root2, _, _) = Build(
                "<p style=\"width:80px;overflow-x:hidden;overflow-y:visible;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p2 = FirstByTag(root2, "p");
            string text2 = LineText(FirstLine(p2));
            Assert.That(text2.EndsWith(Ellipsis), Is.True,
                "overflow-x clipping must still trigger ellipsis; got: " + text2);

            // Both axes clipping (shorthand) still triggers ellipsis.
            var (root3, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            string text3 = LineText(FirstLine(FirstByTag(root3, "p")));
            Assert.That(text3.EndsWith(Ellipsis), Is.True);
        }

        [Test]
        public void Ellipsis_requires_white_space_nowrap_in_v1() {
            // Multi-line ellipsis (line-clamp) is deferred. Without nowrap the
            // text simply wraps to multiple lines and no ellipsis is applied.
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;text-overflow:ellipsis\">aaa bbb ccc ddd eee fff ggg hhh iii</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            int lines = 0;
            bool sawEllipsis = false;
            foreach (var c in p.Children) {
                if (c is LineBox lb) {
                    lines++;
                    if (LineText(lb).Contains(Ellipsis)) sawEllipsis = true;
                }
            }
            Assert.That(lines, Is.GreaterThan(1));
            Assert.That(sawEllipsis, Is.False);
        }

        [Test]
        public void Container_resize_narrower_truncates_more_text() {
            var (root1, _, _) = Build(
                "<p style=\"width:160px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var (root2, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            string wide = LineText(FirstLine(FirstByTag(root1, "p")));
            string narrow = LineText(FirstLine(FirstByTag(root2, "p")));
            Assert.That(wide.EndsWith(Ellipsis), Is.True);
            Assert.That(narrow.EndsWith(Ellipsis), Is.True);
            // Narrower container keeps fewer characters before ellipsis.
            Assert.That(narrow.Length, Is.LessThan(wide.Length));
        }

        [Test]
        public void Container_width_zero_yields_only_ellipsis() {
            var (root, _, _) = Build(
                "<p style=\"width:0px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">hello</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Assert.That(line, Is.Not.Null);
            string text = LineText(line);
            // No characters of the original text fit before the ellipsis itself
            // can fit, so we get just an ellipsis.
            Assert.That(text, Is.EqualTo(Ellipsis));
        }

        [Test]
        public void Ellipsis_uses_donor_run_font_size() {
            // The donor's font size wins for the ellipsis glyph width.
            var (root, _, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;font-size:16px\">abcdefghijklmnopqrstuvwxyz</p>",
                null, 800);
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            // Find the ellipsis-bearing TextRun and check font size.
            TextRun ell = null;
            foreach (var c in line.Children) {
                if (c is TextRun tr && tr.Text.Contains(Ellipsis)) { ell = tr; break; }
            }
            Assert.That(ell, Is.Not.Null);
            Assert.That(ell.FontSize, Is.EqualTo(16).Within(0.001));
        }

        // Regression for the randhtml demo bug: a flex/grid item with
        // `min-width: 0` containing a `white-space: nowrap; overflow: hidden`
        // div was rendering text on TWO lines ("Aerith" / "St…") rather than
        // a single ellipsised line. The bug came from the parent flex/grid
        // axis squeezing the .unit-meta column down to its content min-width
        // and the inner block's nowrap not preventing word-wrap.
        [Test]
        public void Nowrap_inside_flex_min_width_zero_stays_on_one_line() {
            string css = @"
                .row { display: flex; }
                .col { min-width: 0; flex: 1 1 auto; }
                .name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            ";
            var (root, _, _) = Build(
                "<div class=\"row\"><div class=\"col\"><div class=\"name\">Aerith Stormborn</div></div></div>",
                css, 200);
            BlockBox nameBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null
                    && bb.Element.GetAttribute("class") == "name") { nameBox = bb; break; }
            }
            Assert.That(nameBox, Is.Not.Null);
            int lineCount = 0;
            foreach (var c in nameBox.Children) if (c is LineBox) lineCount++;
            Assert.That(lineCount, Is.EqualTo(1),
                "white-space:nowrap should keep \"Aerith Stormborn\" on a single LineBox");
            var line = FirstLine(nameBox);
            string text = LineText(line);
            Assert.That(text.StartsWith("Aerith"), Is.True,
                "First word should appear before the ellipsis; got: " + text);
            bool endsWithEllipsis = text.EndsWith(Ellipsis);
            bool containsSecondWord = text.Contains("St");
            Assert.That(endsWithEllipsis || containsSecondWord, Is.True,
                "Line must contain start of \"Stormborn\" (with or without ellipsis)");
        }

        // Mirrors the randhtml demo's actual layout: grid container holding a
        // flex column whose first child is the nowrap unit-name. The grid's
        // explicit-track sizing and the auto track for the portrait mean the
        // unit-meta cell sees a constrained width — exactly the configuration
        // where the nowrap was failing in the wild.
        [Test]
        public void Nowrap_inside_grid_with_clamp_font_stays_on_one_line() {
            string css = @"
                .unit { display: grid; grid-template-columns: auto 1fr; gap: 12px;
                        width: 240px; padding: 12px; }
                .portrait { width: 64px; height: 64px; }
                .meta { min-width: 0; display: flex; flex-direction: column; gap: 4px; }
                .name { font-weight: 600; font-size: clamp(12px, 1.5vmin, 14px);
                        white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            ";
            var (root, _, _) = Build(
                "<div class=\"unit\"><div class=\"portrait\"></div>" +
                "<div class=\"meta\"><div class=\"name\">Aerith Stormborn</div></div></div>",
                css, 800);
            BlockBox nameBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null
                    && bb.Element.GetAttribute("class") == "name") { nameBox = bb; break; }
            }
            Assert.That(nameBox, Is.Not.Null);
            int lineCount = 0;
            foreach (var c in nameBox.Children) if (c is LineBox) lineCount++;
            Assert.That(lineCount, Is.EqualTo(1),
                "white-space:nowrap inside grid+flex must produce exactly one LineBox; got " + lineCount);
        }

        // Regression: font-size: clamp(MIN, VAL, MAX) must resolve to a
        // sensible pixel value. CssCalc returned by the parser was being
        // dropped by StyleResolver.FontSizePx (which only handled CssLength /
        // CssPercentage / CssNumber), causing the value to fall through to
        // the inherited / root font size — most visibly making clamp() a
        // no-op for `font-size` and (worse) breaking subsequent vmin lookups
        // that depended on the resolved size.
        // Spec test (randhtml audit): a narrow nowrap container must produce a
        // TextRun whose final glyph is the U+2026 horizontal ellipsis, not a
        // visually clipped copy of the original text. Mirrors the exact failure
        // mode the audit guarded against — "Aerith Stormborn" overflowing into
        // the void with no marker that text was elided.
        [Test]
        public void Text_overflow_ellipsis_appends_ellipsis_glyph() {
            // Mono metrics: 8 px per char at default font-size.
            var (root, _, _) = Build(
                "<div style=\"width:50px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis\">Aerith Stormborn</div>",
                null, 800);
            var div = FirstByTag(root, "div");
            var line = FirstLine(div);
            Assert.That(line, Is.Not.Null);

            // The trailing run's last code point must be U+2026 — not just any
            // run that happens to contain the original characters.
            TextRun last = null;
            foreach (var c in line.Children) {
                if (c is TextRun tr) last = tr;
            }
            Assert.That(last, Is.Not.Null);
            Assert.That(last.Text, Is.Not.Null);
            Assert.That(last.Text.Length, Is.GreaterThan(0));
            int finalCodePoint = char.ConvertToUtf32(last.Text, last.Text.Length - 1);
            Assert.That(finalCodePoint, Is.EqualTo(0x2026),
                "Final glyph must be U+2026 HORIZONTAL ELLIPSIS, got U+" +
                finalCodePoint.ToString("X4") + " in run text: " + last.Text);
        }

        [Test]
        public void FontSize_clamp_resolves_to_clamped_value() {
            string css = ".x { font-size: clamp(12px, 1.5vmin, 14px); }";
            var (root, styles, _) = Build("<div class=\"x\">hi</div>", css, 800, 600);
            // 1.5vmin at viewport 800x600 = 0.015 * 600 = 9px → clamped to MIN 12px.
            Weva.Dom.Element x = null;
            foreach (var kv in styles) {
                if (kv.Key.GetAttribute("class") == "x") { x = kv.Key; break; }
            }
            Assert.That(x, Is.Not.Null);
            BlockBox box = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element == x) { box = bb; break; }
            }
            Assert.That(box, Is.Not.Null);
            var line = FirstLine(box);
            Assert.That(line, Is.Not.Null);
            // The line should reflect a 12-14 px font, not a 16 px (root) fallback.
            // Look for a TextRun and check its FontSize.
            TextRun txt = null;
            foreach (var c in line.Children) if (c is TextRun tr) { txt = tr; break; }
            Assert.That(txt, Is.Not.Null);
            Assert.That(txt.FontSize, Is.GreaterThanOrEqualTo(12.0 - 0.001));
            Assert.That(txt.FontSize, Is.LessThanOrEqualTo(14.0 + 0.001));
        }
    }
}
