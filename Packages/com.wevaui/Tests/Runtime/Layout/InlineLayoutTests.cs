using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class InlineLayoutTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        [Test]
        public void Single_text_node_lays_out_one_line() {
            var (root, _, _) = Build("<p>hello</p>", null, viewportWidth: 800);
            var p = FindFirstBlock(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.EqualTo(1));
        }

        [Test]
        public void Long_text_wraps_to_multiple_lines() {
            // viewportWidth chosen so it forces wrapping with mono font (8 px / char @ 16px).
            var (root, _, _) = Build("<p>one two three four five six seven eight nine ten</p>", null, 64);
            var p = FindFirstBlock(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.GreaterThan(1));
        }

        [Test]
        public void Strong_inline_element_creates_run_with_bold_style() {
            var (root, _, _) = Build(
                "<p>hi <strong>there</strong></p>",
                "strong { font-weight: bold; }",
                800);
            var p = FindFirstBlock(root, "p");
            // Find the run whose text starts with "there" or contains "there".
            TextRun bold = null;
            foreach (var b in AllBoxes(p)) {
                if (b is TextRun tr && tr.Text.Contains("there")) { bold = tr; break; }
            }
            Assert.That(bold, Is.Not.Null);
            Assert.That(bold.Style.Get("font-weight"), Is.EqualTo("bold"));
        }

        [Test]
        public void Hyperlink_in_middle_produces_three_runs() {
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\">here</a> to start</p>",
                null, 800);
            var p = FindFirstBlock(root, "p");
            int textRuns = 0;
            bool hasClick = false, hasHere = false, hasStart = false;
            foreach (var b in AllBoxes(p)) {
                if (b is TextRun tr) {
                    textRuns++;
                    if (tr.Text.Contains("Click")) hasClick = true;
                    if (tr.Text.Contains("here")) hasHere = true;
                    if (tr.Text.Contains("start")) hasStart = true;
                }
            }
            Assert.That(textRuns, Is.GreaterThanOrEqualTo(3));
            Assert.That(hasClick && hasHere && hasStart, Is.True);
        }

        [Test]
        public void Single_inline_wrapper_fast_path_keeps_inline_fragment() {
            var (root, _, _) = Build("<p><a href=\"#\">hello</a></p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            TextRun run = null;
            InlineBox anchor = null;
            foreach (var b in AllBoxes(p)) {
                if (b is LineBox lb) line = lb;
                if (b is TextRun tr && tr.Text == "hello") run = tr;
                if (b is InlineBox ib && ib.Element?.TagName == "a") anchor = ib;
            }

            Assert.That(line, Is.Not.Null);
            Assert.That(run, Is.Not.Null);
            Assert.That(anchor, Is.Not.Null);
            Assert.That(anchor.Parent, Is.SameAs(line));
            Assert.That(anchor.Width, Is.EqualTo(run.Width).Within(0.001));
            Assert.That(anchor.Height, Is.EqualTo(run.Height).Within(0.001));
        }

        [Test]
        public void Single_run_fast_path_reuses_measurement_cache_across_layouts() {
            var metrics = new CountingMetrics();
            var doc = Html("<section><p><span>hello</span></p><p><span>hello</span></p></section>");
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent)
            };
            var cascade = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(metrics) {
                ViewportWidthPx = 800,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var engine = new LayoutEngine(metrics, true);
            ComputedStyle StyleOf(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            engine.Layout(doc, StyleOf, ctx);
            int afterFirst = metrics.MeasureCalls;
            engine.Layout(doc, StyleOf, ctx);

            Assert.That(afterFirst, Is.EqualTo(1),
                "Identical single-run text should share one measured width inside the first layout.");
            Assert.That(metrics.MeasureCalls, Is.EqualTo(afterFirst),
                "A stable second layout should reuse the inline fast-path measurement cache.");
        }

        sealed class CountingMetrics : IFontMetrics {
            public int MeasureCalls;

            public double LineHeight(double fontSize) => fontSize * 1.2;
            public double Ascent(double fontSize) => fontSize * 0.8;
            public double Descent(double fontSize) => fontSize * 0.4;
            public double Measure(string text, double fontSize) {
                MeasureCalls++;
                return string.IsNullOrEmpty(text) ? 0 : text.Length * fontSize * 0.5;
            }
            public double Measure(string text, int start, int length, double fontSize) {
                MeasureCalls++;
                if (string.IsNullOrEmpty(text) || length <= 0) return 0;
                if (start < 0) { length += start; start = 0; }
                if (start >= text.Length) return 0;
                if (start + length > text.Length) length = text.Length - start;
                return length * fontSize * 0.5;
            }
        }

        [Test]
        public void Phase1_demo_renders_inline_with_link_styles() {
            // The Phase 1 demo: ensure link element's text inherits color: blue from CSS,
            // and the strong inside it inherits font-weight: bold.
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\"><strong>here</strong></a> to start</p>",
                "a { color: blue; } strong { font-weight: bold; }",
                800);
            var p = FindFirstBlock(root, "p");
            TextRun hereRun = null;
            foreach (var b in AllBoxes(p)) {
                if (b is TextRun tr && tr.Text.Contains("here")) { hereRun = tr; break; }
            }
            Assert.That(hereRun, Is.Not.Null);
            Assert.That(hereRun.Style.Get("color"), Is.EqualTo("blue"));
            Assert.That(hereRun.Style.Get("font-weight"), Is.EqualTo("bold"));
        }

        [Test]
        public void Phase1_demo_wraps_when_viewport_narrow() {
            // 16px font, mono. "Click here to start" = 19 chars * 8 = 152px. Width 80 forces wrap.
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\"><strong>here</strong></a> to start</p>",
                null, 80);
            var p = FindFirstBlock(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            Assert.That(lines, Is.GreaterThan(1));
        }

        [Test]
        public void Text_align_left_default_no_offset() {
            var (root, _, _) = Build("<p>hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            Assert.That(first.X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Text_align_right_offsets_runs_to_end() {
            var (root, _, _) = Build(
                "<p style=\"text-align:right\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            // contentW = 800 - 0 padding/border. Text "hi" = 16 px. Offset = 800 - 16 = 784.
            Assert.That(first.X, Is.EqualTo(784).Within(0.001));
        }

        [Test]
        public void Text_align_center_centers_runs() {
            var (root, _, _) = Build(
                "<p style=\"text-align:center\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            // (800 - 16) / 2 = 392
            Assert.That(first.X, Is.EqualTo(392).Within(0.001));
        }

        [Test]
        public void Line_height_property_applies_to_line_box() {
            var (root, _, _) = Build(
                "<p style=\"line-height:40px\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line.Height, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Empty_paragraph_produces_line_box_with_default_line_height() {
            // A <p> containing only a whitespace text node still has an inline formatting
            // context; the resulting line is empty after collapse, but a line box is emitted
            // at the default line-height so the paragraph reserves vertical space.
            var (root, _, _) = Build("<p> </p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Height, Is.EqualTo(19.2).Within(0.001));
        }

        [Test]
        public void Block_with_padding_offsets_line_boxes() {
            var (root, _, _) = Build(
                "<p style=\"padding-left:20px;padding-top:10px\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line.X, Is.EqualTo(20).Within(0.001));
            Assert.That(line.Y, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Multiple_paragraphs_each_get_their_own_lines() {
            var (root, _, _) = Build(
                "<p>first</p><p>second</p>", null, 800);
            int paraLineBoxes = 0;
            foreach (var b in AllBoxes(root)) if (b is LineBox) paraLineBoxes++;
            Assert.That(paraLineBoxes, Is.EqualTo(2));
        }

        [Test]
        public void Block_height_grows_to_contain_lines() {
            var (root, _, _) = Build(
                "<p>one two three four five six seven eight nine ten</p>", null, 64);
            var p = FindFirstBlock(root, "p");
            int lines = 0;
            foreach (var c in p.Children) if (c is LineBox) lines++;
            // Each line should be 19.2 tall in our mono metrics.
            Assert.That(p.Height, Is.EqualTo(lines * 19.2).Within(0.001));
        }

        [Test]
        public void Text_runs_inside_inline_element_keep_link_color() {
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">link</a> after</p>",
                "a { color: blue; }", 800);
            TextRun linkRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text.Contains("link")) { linkRun = tr; break; }
            }
            Assert.That(linkRun, Is.Not.Null);
            Assert.That(linkRun.Style.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Text_align_last_justify_distributes_extra_to_spaces() {
            // 3 words, 2 inter-word spaces. content width 200. text "ab cd ef" = 8 chars * 8 = 64.
            // With text-align-last:justify, extra = 200 - 64 = 136 spread over 2 gaps -> +68 per space.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;width:200px\">ab cd ef</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            // After justify the line width should equal the content width.
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Text_align_last_justify_shifts_inline_block_atoms_with_spacing() {
            // Bug A16 regression: JustifyLine used to iterate only TextRun
            // fragments, leaving inline-block atoms at their pre-justify X
            // while the surrounding words spread out.
            //
            // Layout (16px mono, 8px/char):
            //   "word1" (5*8=40) + " " (8) + atom (20) + " " (8) + "word2" (5*8=40) = 116
            // container width = 200, extra = 84, 2 gaps -> +42 per space.
            // Pre-justify X positions on the line: word1@0, " "@40, atom@48,
            // " "@68, word2@76. After justify, the atom must shift by the
            // cumulative spacing inserted before it (one gap = +42) to land
            // at X=90; "word2" picks up both gaps (+84) -> X=160.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;text-align-last:justify;width:200px\">" +
                "word1 <span style=\"display:inline-block;width:20px;height:20px\"></span> word2</p>",
                null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            BlockBox atom = null;
            TextRun word2 = null;
            foreach (var c in line.ChildList) {
                if (c is BlockBox bb && bb.IsInlineBlock) atom = bb;
                else if (c is TextRun tr && tr.Text.Contains("word2")) word2 = tr;
            }
            Assert.That(atom, Is.Not.Null, "inline-block atom must be attached to the line");
            Assert.That(word2, Is.Not.Null);
            // Atom shifted by exactly one gap of distributed space (+42).
            Assert.That(atom.X, Is.EqualTo(90).Within(0.001));
            // Trailing word picks up both gaps (+84).
            Assert.That(word2.X, Is.EqualTo(160).Within(0.001));
            // Line width still matches the content width post-justify.
            Assert.That(line.Width, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void Forced_break_terminated_line_is_not_justified() {
            // Bug A17 regression: a forced line break (preserved \n in
            // pre-line / pre-wrap / pre, or a <br>) should NOT trigger
            // text-align:justify on its line. CSS Text L3 §7.2 puts
            // forced-break-terminated lines in the same bucket as the
            // structural last line of a block — they use text-align-last,
            // which here is the default "left".
            //
            // Layout (16px mono, 8 px/char), width 200:
            //   line 0: "one two"  -> natural 56, would be justified to 200
            //                         if treated as a normal wrapped line.
            //   line 1: "three"    -> natural 40, structural final.
            // With the fix, line 0 keeps its 56 px natural width because
            // it is forced-break-terminated (not "stretched-justify").
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;white-space:pre-line;width:200px\">one two\nthree</p>",
                null, 800);
            var p = FindFirstBlock(root, "p");
            var lines = new List<LineBox>();
            foreach (var c in p.Children) if (c is LineBox lb) lines.Add(lb);
            Assert.That(lines.Count, Is.EqualTo(2), "expected forced \\n to split into two lines");

            // Line 0 ended on a forced break. ApplyTextAlign must NOT
            // stretch it to the container width.
            Assert.That(lines[0].IsFinalLine, Is.True,
                "forced-break-terminated line must be flagged so justify is suppressed");
            Assert.That(lines[0].Width, Is.EqualTo(56).Within(0.001),
                "forced-break line must keep its natural width, not justify-stretch to 200");

            // Line 1 is the structural final line; also not justified.
            Assert.That(lines[1].IsFinalLine, Is.True);
            Assert.That(lines[1].Width, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Wrapped_line_without_forced_break_still_justifies() {
            // Regression guard for the A17 fix: a line that ends because
            // the next word would overflow (auto wrap) MUST still justify.
            // Only forced-break-terminated lines are exempt per §7.2.
            //
            // Width 80 (10 chars), text "ab cd ef gh": line 0 holds
            // "ab cd ef" (8 chars = 64 px natural). With justify, the two
            // inter-word gaps absorb (80 - 64) = 16 px -> +8 each. Line 0
            // must stretch to fill the full 80 px container.
            var (root, _, _) = Build(
                "<p style=\"text-align:justify;width:80px\">ab cd ef gh</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            var lines = new List<LineBox>();
            foreach (var c in p.Children) if (c is LineBox lb) lines.Add(lb);
            Assert.That(lines.Count, Is.GreaterThanOrEqualTo(2),
                "expected text to wrap into multiple lines at width 80");
            Assert.That(lines[0].IsFinalLine, Is.False,
                "auto-wrapped (non-forced) line must not be flagged as final");
            Assert.That(lines[0].Width, Is.EqualTo(80).Within(0.001),
                "auto-wrapped line must still justify-stretch to the container width");
        }

        [Test]
        public void Letter_spacing_adds_width_to_text_run() {
            // MonoFontMetrics: 8 px per char @ 16px font-size. "abc" = 24 px natural.
            // letter-spacing: 4px adds 4 * 2 = 8 px (3 chars produce 2 inter-letter
            // gaps per CSS Text 3 §10.1). Total run width = 32 px.
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:4px\">abc</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Width, Is.EqualTo(32).Within(0.001));
        }

        [Test]
        public void Letter_spacing_em_resolves_against_font_size() {
            // .25em at font-size:16 => 4 px. "ab" = 16 px natural (2 chars * 8);
            // +4*1 = 20 px (2 chars produce 1 inter-letter gap).
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:.25em;font-size:16px\">ab</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            Assert.That(first.Width, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Letter_spacing_normal_is_zero() {
            // "abc" natural width = 24 px; letter-spacing:normal is 0.
            var (root, _, _) = Build(
                "<p style=\"letter-spacing:normal\">abc</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            TextRun first = null;
            foreach (var c in line.Children) if (c is TextRun tr) { first = tr; break; }
            Assert.That(first.Width, Is.EqualTo(24).Within(0.001));
        }

        [Test]
        public void Text_transform_uppercase_rewrites_run_text() {
            var (root, _, _) = Build(
                "<p style=\"text-transform:uppercase\">abc</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var b in AllBoxes(p)) if (b is TextRun tr) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Text, Is.EqualTo("ABC"));
        }

        [Test]
        public void Text_transform_lowercase_rewrites_run_text() {
            var (root, _, _) = Build(
                "<p style=\"text-transform:lowercase\">HELLO</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var b in AllBoxes(p)) if (b is TextRun tr) { first = tr; break; }
            Assert.That(first.Text, Is.EqualTo("hello"));
        }

        [Test]
        public void Text_transform_capitalize_uppercases_first_letter_of_each_word() {
            var (root, _, _) = Build(
                "<p style=\"text-transform:capitalize\">hello world</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            // Capitalized chars across all the runs concatenated should match.
            var sb = new System.Text.StringBuilder();
            foreach (var b in AllBoxes(p)) if (b is TextRun tr) sb.Append(tr.Text);
            Assert.That(sb.ToString(), Is.EqualTo("Hello World"));
        }

        [Test]
        public void Text_transform_none_preserves_original_text() {
            var (root, _, _) = Build(
                "<p style=\"text-transform:none\">MixedCase</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            TextRun first = null;
            foreach (var b in AllBoxes(p)) if (b is TextRun tr) { first = tr; break; }
            Assert.That(first.Text, Is.EqualTo("MixedCase"));
        }

        [Test]
        public void Surrounding_text_keeps_default_color_when_link_blue() {
            var (root, _, _) = Build(
                "<p>before <a href=\"#\">link</a> after</p>",
                "a { color: blue; } p { color: red; }", 800);
            TextRun beforeRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text.Contains("before")) { beforeRun = tr; break; }
            }
            Assert.That(beforeRun, Is.Not.Null);
            Assert.That(beforeRun.Style.Get("color"), Is.EqualTo("red"));
        }

        // CSS 2.1 §10.8.1 — F5: half-leading is signed in BOTH directions.
        // A line-height tighter than the metric line-height must produce a
        // line shorter than the metric line-height (negative half-leading).
        [Test]
        public void Tight_line_height_shrinks_line_below_metric_line_height() {
            // 16px font, metric LineHeight = 16 * 1.2 = 19.2.
            // line-height: 0.9 -> 14.4; halfLeading = (14.4-19.2)/2 = -2.4.
            // Expected: line.Height = 14.4, run.Y = -2.4, baseline = 12.8-2.4 = 10.4.
            var (root, _, _) = Build("<p style=\"line-height:0.9\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Height, Is.EqualTo(14.4).Within(0.001));
            Assert.That(line.Baseline, Is.EqualTo(10.4).Within(0.001));
            TextRun run = null;
            foreach (var c in line.Children) if (c is TextRun tr) { run = tr; break; }
            Assert.That(run, Is.Not.Null);
            Assert.That(run.Y, Is.EqualTo(-2.4).Within(0.001));
        }

        // CSS 2.1 §10.8.1 — F5: a tight line-height applied to a multi-fragment
        // line still produces negative half-leading. The slow-path InlineLayout
        // branch must also shift fragments UP, not clamp at zero.
        [Test]
        public void Tight_line_height_shrinks_multi_fragment_line() {
            // <p><span>a</span><span>b</span></p> forces the slow path
            // (multiple inline children).
            var (root, _, _) = Build(
                "<p style=\"line-height:0.9\"><span>a</span><span>b</span></p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Height, Is.EqualTo(14.4).Within(0.001));
            Assert.That(line.Baseline, Is.EqualTo(10.4).Within(0.001));
            foreach (var c in line.Children) {
                if (c is TextRun tr) Assert.That(tr.Y, Is.EqualTo(-2.4).Within(0.001));
            }
        }

        // CSS 2.1 §10.8.1 — F5 regression guard: positive leading still works.
        [Test]
        public void Mixed_font_size_inline_fragments_share_one_line_baseline() {
            var (root, _, _) = Build(
                "<p><span style=\"font-size:16px\">a</span><span style=\"font-size:32px\">B</span></p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);

            TextRun small = null;
            TextRun large = null;
            foreach (var c in line.Children) {
                if (c is not TextRun run) continue;
                if (run.Text == "a") small = run;
                if (run.Text == "B") large = run;
            }

            Assert.That(small, Is.Not.Null);
            Assert.That(large, Is.Not.Null);
            Assert.That(large.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(small.Y, Is.EqualTo(12.8).Within(0.001));
            Assert.That(small.Y + 16 * 0.8, Is.EqualTo(large.Y + 32 * 0.8).Within(0.001));
        }

        [Test]
        public void Loose_line_height_centers_text_in_line() {
            // metric LH = 19.2; line-height: 1.5 at 16px -> 24; halfLeading = 2.4.
            var (root, _, _) = Build("<p style=\"line-height:1.5\">hi</p>", null, 800);
            var p = FindFirstBlock(root, "p");
            LineBox line = null;
            foreach (var c in p.Children) if (c is LineBox lb) { line = lb; break; }
            Assert.That(line, Is.Not.Null);
            Assert.That(line.Height, Is.EqualTo(24).Within(0.001));
            Assert.That(line.Baseline, Is.EqualTo(15.2).Within(0.001));
            TextRun run = null;
            foreach (var c in line.Children) if (c is TextRun tr) { run = tr; break; }
            Assert.That(run.Y, Is.EqualTo(2.4).Within(0.001));
        }

        // CSS 2.1 §10.8.1 — F6: line.Height is max-ascent + max-descent across
        // runs, NOT max of each run's own LineHeight. A tall inline-block atom
        // (Height=40, baseline at bottom -> ascent=40, descent=0) combined
        // with a text run (ascent=10, descent=5) must produce a line of
        // height 45, not 40 — otherwise the text run's descender is clipped.
        [Test]
        public void Line_height_includes_text_descent_alongside_tall_atom() {
            var atom = new BlockBox { Width = 12, Height = 40 };
            var atomItem = new LineBreaker.Item {
                AtomBox = atom,
                AtomOuterWidth = 12,
                AtomBaseline = 40,
                Metrics = new MonoFontMetrics()
            };
            var textMetrics = new MonoFontMetrics(0.5, 1.5, 0.625, 0.3125);
            // 16px * 0.625 ascent = 10; 16px * 0.3125 descent = 5; LH = 24.
            var textItem = new LineBreaker.Item {
                Text = "x",
                FontSize = 16,
                Color = "black",
                WhiteSpace = "normal",
                Metrics = textMetrics
            };

            var br = new LineBreaker();
            var r = br.Break(new List<LineBreaker.Item> { atomItem, textItem }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            var line = r.Lines[0];
            Assert.That(line.Baseline, Is.EqualTo(40).Within(0.001));
            // CSS 2.1 §10.8.1: each fragment contributes ascent + descent +
            // half-leading distributed evenly above ascent and below descent.
            //   atom:  A=40, D=0, lh=40 → halfLeading=0 → (40, 0)
            //   text:  A=10, D=5, lh=24 → halfLeading=4.5 → (14.5, 9.5)
            //   line top above baseline   = max(40, 14.5) = 40
            //   line bottom below baseline = max(0, 9.5)  = 9.5
            //   line.Height = 40 + 9.5 = 49.5
            // Pre-fix value (45 = max(40,10) + max(0,5)) dropped the text
            // fragment's half-leading-below, producing a 4.5px-short line
            // that clipped descenders when a sibling block stacked
            // immediately after — the long skill-title clipping bug.
            Assert.That(line.Height, Is.EqualTo(49.5).Within(0.001));
        }

        // F6 regression guard: a homogeneous single-text line still has the
        // same height it had pre-fix (ascent + descent equals metric line-
        // height for our default mono metrics).
        [Test]
        public void Single_text_run_line_height_unchanged_after_fix() {
            var br = new LineBreaker();
            var item = new LineBreaker.Item {
                Text = "abc",
                FontSize = 16,
                Color = "black",
                WhiteSpace = "normal",
                Metrics = new MonoFontMetrics()
            };
            var r = br.Break(new List<LineBreaker.Item> { item }, 1000);
            Assert.That(r.Lines.Count, Is.EqualTo(1));
            // Mono: ascent 0.8em + descent 0.4em = 1.2em = 19.2 = LineHeight.
            Assert.That(r.Lines[0].Height, Is.EqualTo(19.2).Within(0.001));
        }
    }
}
