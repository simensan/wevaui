using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    // CSS 2.1 §9.2.1.1 + §12 — generated-content anonymous-block generation
    // and inline-span width attribution for ::before / ::after pseudo-elements.
    //
    // Covers two tracker items:
    //   Q-BLOCK-AFTER: block <q> with a block child should produce 3 anonymous
    //   blocks (::before, block child, ::after) — Chrome h=48, 3 lines.
    //
    //   Q-INLINE-WIDTH: inline <q>'s InlineBox width must span the full
    //   generated content including ::before and ::after — Chrome w=97.09px
    //   for "[Hello world]" at 16px Inter.
    //
    // All tests use the full UA stylesheet (includes q::before/q::after rules).
    // Layout uses MonoFontMetrics (0.5em per char at 16px) for deterministic widths.
    public class GeneratedContentPseudoTests {

        // ── Helpers ──────────────────────────────────────────────────────────

        // Full UA stylesheet + author CSS, box-tree only (no layout).
        static (Box root, CascadeEngine engine, Dictionary<Element, ComputedStyle> styles) BuildBoxTree(
            string html, string css = null) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(UserAgentStylesheet.Source)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);
            return (bb.BuildDocument(doc), engine, styles);
        }

        // Full UA stylesheet + author CSS, laid out with MonoFontMetrics.
        // MonoFontMetrics: CharWidthEm = 0.5, so at 16px a char is 8px wide.
        // LineHeight = 1.0 × fontSize = 16px per line.
        static (Box root, CascadeEngine engine, Dictionary<Element, ComputedStyle> styles) BuildLayout(
            string html, string css = null,
            double viewportWidth = 800, double viewportHeight = 600) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(UserAgentStylesheet.Source)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            le.BeforeStyleOf = e => engine.ComputeBefore(e);
            le.AfterStyleOf  = e => engine.ComputeAfter(e);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);
            return (root, engine, styles);
        }

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        static Box FindByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == tag) return b;
            }
            return null;
        }

        static Box FindById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.GetAttribute("id") == id) return b;
            }
            return null;
        }

        // ── Q-BLOCK-AFTER: box-tree structure tests ───────────────────────────

        // CSS 2.1 §9.2.1.1: a block container that mixes inline and block-level
        // children must wrap each contiguous inline run in an anonymous block box.
        // A block <q> has ::before (inline) + text "Hi" (inline) + block <div>
        // + ::after (inline). After FinalizeBlockChildren the <q> should have
        // exactly 3 children: anon([::before,"Hi"]), <div>, anon([::after]).
        [Test]
        public void Block_q_with_block_child_has_three_anonymous_block_children() {
            string html = "<q style=\"display:block\">Hi<div style=\"display:block\">mid</div></q>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "#wrap { quotes: \"[\" \"]\" \"(\" \")\"; }";
            var (root, _, _) = BuildBoxTree(html, css);

            Box qBox = FindByTag(root, "q");
            Assert.That(qBox, Is.Not.Null, "Expected a <q> box in the tree");

            // Must have exactly 3 children after the anonymous-block sweep.
            Assert.That(qBox.Children.Count, Is.EqualTo(3),
                "block <q> with a block child should have 3 children: " +
                "anon(::before+text), block-child, anon(::after)");

            // First child: anonymous block (not AnonymousBlockBox but IS anonymous
            // — i.e. Element==null and ContainsInlines).
            var first = qBox.Children[0];
            Assert.That(first, Is.InstanceOf<AnonymousBlockBox>(),
                "first child should be an AnonymousBlockBox wrapping ::before + 'Hi'");

            // Second child: the real block div (has an Element).
            var second = qBox.Children[1];
            Assert.That(second.Element?.TagName, Is.EqualTo("div"),
                "second child should be the real <div> (block child)");

            // Third child: anonymous block (wrapping ::after).
            var third = qBox.Children[2];
            Assert.That(third, Is.InstanceOf<AnonymousBlockBox>(),
                "third child should be an AnonymousBlockBox wrapping ::after");
        }

        // The ::before anonymous block (anon1) must contain the open-quote TextRun
        // and the "Hi" TextRun; the ::after anonymous block (anon2) must contain
        // the close-quote TextRun.
        [Test]
        public void Block_q_anonymous_blocks_contain_correct_text_runs() {
            string html = "<q style=\"display:block\">Hi<div style=\"display:block\">mid</div></q>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "q { quotes: \"[\" \"]\" \"(\" \")\"; }";
            var (root, _, _) = BuildBoxTree(html, css);

            Box qBox = FindByTag(root, "q");
            Assert.That(qBox, Is.Not.Null);
            Assert.That(qBox.Children.Count, Is.EqualTo(3));

            // Gather all texts from the first anon block.
            var sb1 = new System.Text.StringBuilder();
            foreach (var b in AllBoxes(qBox.Children[0])) {
                if (b is TextRun tr && !string.IsNullOrWhiteSpace(tr.Text)) sb1.Append(tr.Text);
            }
            // Should contain the open-quote "[" and the literal "Hi".
            string anon1Text = sb1.ToString();
            Assert.That(anon1Text, Does.Contain("["),
                "first anonymous block should contain the ::before open-quote");
            Assert.That(anon1Text, Does.Contain("Hi"),
                "first anonymous block should contain the 'Hi' text node");

            // Gather all texts from the third anon block.
            var sb3 = new System.Text.StringBuilder();
            foreach (var b in AllBoxes(qBox.Children[2])) {
                if (b is TextRun tr && !string.IsNullOrWhiteSpace(tr.Text)) sb3.Append(tr.Text);
            }
            // Should contain the close-quote "]".
            Assert.That(sb3.ToString(), Does.Contain("]"),
                "third anonymous block should contain the ::after close-quote");
        }

        // Layout-level: block <q> with a block child should produce h = 3 lines
        // × 16px = 48px at line-height:1, font-size:16px.
        // Without the fix: h = 32 (only 2 lines — ::after anonymous block dropped).
        [Test]
        public void Block_q_with_block_child_layout_height_equals_three_lines() {
            // 200px wide container; all content fits on one line each.
            string html = "<div id=\"wrap\"><q id=\"bq\">Hi<div style=\"display:block\">mid</div></q></div>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "#wrap { width:200px; quotes:\"[\" \"]\" \"(\" \")\"; } " +
                          "#bq { display:block; }";
            var (root, _, _) = BuildLayout(html, css);

            Box bq = FindById(root, "bq");
            Assert.That(bq, Is.Not.Null, "<q id=bq> not found after layout");
            Assert.That(bq.Height, Is.EqualTo(48).Within(1.0),
                "block <q> with block child: expected height=48 (3 lines × 16px); " +
                "got " + bq.Height + ". The ::after text block must be a separate anonymous block.");
        }

        // Regression: a block <q> WITHOUT a block child should still be a single
        // anonymous inline container (h = 16px, ContainsInlines = true).
        [Test]
        public void Block_q_without_block_child_height_is_one_line() {
            string html = "<div id=\"wrap\"><q id=\"bq\">Hello</q></div>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "#wrap { width:200px; quotes:\"[\" \"]\" \"(\" \")\"; } " +
                          "#bq { display:block; }";
            var (root, _, _) = BuildLayout(html, css);

            Box bq = FindById(root, "bq");
            Assert.That(bq, Is.Not.Null, "<q id=bq> not found after layout");
            Assert.That(bq.Height, Is.EqualTo(16).Within(1.0),
                "block <q> without block child: expected height=16 (1 line); got " + bq.Height);
        }

        // ── Q-INLINE-WIDTH: InlineBox width attribution tests ─────────────────

        // An inline <q> laid out with pseudo-element resolvers should produce an
        // InlineBox fragment whose Width spans the full "[Hello world]" text, not
        // just "Hello world". MonoFontMetrics: 8px/char at 16px.
        //   "["       = 1 char × 8px =  8px
        //   "Hello world" = 11 chars × 8px = 88px (space counts as a char)
        //   "]"       = 1 char × 8px =  8px
        //   Total = 104px
        // Without the fix: Width = 88px (only "Hello world", missing brackets).
        [Test]
        public void Inline_q_InlineBox_width_includes_pseudo_before_and_after() {
            string html = "<div id=\"wrap\"><q id=\"iq\">Hello world</q></div>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "#wrap { width:400px; quotes:\"[\" \"]\" \"(\" \")\"; }";
            var (root, _, _) = BuildLayout(html, css);

            // Find the InlineBox fragment for <q id=iq>. It is a direct child of
            // a LineBox. The InlineBox is inserted BEFORE the TextRuns in the line
            // so the principal-box walker picks it up.
            Box iqBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox ib && ib.Element?.GetAttribute("id") == "iq"
                    && !ib.IsLineFragment) {
                    iqBox = ib;
                    break;
                }
            }
            Assert.That(iqBox, Is.Not.Null,
                "Expected to find InlineBox fragment for <q id=iq>");

            // MonoFontMetrics at 16px: "[Hello world]" = 13 chars × 8px = 104px.
            Assert.That(iqBox.Width, Is.EqualTo(104.0).Within(1.0),
                "inline <q> InlineBox Width should include '[' and ']' pseudo content; " +
                "got " + iqBox.Width + ". Expected 104px ([Hello world] = 13 chars × 8px).");
        }

        // Complementary: the principal-box walk (mimicking BuildUnityBoxes) should
        // attribute the InlineBox fragment to the <q> element rather than a TextRun,
        // so the reported width includes pseudo content.
        [Test]
        public void Inline_q_principal_box_is_InlineBox_not_TextRun() {
            string html = "<div id=\"wrap\"><q id=\"iq\">Hi</q></div>";
            string css  = "body { margin:0; font-size:16px; line-height:1; } " +
                          "#wrap { width:200px; quotes:\"[\" \"]\" \"(\" \")\"; }";
            var (root, _, _) = BuildLayout(html, css);

            // Simulate the seenElements walk: the FIRST box with Element=<q>
            // that is NOT a LineBox / AnonymousBlockBox / AnonymousInlineBox is
            // the principal box. After the InsertChildFirst fix, InlineBox comes
            // before any same-element TextRun in the LineBox's child list.
            Element qElement = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == "q") { qElement = b.Element; break; }
            }
            Assert.That(qElement, Is.Not.Null, "<q> element not found");

            Box firstSeen = null;
            var seen = new System.Collections.Generic.HashSet<Element>();
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null
                    && b is not LineBox
                    && b is not AnonymousBlockBox
                    && b is not AnonymousInlineBox
                    && seen.Add(b.Element)
                    && ReferenceEquals(b.Element, qElement)) {
                    firstSeen = b;
                    break;
                }
            }
            Assert.That(firstSeen, Is.Not.Null, "No principal box found for <q>");
            Assert.That(firstSeen, Is.InstanceOf<InlineBox>(),
                "Principal box for inline <q> should be InlineBox (fragment), " +
                "not TextRun. Got: " + firstSeen.GetType().Name);
        }

        // Regression: pure inline <q> with NO block children must not regress
        // existing behaviour — the element should still be found and have positive width.
        [Test]
        public void Pure_inline_q_without_pseudo_resolvers_has_positive_width() {
            // Deliberately NOT using pseudo resolvers to test the no-pseudo path.
            var doc = HtmlParser.Parse("<div><q>text</q></div>");
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(UserAgentStylesheet.Source)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 400, ViewportHeightPx = 300,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            // Intentionally NO BeforeStyleOf / AfterStyleOf.
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            Box qBox = FindByTag(root, "q");
            Assert.That(qBox, Is.Not.Null, "No <q> box found");
            Assert.That(qBox.Width, Is.GreaterThan(0),
                "inline <q> without pseudo resolvers must still have positive width");
        }

        // Regression pin: the existing 2-block <q> snippet (41-quotes) —
        // two block <q> elements with only inline content — must still produce
        // h=16 per element (one line each including the bracket pseudo content).
        [Test]
        public void Block_q_pure_inline_content_height_is_one_line() {
            string html = "<div id=\"wrap\">" +
                          "  <q id=\"q1\" class=\"bq\">Hi</q>" +
                          "  <q id=\"q2\" class=\"bq\">Ok</q>" +
                          "</div>";
            string css  = "body { margin:0; padding:0; font-size:16px; line-height:1; } " +
                          "#wrap { width:200px; quotes:\"[\" \"]\" \"(\" \")\"; } " +
                          ".bq { display:block; }";
            var (root, _, _) = BuildLayout(html, css);

            Box q1 = FindById(root, "q1");
            Box q2 = FindById(root, "q2");
            Assert.That(q1, Is.Not.Null, "<q id=q1> not found");
            Assert.That(q2, Is.Not.Null, "<q id=q2> not found");
            Assert.That(q1.Height, Is.EqualTo(16).Within(1.0),
                "block <q> with only inline content: expected h=16 (1 line); got " + q1.Height);
            Assert.That(q2.Height, Is.EqualTo(16).Within(1.0),
                "block <q> with only inline content: expected h=16 (1 line); got " + q2.Height);
        }
    }
}
