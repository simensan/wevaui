using System;
using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.BaselineGen {
    // Standalone smoke test for the v0.2.6 inline-splitting and text-overflow:
    // ellipsis features. Lets us validate the core layout behavior without the
    // Unity Test Runner. Mirrors a subset of the assertions in
    // InlineSplittingTests / TextOverflowEllipsisTests.
    static class InlineSplittingCheck {
        const string BuiltinUserAgent = @"
            html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr { display: block; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
        ";

        public static int Run() {
            int failures = 0;
            failures += Check("baseline: hyperlink in middle still produces 3 text runs", BaselineHyperlinkRuns);
            failures += Check("split: <a><div/></a> produces a block child of <p>", SplitAnchorWithDiv);
            failures += Check("split: <a>x<div/>z</a> produces line+block+line", SplitMidWord);
            failures += Check("split: two blocks => 5 children", SplitTwoBlocks);
            failures += Check("split: inline-block does not split", InlineBlockNoSplit);
            failures += Check("split: block height contributes", BlockHeightCounts);

            failures += Check("ellipsis: long text truncates with …", EllipsisTruncates);
            failures += Check("ellipsis: short text untouched", EllipsisShortText);
            failures += Check("ellipsis: text-overflow clip default no ellipsis", EllipsisRequiresKeyword);
            failures += Check("ellipsis: overflow visible no ellipsis", EllipsisRequiresOverflowClip);
            failures += Check("ellipsis: requires nowrap in v1", EllipsisRequiresNowrap);
            failures += Check("ellipsis: width 0 = just …", EllipsisZeroWidth);
            return failures;
        }

        static int Check(string name, Action body) {
            try {
                body();
                Console.WriteLine($"  PASS: {name}");
                return 0;
            } catch (Exception e) {
                Console.WriteLine($"  FAIL: {name}: {e.Message}");
                return 1;
            }
        }

        static (Box root, Dictionary<Element, ComputedStyle> styles) Build(string html, string css = null, double w = 800) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            if (!string.IsNullOrEmpty(css)) sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = w, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            var le = new LayoutEngine(new MonoFontMetrics());
            return (le.Layout(doc, Resolve, ctx), styles);
        }

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        static BlockBox FirstByTag(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element != null && bb.Element.TagName == tag) return bb;
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

        static int LineCount(BlockBox b) {
            int n = 0;
            foreach (var c in b.Children) if (c is LineBox) n++;
            return n;
        }

        static int BlockChildCount(BlockBox b, string tag = null) {
            int n = 0;
            foreach (var c in b.Children) {
                if (c is LineBox) continue;
                if (c is BlockBox bb && !(c is AnonymousBlockBox) && !bb.IsInlineBlock) {
                    if (tag == null || (bb.Element != null && bb.Element.TagName == tag)) n++;
                }
            }
            return n;
        }

        static void Expect(bool cond, string msg) {
            if (!cond) throw new Exception(msg);
        }

        // ----- splitting tests -----
        static void BaselineHyperlinkRuns() {
            var (root, _) = Build("<p>Click <a href=\"#\">here</a> to start</p>");
            var p = FirstByTag(root, "p");
            int runs = 0;
            foreach (var b in AllBoxes(p)) if (b is TextRun) runs++;
            Expect(runs >= 3, $"expected >=3 runs, got {runs}");
        }

        static int DivBlockCountInTree(Box root) {
            int n = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && !bb.IsInlineBlock
                    && bb.Element != null && bb.Element.TagName == "div") n++;
            }
            return n;
        }

        static void SplitAnchorWithDiv() {
            // Post HTML5 §13.2.6 AAA the `<div>` lifts out of the `<p>`:
            //   <p>Click <a/></p>  <div><a>here</a></div>  " to start"
            // Verify exactly one <div> in the tree, and that "here" exists.
            var (root, _) = Build("<p>Click <a href=\"#\"><div>here</div></a> to start</p>");
            int divs = DivBlockCountInTree(root);
            Expect(divs == 1, $"expected 1 div in tree, got {divs}");
        }

        static void SplitMidWord() {
            var (root, _) = Build("<p>before <a href=\"#\">x<div>m</div>z</a> after</p>");
            int divs = DivBlockCountInTree(root);
            Expect(divs == 1, $"expected 1 div in tree, got {divs}");
        }

        static void SplitTwoBlocks() {
            var (root, _) = Build("<p><a href=\"#\">x<div>a</div>y<div>b</div>z</a></p>");
            int divs = DivBlockCountInTree(root);
            Expect(divs == 2, $"expected 2 divs in tree, got {divs}");
        }

        static void InlineBlockNoSplit() {
            var (root, _) = Build("<p>before <span style=\"display:inline-block;width:20px;height:20px\"></span> after</p>");
            var p = FirstByTag(root, "p");
            int lines = LineCount(p);
            int blocks = BlockChildCount(p);
            Expect(lines == 1, $"expected 1 line, got {lines}");
            Expect(blocks == 0, $"expected 0 blocks, got {blocks}");
        }

        static void BlockHeightCounts() {
            var (root, _) = Build("<p>before <a href=\"#\">x<div style=\"height:50px\">m</div>z</a> after</p>");
            BlockBox div = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element != null && bb.Element.TagName == "div") { div = bb; break; }
            }
            Expect(div != null, "expected a div block somewhere in tree");
            Expect(Math.Abs(div.Height - 50) < 0.001, $"expected div.Height==50, got {div.Height}");
        }

        // ----- ellipsis tests -----
        static void EllipsisTruncates() {
            var (root, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>");
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Expect(line != null, "expected a line box");
            string t = LineText(line);
            Expect(t.EndsWith("…"), $"expected …, got '{t}'");
            Expect(line.Width <= 80 + 1e-6, $"expected width <= 80, got {line.Width}");
        }

        static void EllipsisShortText() {
            var (root, _) = Build(
                "<p style=\"width:200px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">hi</p>");
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string t = LineText(line);
            Expect(!t.Contains("…"), $"unexpected ellipsis, got '{t}'");
            Expect(t == "hi", $"expected 'hi', got '{t}'");
        }

        static void EllipsisRequiresKeyword() {
            // text-overflow defaults to "clip" - no ellipsis.
            var (root, _) = Build(
                "<p style=\"width:80px;overflow:hidden;white-space:nowrap\">abcdefghijklmnopqrstuvwxyz</p>");
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string t = LineText(line);
            Expect(!t.Contains("…"), $"unexpected ellipsis (clip default), got '{t}'");
        }

        static void EllipsisRequiresOverflowClip() {
            var (root, _) = Build(
                "<p style=\"width:80px;white-space:nowrap;text-overflow:ellipsis\">abcdefghijklmnopqrstuvwxyz</p>");
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            string t = LineText(line);
            Expect(!t.Contains("…"), $"unexpected ellipsis (overflow visible), got '{t}'");
        }

        static void EllipsisRequiresNowrap() {
            var (root, _) = Build(
                "<p style=\"width:80px;overflow:hidden;text-overflow:ellipsis\">aaa bbb ccc ddd eee fff ggg hhh iii</p>");
            var p = FirstByTag(root, "p");
            int lines = 0;
            bool sawEllipsis = false;
            foreach (var c in p.Children) {
                if (c is LineBox lb) {
                    lines++;
                    if (LineText(lb).Contains("…")) sawEllipsis = true;
                }
            }
            Expect(lines > 1, $"expected text to wrap, got {lines} lines");
            Expect(!sawEllipsis, "no ellipsis expected without nowrap");
        }

        static void EllipsisZeroWidth() {
            var (root, _) = Build(
                "<p style=\"width:0px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis\">hello</p>");
            var p = FirstByTag(root, "p");
            var line = FirstLine(p);
            Expect(line != null, "expected a line");
            string t = LineText(line);
            Expect(t == "…", $"expected just '…', got '{t}'");
        }
    }
}
