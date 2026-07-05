using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Lists L3 §5 + CSS Generated Content L3 §2 — end-to-end counter scope
    // wiring: BoxBuilder builds a CounterContext from counter-reset /
    // counter-increment / counter-set declarations and passes it to
    // ResolveContentString so counter() / counters() in pseudo-element
    // content resolve to actual values.
    public class CounterScopeTests {
        // Builds a box tree with ::before and ::after pseudo-element resolvers
        // wired to the CascadeEngine. This mirrors the production path in
        // UIDocumentBuilder without requiring a full document lifecycle.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithPseudos(
            string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));

            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            // Use the convenience constructor that allocates a fresh pool and
            // calls BeginPass so Allocate* methods are ready to use.
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);

            return (bb.BuildDocument(doc), styles);
        }

        // Find the first TextRun whose text equals `expected` anywhere in the tree.
        static TextRun FindTextRun(Box root, string expected) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text == expected) return tr;
            }
            return null;
        }

        // Collect all TextRun.Text values from boxes that have no DOM element
        // (i.e. from pseudo-element generated content). Excludes whitespace-only
        // runs and the disc/decimal marker runs from list-style injection.
        static List<string> CollectPseudoTexts(Box root) {
            var result = new List<string>();
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Element == null && tr.Text != null
                    && tr.Text.Trim().Length > 0) {
                    result.Add(tr.Text);
                }
            }
            return result;
        }

        // ── Test 1: single counter-reset at default (0) ──────────────────────

        [Test]
        public void Counter_reset_at_default_produces_zero() {
            // The parent resets counter "c"; the child's ::before reads counter(c).
            // No increment → value is 0 (default reset value).
            const string css = @"
                .parent { counter-reset: c; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "0");
            Assert.That(run, Is.Not.Null,
                "counter(c) with counter-reset:c and no increment should resolve to '0'");
        }

        // ── Test 2: counter-increment on parent ───────────────────────────────

        [Test]
        public void Counter_reset_then_increment_produces_one() {
            // parent: counter-reset: c, then counter-increment: c (adds 1).
            // child::before → counter(c) = 1.
            const string css = @"
                .parent { counter-reset: c; counter-increment: c; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "1");
            Assert.That(run, Is.Not.Null,
                "counter-reset:c then counter-increment:c should produce counter(c)='1'");
        }

        // ── Test 3: explicit reset value ──────────────────────────────────────

        [Test]
        public void Counter_reset_with_explicit_value() {
            // counter-reset: c 5 → counter(c) = 5.
            const string css = @"
                .parent { counter-reset: c 5; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "5");
            Assert.That(run, Is.Not.Null,
                "counter-reset:c 5 should produce counter(c)='5'");
        }

        // ── Test 4: multi-element increment — three siblings ──────────────────

        [Test]
        public void Three_siblings_each_increment_produce_sequential_values() {
            // The list wrapper resets counter "c". Each li increments it; the
            // li's ::before reads counter(c). The three items should show 1, 2, 3.
            const string css = @"
                .list { counter-reset: c; }
                .item { counter-increment: c; }
                .item::before { content: counter(c); }
            ";
            const string html = @"
                <div class=""list"">
                    <div class=""item"">a</div>
                    <div class=""item"">b</div>
                    <div class=""item"">c</div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);
            var texts = CollectPseudoTexts(root);

            // The three items' ::before boxes should contain "1", "2", "3".
            Assert.That(texts, Has.Member("1"), "first item should show counter value 1");
            Assert.That(texts, Has.Member("2"), "second item should show counter value 2");
            Assert.That(texts, Has.Member("3"), "third item should show counter value 3");
        }

        // ── Test 5: counter-set overrides value ───────────────────────────────

        [Test]
        public void Counter_set_overrides_value_without_creating_scope() {
            // parent resets c to 0, then sets it to 42. child sees 42.
            const string css = @"
                .parent { counter-reset: c; counter-set: c 42; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "42");
            Assert.That(run, Is.Not.Null,
                "counter-set:c 42 should produce counter(c)='42'");
        }

        // ── Test 6: counters() with 2-level nesting ───────────────────────────

        [Test]
        public void Counters_two_level_nesting_produces_dotted_chain() {
            // Outer list resets c. Inner list also resets c (creates new scope).
            // The deepest child uses counters(c, ".") → "1.1".
            const string css = @"
                .outer { counter-reset: c; counter-increment: c; }
                .inner { counter-reset: c; counter-increment: c; }
                .leaf::before { content: counters(c, "".""); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""inner"">
                        <span class=""leaf"">x</span>
                    </div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);

            // Both outer and inner reset c (pushing two scopes) and increment.
            // outer: scope chain = [1]; inner: scope chain = [1, 1].
            // counters(c, ".") → "1.1"
            var run = FindTextRun(root, "1.1");
            Assert.That(run, Is.Not.Null,
                "counters(c, \".\") with 2-level nesting should produce '1.1'");
        }

        // ── Test 7: counters() with separator and upper-roman style ───────────

        [Test]
        public void Counters_with_separator_and_upper_roman_style() {
            // Two-level nesting, counter value 1 at each level.
            // counters(c, ".", upper-roman) → "I.I"
            const string css = @"
                .outer { counter-reset: c; counter-increment: c; }
                .inner { counter-reset: c; counter-increment: c; }
                .leaf::before { content: counters(c, ""."", upper-roman); }
            ";
            const string html = @"
                <div class=""outer"">
                    <div class=""inner"">
                        <span class=""leaf"">x</span>
                    </div>
                </div>
            ";
            var (root, _) = BuildWithPseudos(html, css);

            // Each scope has value 1; upper-roman(1) = "I".
            // counters → "I.I"
            var run = FindTextRun(root, "I.I");
            Assert.That(run, Is.Not.Null,
                "counters(c, \".\", upper-roman) with 2-level nesting should produce 'I.I'");
        }

        // ── Test 8: counter via ol/li chain through CSS counters ──────────────

        [Test]
        public void Ol_li_counter_via_css_counter_reset_and_increment() {
            // The CSS counters model for ol/li: ol resets "item", each li increments.
            // This validates the unified counter system for the standard list model.
            const string css = @"
                ol { counter-reset: item; }
                li { counter-increment: item; }
                li::before { content: counter(item); }
                ol, li { list-style: none; }
            ";
            const string html = "<ol><li>a</li><li>b</li></ol>";
            var (root, _) = BuildWithPseudos(html, css);

            var texts = CollectPseudoTexts(root);
            // The two li::before pseudo boxes should show "1" and "2".
            Assert.That(texts, Has.Member("1"), "first li should show counter(item)='1'");
            Assert.That(texts, Has.Member("2"), "second li should show counter(item)='2'");
        }

        // ── Test 9: undefined counter returns empty string, box still exists ──

        [Test]
        public void Undefined_counter_produces_empty_string_box_still_exists() {
            // content: counter(undefined_name) — no counter-reset for this name.
            // Per spec, an unresolvable counter() produces empty string (not
            // suppress-box). The pseudo box IS generated with empty text.
            const string css = @"
                .parent::before { content: counter(undefined_name); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\">x</div>", css);

            // The pseudo box should exist. Because the text is "", it won't
            // generate a TextRun child (BuildPseudoBox only adds a run when
            // text.Length > 0). So we verify the anonymous InlineBox or
            // BlockBox pseudo exists with no TextRun child of a visible text.
            bool foundPseudoBox = false;
            foreach (var b in AllBoxes(root)) {
                if (b.Element == null && b.Style != null) {
                    // Look for the pseudo box that has content="counter(undefined_name)"
                    string content = b.Style.Get("content");
                    if (content != null && content.Contains("undefined_name")) {
                        foundPseudoBox = true;
                        break;
                    }
                }
            }
            Assert.That(foundPseudoBox, Is.True,
                "pseudo box with undefined counter should still be generated (empty text)");
        }

        // ── Test 10: counter-increment with explicit step value ───────────────

        [Test]
        public void Counter_increment_with_explicit_step() {
            // counter-reset: c 0; counter-increment: c 10 → counter(c) = 10.
            const string css = @"
                .parent { counter-reset: c 0; counter-increment: c 10; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "10");
            Assert.That(run, Is.Not.Null,
                "counter-increment:c 10 starting from 0 should produce counter(c)='10'");
        }

        // ── Test 11: counter with lower-alpha style ───────────────────────────

        [Test]
        public void Counter_with_lower_alpha_style() {
            // counter-reset: c 0; counter-increment: c 1 → counter(c, lower-alpha) = "a".
            const string css = @"
                .parent { counter-reset: c; counter-increment: c; }
                .child::before { content: counter(c, lower-alpha); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            // counter value 1 → lower-alpha → "a"
            var run = FindTextRun(root, "a");
            Assert.That(run, Is.Not.Null,
                "counter(c, lower-alpha) with value 1 should produce 'a'");
        }

        // ── Test 12: negative counter-increment ───────────────────────────────

        [Test]
        public void Counter_with_negative_increment() {
            // counter-reset: c 10; counter-increment: c -3 → counter(c) = 7.
            const string css = @"
                .parent { counter-reset: c 10; counter-increment: c -3; }
                .child::before { content: counter(c); }
            ";
            var (root, _) = BuildWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var run = FindTextRun(root, "7");
            Assert.That(run, Is.Not.Null,
                "counter-reset:c 10; counter-increment:c -3 should produce counter(c)='7'");
        }
    }
}
