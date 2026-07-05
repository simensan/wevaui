using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS Generated Content Level 3 §3 — `quotes` property + open-quote /
    // close-quote / no-open-quote / no-close-quote content keywords, including
    // quote-depth tracking across document-order elements.
    //
    // Tests mirror the CounterScopeTests pattern: boxes are built with
    // BeforeStyleOf / AfterStyleOf wired so the CounterContext tree walk
    // accumulates quote depth from preceding pseudo-elements in document order.
    //
    // Spec refs:
    //   CSS Generated Content L3 §3: `quotes` syntax and inheritance.
    //   CSS Generated Content L3 §3.1: open-quote / close-quote content keywords.
    //   CSS2 §12.4: generated content overview.
    //   CSS Containment L2 §3.3: style containment isolates quote depth.
    public class QuoteDepthTests {

        // ── Helpers ──────────────────────────────────────────────────────────

        // Builds a box tree with ::before / ::after pseudo-element resolvers
        // wired to the CascadeEngine, including the full UA stylesheet so the
        // built-in `q` rules fire.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildWithPseudos(
            string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            // Use the full UA stylesheet (includes `q::before { content: open-quote }` etc.)
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(UserAgentStylesheet.Source)));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));

            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);

            return (bb.BuildDocument(doc), styles);
        }

        // Builds WITHOUT the full UA stylesheet (uses the layout-test minimal UA).
        // Used when testing quote depth without the built-in `q` rules.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildMinimalWithPseudos(
            string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent +
                "\nq { display: inline; }")));
            if (!string.IsNullOrEmpty(css))
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));

            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);

            return (bb.BuildDocument(doc), styles);
        }

        // Collect all TextRun texts from pseudo-element boxes (Element == null)
        // that are non-whitespace.
        static List<string> PseudoTexts(Box root) {
            var result = new List<string>();
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Element == null
                    && tr.Text != null && tr.Text.Trim().Length > 0) {
                    result.Add(tr.Text);
                }
            }
            return result;
        }

        // Returns the concatenated text of all pseudo TextRuns in document order.
        static string PseudoTextConcat(Box root) {
            var sb = new System.Text.StringBuilder();
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Element == null && tr.Text != null)
                    sb.Append(tr.Text);
            }
            return sb.ToString();
        }

        // ── 1. Default `auto` pair at depth 0 ────────────────────────────────

        [Test]
        public void Auto_quotes_depth0_inserts_English_typographic_open() {
            // CSS Generated Content L3 §3: `quotes: auto` for an English document
            // must resolve to typographic left/right double quotes at depth 0.
            // Chrome: opens with U+201C ("), closes with U+201D (").
            const string css = @"
                .wrap::before { content: open-quote; }
                .wrap::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"wrap\">text</span>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.GreaterThanOrEqualTo(2),
                "must have ::before and ::after pseudo runs");
            Assert.That(texts[0], Is.EqualTo("“"),
                "depth-0 open-quote with auto must be U+201C LEFT DOUBLE QUOTATION MARK");
            Assert.That(texts[texts.Count - 1], Is.EqualTo("”"),
                "depth-0 close-quote with auto must be U+201D RIGHT DOUBLE QUOTATION MARK");
        }

        [Test]
        public void Auto_quotes_depth1_inserts_English_typographic_single() {
            // At depth 1 (inside an outer open-quote), `auto` uses single quotes
            // for English: U+2018 (') and U+2019 (').
            const string css = @"
                .outer::before { content: open-quote; }
                .inner::before { content: open-quote; }
                .inner::after  { content: close-quote; }
                .outer::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"outer\"><span class=\"inner\">text</span></span>", css);

            var texts = PseudoTexts(root);
            // In tree order: outer::before, inner::before, inner::after, outer::after
            Assert.That(texts, Has.Count.EqualTo(4),
                "must have 4 pseudo runs (outer::before, inner::before, inner::after, outer::after)");
            Assert.That(texts[0], Is.EqualTo("“"), "outer::before = depth-0 open = \"");
            Assert.That(texts[1], Is.EqualTo("‘"), "inner::before = depth-1 open = ‘");
            Assert.That(texts[2], Is.EqualTo("’"), "inner::after  = depth-1 close = ’");
            Assert.That(texts[3], Is.EqualTo("”"), "outer::after  = depth-0 close = \"");
        }

        // ── 2. Authored single pair ───────────────────────────────────────────

        [Test]
        public void Authored_single_pair_at_depth_0() {
            // `quotes: "<" ">"` — single pair means all levels use the same marks.
            const string css = @"
                .wrap { quotes: '<' '>'; }
                .wrap::before { content: open-quote; }
                .wrap::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"wrap\">text</span>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(2),
                "must have exactly ::before and ::after pseudo runs");
            Assert.That(texts[0], Is.EqualTo("<"), "authored open quote must be <");
            Assert.That(texts[1], Is.EqualTo(">"), "authored close quote must be >");
        }

        // ── 3. Multi-level pairs + clamping ──────────────────────────────────

        [Test]
        public void Two_level_pairs_selects_correct_pair_per_depth() {
            // `quotes: '"' '"' "'" "'"` — outer double, inner single.
            const string css = @"
                .outer { quotes: '""' '""' ""'"" ""'""; }
                .outer::before { content: open-quote; }
                .inner::before { content: open-quote; }
                .inner::after  { content: close-quote; }
                .outer::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<div class=\"outer\"><span class=\"inner\">x</span></div>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(4), "must have 4 pseudo runs");
            Assert.That(texts[0], Is.EqualTo("\""), "depth-0 open = double quote");
            Assert.That(texts[1], Is.EqualTo("'"),  "depth-1 open = single quote");
            Assert.That(texts[2], Is.EqualTo("'"),  "depth-1 close = single quote");
            Assert.That(texts[3], Is.EqualTo("\""), "depth-0 close = double quote");
        }

        [Test]
        public void Depth_clamped_to_last_pair_when_exceeded() {
            // Single pair + 2 levels of nesting: depth-0 and depth-1 both
            // must use the single pair (clamping at last pair per spec).
            const string css = @"
                .a { quotes: '(' ')'; }
                .a::before { content: open-quote; }
                .b::before { content: open-quote; }
                .c::before { content: open-quote; }
                .c::after  { content: close-quote; }
                .b::after  { content: close-quote; }
                .a::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<div class=\"a\"><div class=\"b\"><span class=\"c\">x</span></div></div>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(6), "6 pseudo runs (3 opens + 3 closes)");
            // All levels must use '(' and ')' since there's only 1 pair.
            foreach (var t in texts) {
                Assert.That(t == "(" || t == ")", Is.True,
                    $"all marks must be '(' or ')' when only one pair is defined, got '{t}'");
            }
        }

        // ── 4. `no-open-quote` / `no-close-quote` — depth without insertion ──

        [Test]
        public void No_open_quote_increments_depth_without_inserting() {
            // After a no-open-quote (depth becomes 1), the next open-quote
            // should use the depth-1 pair (single quote for English auto).
            const string css = @"
                .outer::before { content: no-open-quote; }
                .inner::before { content: open-quote; }
                .inner::after  { content: close-quote; }
                .outer::after  { content: no-close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"outer\"><span class=\"inner\">text</span></span>", css);

            var texts = PseudoTexts(root);
            // outer::before → no-open-quote (depth: 0→1, no insert)
            // inner::before → open-quote at depth 1 = '
            // inner::after  → close-quote at depth 1→0 = '
            // outer::after  → no-close-quote (depth: 0→0, no insert)
            // Only inner::before and inner::after produce text.
            Assert.That(texts, Has.Count.EqualTo(2),
                "no-open-quote and no-close-quote must not insert text");
            Assert.That(texts[0], Is.EqualTo("‘"),
                "inner::before at depth 1 must be U+2018 (single open)");
            Assert.That(texts[1], Is.EqualTo("’"),
                "inner::after at depth 1 must be U+2019 (single close)");
        }

        [Test]
        public void No_close_quote_decrements_depth_without_inserting() {
            // Symmetric to no_open_quote: the outer close is suppressed but depth adjusts.
            const string css = @"
                .a::before { content: open-quote; }
                .b::before { content: open-quote; }
                .b::after  { content: no-close-quote; }
                .a::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"a\"><span class=\"b\">text</span></span>", css);

            var texts = PseudoTexts(root);
            // a::before  → open-quote depth 0→1, inserts "
            // b::before  → open-quote depth 1→2, inserts '
            // b::after   → no-close-quote depth 2→1, no insert
            // a::after   → close-quote depth 1→0, inserts "
            Assert.That(texts, Has.Count.EqualTo(3),
                "no-close-quote must not insert text; 3 pseudo runs expected");
            Assert.That(texts[0], Is.EqualTo("“"), "a::before at depth 0 = \"");
            Assert.That(texts[1], Is.EqualTo("‘"), "b::before at depth 1 = '");
            Assert.That(texts[2], Is.EqualTo("”"), "a::after after no-close-quote = depth 0 = \"");
        }

        // ── 5. `quotes: none` — no insertion but depth still adjusts ─────────

        [Test]
        public void Quotes_none_inserts_nothing_for_open_quote() {
            // CSS Generated Content L3 §3: `quotes: none` means open-quote /
            // close-quote produce empty strings.
            const string css = @"
                .wrap { quotes: none; }
                .wrap::before { content: open-quote; }
                .wrap::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"wrap\">text</span>", css);

            var texts = PseudoTexts(root);
            // Both pseudo runs exist but produce "" — they are still generated
            // but with empty text, so pseudo boxes exist. The PseudoTexts helper
            // filters to non-whitespace runs (Trim().Length > 0), so we expect 0.
            Assert.That(texts, Has.Count.EqualTo(0),
                "quotes: none must produce empty strings for open-quote / close-quote");
        }

        [Test]
        public void Quotes_none_still_adjusts_depth_for_nested_element() {
            // The outer element has `quotes: none` with an open-quote. The inner
            // element has `quotes: auto`. Despite the outer emitting no marks,
            // the depth IS incremented, so the inner open-quote lands at depth 1
            // (single quote for English).
            const string css = @"
                .outer { quotes: none; }
                .inner { quotes: auto; }
                .outer::before { content: open-quote; }
                .inner::before { content: open-quote; }
                .inner::after  { content: close-quote; }
                .outer::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"outer\"><span class=\"inner\">text</span></span>", css);

            var texts = PseudoTexts(root);
            // outer::before → open-quote (quotes:none → "", depth 0→1)
            // inner::before → open-quote (quotes:auto, depth 1 → single)
            // inner::after  → close-quote (quotes:auto, depth 1→0 → single)
            // outer::after  → close-quote (quotes:none → "")
            // Only inner's marks are non-empty.
            Assert.That(texts, Has.Count.EqualTo(2),
                "inner marks must be present even when outer has quotes:none");
            Assert.That(texts[0], Is.EqualTo("‘"),
                "inner open at depth 1 = U+2018 single open");
            Assert.That(texts[1], Is.EqualTo("’"),
                "inner close at depth 0 (after decrement) = U+2019 single close");
        }

        // ── 6. Unmatched close-quote at depth 0 ──────────────────────────────

        [Test]
        public void Unmatched_close_quote_at_depth_0_uses_depth_0_string() {
            // CSS Generated Content L3 §3.1: a close-quote at depth 0 (unmatched)
            // clamps to 0 — the string for the outermost level is used, depth stays 0.
            // Chrome inserts the level-0 close quote and leaves depth at 0.
            const string css = @"
                .wrap::after { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"wrap\">text</span>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(1),
                "unmatched close-quote at depth 0 must still produce a text run");
            Assert.That(texts[0], Is.EqualTo("”"),
                "unmatched close-quote at depth 0 must use depth-0 close string");
        }

        // ── 7. `quotes` inheritance ───────────────────────────────────────────

        [Test]
        public void Quotes_is_inherited_from_ancestor() {
            // `quotes` is inherited. Setting it on a parent means the child's
            // ::before / ::after use the inherited pair without repeating it.
            const string css = @"
                .parent { quotes: '<' '>'; }
                .child::before { content: open-quote; }
                .child::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<div class=\"parent\"><span class=\"child\">text</span></div>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(2), "must have ::before and ::after runs");
            Assert.That(texts[0], Is.EqualTo("<"), "inherited open quote must be <");
            Assert.That(texts[1], Is.EqualTo(">"), "inherited close quote must be >");
        }

        [Test]
        public void Child_quotes_none_overrides_inherited_pairs() {
            // A child with `quotes: none` overrides the inherited value so its own
            // open/close produce no marks.
            const string css = @"
                .parent { quotes: '<' '>'; }
                .child  { quotes: none; }
                .child::before { content: open-quote; }
                .child::after  { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<div class=\"parent\"><span class=\"child\">x</span></div>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(0),
                "child with quotes:none must not produce marks even though parent has pairs");
        }

        // ── 8. Document-order depth across siblings ───────────────────────────

        [Test]
        public void Open_in_first_sibling_close_in_second_sibling() {
            // Quote depth is accumulated document-order across siblings.
            // The first element opens a quote; the second closes it.
            const string css = @"
                .open::before  { content: open-quote; }
                .close::before { content: close-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<span class=\"open\">hello</span><span class=\"close\">world</span>", css);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(2), "must have 2 pseudo runs");
            Assert.That(texts[0], Is.EqualTo("“"), ".open::before = depth-0 open = \"");
            Assert.That(texts[1], Is.EqualTo("”"), ".close::before = depth after decrement = 0 = \"");
        }

        // ── 9. UA `<q>` element — built-in rule ──────────────────────────────

        [Test]
        public void Q_element_gets_UA_quotes_via_builtin_rules() {
            // The UA stylesheet must include `q::before { content: open-quote }` and
            // `q::after { content: close-quote }`. With no author override, a bare
            // `<q>` element should generate English typographic marks.
            var (root, _) = BuildWithPseudos("<q>text</q>", null);

            var texts = PseudoTexts(root);
            Assert.That(texts, Has.Count.EqualTo(2),
                "<q> must have ::before and ::after pseudo runs from UA stylesheet");
            Assert.That(texts[0], Is.EqualTo("“"),
                "<q>::before must be U+201C LEFT DOUBLE QUOTATION MARK from auto");
            Assert.That(texts[1], Is.EqualTo("”"),
                "<q>::after must be U+201D RIGHT DOUBLE QUOTATION MARK from auto");
        }

        [Test]
        public void Nested_q_elements_use_inner_pair_for_nested() {
            // Nested `<q>` elements: the outer uses double quotes, the inner uses
            // single quotes. Depth tracking across nested elements.
            var (root, _) = BuildWithPseudos("<q>outer <q>inner</q> text</q>", null);

            var texts = PseudoTexts(root);
            // Document order: outer::before, inner::before, inner::after, outer::after
            Assert.That(texts, Has.Count.EqualTo(4),
                "two nested <q> must produce 4 pseudo runs");
            Assert.That(texts[0], Is.EqualTo("“"), "outer open = \"");
            Assert.That(texts[1], Is.EqualTo("‘"), "inner open = '");
            Assert.That(texts[2], Is.EqualTo("’"), "inner close = '");
            Assert.That(texts[3], Is.EqualTo("”"), "outer close = \"");
        }

        // ── 10. `contain: style` resets / isolates quote depth ───────────────

        [Test]
        public void Style_containment_boundary_prevents_outer_depth_leak_inward() {
            // A style-contained element's quote adjustments must not affect the
            // depth seen by later siblings outside the boundary.
            // Spec: CSS Containment L2 §3.3 mirrors the counter isolation.
            const string css = @"
                .contained { contain: style; }
                .contained::before { content: open-quote; }
                .contained::after  { content: close-quote; }
                .after::before     { content: open-quote; }
            ";
            var (root, _) = BuildMinimalWithPseudos(
                "<div><span class=\"contained\">x</span><span class=\"after\">y</span></div>",
                css);

            var texts = PseudoTexts(root);
            // .contained::before   → open-quote at depth 0 = "
            // .contained::after    → close-quote at depth 0 = "
            // .after::before       → open-quote at depth 0 = " (depth not leaked by contained)
            Assert.That(texts, Has.Count.EqualTo(3), "3 pseudo runs expected");
            // All three must be the depth-0 string since containment prevents depth leaking.
            Assert.That(texts[0], Is.EqualTo("“"), "contained::before = depth-0 open");
            Assert.That(texts[2], Is.EqualTo("“"), ".after::before must see depth 0 (not depth leaking from contained)");
        }

        // ── 11. `ParseQuotePairs` unit tests ─────────────────────────────────

        [Test]
        public void ParseQuotePairs_auto_returns_English_typographic_two_levels() {
            var pairs = CascadeEngine.ParseQuotePairs("auto");
            Assert.That(pairs, Is.Not.Null, "auto must return non-null pairs");
            Assert.That(pairs.Length, Is.EqualTo(2), "auto must return 2 pairs for English");
            Assert.That(pairs[0][0], Is.EqualTo("“"), "auto outer open = U+201C");
            Assert.That(pairs[0][1], Is.EqualTo("”"), "auto outer close = U+201D");
            Assert.That(pairs[1][0], Is.EqualTo("‘"), "auto inner open = U+2018");
            Assert.That(pairs[1][1], Is.EqualTo("’"), "auto inner close = U+2019");
        }

        [Test]
        public void ParseQuotePairs_null_returns_English_typographic_default() {
            var pairs = CascadeEngine.ParseQuotePairs(null);
            Assert.That(pairs, Is.Not.Null, "null (unset) must return non-null pairs");
            Assert.That(pairs.Length, Is.EqualTo(2), "null must return 2 English pairs");
        }

        [Test]
        public void ParseQuotePairs_none_returns_null() {
            var pairs = CascadeEngine.ParseQuotePairs("none");
            Assert.That(pairs, Is.Null, "quotes:none must return null");
        }

        [Test]
        public void ParseQuotePairs_single_authored_pair() {
            var pairs = CascadeEngine.ParseQuotePairs("\"<\" \">\"");
            Assert.That(pairs, Is.Not.Null);
            Assert.That(pairs.Length, Is.EqualTo(1), "one pair");
            Assert.That(pairs[0][0], Is.EqualTo("<"), "open");
            Assert.That(pairs[0][1], Is.EqualTo(">"), "close");
        }

        [Test]
        public void ParseQuotePairs_two_authored_pairs() {
            var pairs = CascadeEngine.ParseQuotePairs("\"(\" \")\" \"[\" \"]\"");
            Assert.That(pairs, Is.Not.Null);
            Assert.That(pairs.Length, Is.EqualTo(2), "two pairs");
            Assert.That(pairs[0][0], Is.EqualTo("("), "outer open");
            Assert.That(pairs[1][0], Is.EqualTo("["), "inner open");
        }

        // ── 12. ResolveContentString — direct unit tests ──────────────────────

        [Test]
        public void ResolveContentString_open_quote_returns_auto_depth0_mark() {
            // Direct invocation of ResolveContentString with a null context (depth 0).
            // When counterCtx is null, depth defaults to 0.
            string result = CascadeEngine.ResolveContentString("open-quote", null, null, "auto");
            Assert.That(result, Is.EqualTo("“"),
                "open-quote with auto and null ctx (depth 0) must be U+201C");
        }

        [Test]
        public void ResolveContentString_close_quote_returns_auto_depth0_mark() {
            string result = CascadeEngine.ResolveContentString("close-quote", null, null, "auto");
            Assert.That(result, Is.EqualTo("”"),
                "close-quote with auto and null ctx (depth 0) must be U+201D");
        }

        [Test]
        public void ResolveContentString_open_quote_quotes_none_returns_empty() {
            string result = CascadeEngine.ResolveContentString("open-quote", null, null, "none");
            Assert.That(result, Is.EqualTo(""),
                "open-quote with quotes:none must produce empty string, not null");
        }

        [Test]
        public void ResolveContentString_close_quote_quotes_none_returns_empty() {
            string result = CascadeEngine.ResolveContentString("close-quote", null, null, "none");
            Assert.That(result, Is.EqualTo(""),
                "close-quote with quotes:none must produce empty string, not null");
        }

        [Test]
        public void ResolveContentString_no_open_quote_returns_empty() {
            string result = CascadeEngine.ResolveContentString("no-open-quote", null, null, "auto");
            Assert.That(result, Is.EqualTo(""),
                "no-open-quote must produce empty string (no insertion)");
        }

        [Test]
        public void ResolveContentString_no_close_quote_returns_empty() {
            string result = CascadeEngine.ResolveContentString("no-close-quote", null, null, "auto");
            Assert.That(result, Is.EqualTo(""),
                "no-close-quote must produce empty string (no insertion)");
        }

        [Test]
        public void ResolveContentString_open_quote_with_authored_pair() {
            string result = CascadeEngine.ResolveContentString("open-quote", null, null, "\"[\" \"]\"");
            Assert.That(result, Is.EqualTo("["),
                "open-quote with authored pair '[' ']' at depth 0 must be [");
        }

        [Test]
        public void ResolveContentString_multi_segment_quote_and_string() {
            // `content: open-quote "hello" close-quote` with authored pair < >.
            string result = CascadeEngine.ResolveContentString(
                "open-quote \"hello\" close-quote", null, null, "\"<\" \">\"");
            Assert.That(result, Is.EqualTo("<hello>"),
                "multi-segment: open-quote + string + close-quote must concatenate");
        }

        // ── 13. Cascade-level registration sanity ────────────────────────────

        [Test]
        public void Quotes_property_is_registered_inherited_initial_auto_in_cascade() {
            // Duplicate of QuotesAndQuoteContentTests.Quotes_is_registered_inherited_initial_auto
            // kept here so the quote-depth test file is self-contained for regression.
            int id = CssProperties.GetId("quotes");
            Assert.That(id, Is.GreaterThanOrEqualTo(0), "quotes must be registered");
            var prop = CssProperties.Get(id);
            Assert.That(prop.IsInherited, Is.True, "quotes must be inherited");
            Assert.That(prop.InitialValue, Is.EqualTo("auto"), "initial value must be auto");
        }
    }
}
