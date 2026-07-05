using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Selectors;
using Weva.Css.Values;
using Weva.Layout.Grid;
using Weva.Parsing;

namespace Weva.Tests.Css.Parsing {
    // Audit PH1-PH4: adversarial-input hardening. Every input here
    // previously crashed the process (uncatchable StackOverflow), OOM'd it,
    // or blanked the whole document — run-confirmed in the 2026-07 audit.
    // These pins double as tripwires: if a cap regresses, the SOE cases
    // kill the test runner loudly instead of shipping.
    public class CssParserHardeningTests {
        static ParseOptions Lenient => new ParseOptions { ThrowOnError = false };

        // ── PH1: rule-nesting recursion cap ────────────────────────────────

        [Test]
        public void Deeply_nested_at_rules_do_not_overflow_the_stack_PH1() {
            // 20,000 nested `@media all{` ≈ 200KB — previously an uncatchable
            // StackOverflow that killed the editor/player.
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++) sb.Append("@media all{");
            sb.Append(".x{color:red}");
            for (int i = 0; i < 20000; i++) sb.Append("}");
            sb.Append(" .survivor { color: blue; }");
            var sheet = CssParser.Parse(sb.ToString(), Lenient);
            Assert.That(sheet, Is.Not.Null, "parse must complete, not crash");
            // The over-depth tail is dropped; the trailing sibling rule must
            // still parse (the skipper resyncs at the rule boundary).
            bool survivorFound = false;
            foreach (var r in sheet.Rules) {
                if (r is StyleRule sr && sr.Selectors.Contains(".survivor")) survivorFound = true;
            }
            Assert.That(survivorFound, Is.True, "rules after the bomb must survive");
        }

        [Test]
        public void Deeply_nested_style_rules_do_not_overflow_the_stack_PH1() {
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++) sb.Append(".a{");
            sb.Append("color:red;");
            for (int i = 0; i < 20000; i++) sb.Append("}");
            var sheet = CssParser.Parse(sb.ToString(), Lenient);
            Assert.That(sheet, Is.Not.Null, "parse must complete, not crash");
        }

        [Test]
        public void Reasonable_nesting_still_parses_inside_the_cap_PH1() {
            // 20 levels — deep but legitimate; must be unaffected by the cap.
            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++) sb.Append("@media all{");
            sb.Append(".deep{color:red}");
            for (int i = 0; i < 20; i++) sb.Append("}");
            var sheet = CssParser.Parse(sb.ToString(), Lenient);
            int found = CountStyleRules(sheet);
            Assert.That(found, Is.GreaterThanOrEqualTo(1), "in-cap nesting must keep its rules");
        }

        static int CountStyleRules(Stylesheet sheet) {
            int n = 0;
            void Walk(System.Collections.Generic.List<Rule> rules) {
                foreach (var r in rules) {
                    if (r is StyleRule) n++;
                    if (r is MediaRule mr) Walk(mr.Rules);
                }
            }
            Walk(sheet.Rules);
            return n;
        }

        // ── PH1: calc() recursion cap ─────────────────────────────────────

        [Test]
        public void Deeply_nested_calc_fails_gracefully_not_SOE_PH1() {
            // calc( + 10,000 '(' — previously SOE at value-RESOLVE time: a
            // property value could crash a shipped app.
            var sb = new StringBuilder("calc(");
            for (int i = 0; i < 10000; i++) sb.Append('(');
            sb.Append("1px");
            for (int i = 0; i < 10000; i++) sb.Append(')');
            sb.Append(')');
            bool ok = CssValue.TryParseSilent(sb.ToString(), out _);
            Assert.That(ok, Is.False, "over-depth calc must be an invalid value, not a crash");
        }

        [Test]
        public void Reasonably_nested_calc_still_parses_PH1() {
            bool ok = CssValue.TryParse("calc(((((1px + 2px)))))", out var v);
            Assert.That(ok, Is.True, "in-cap calc nesting must parse");
            Assert.That(v, Is.Not.Null);
        }

        // ── PH1: selector pseudo-class recursion cap ──────────────────────

        [Test]
        public void Deeply_nested_not_throws_catchable_parse_error_PH1() {
            // :not(:not(:not(…))) — only :has nesting was capped before; this
            // drove ParsePseudoClass <-> ParseSequenceList to SOE.
            var sb = new StringBuilder();
            for (int i = 0; i < 10000; i++) sb.Append(":not(");
            sb.Append(".x");
            for (int i = 0; i < 10000; i++) sb.Append(')');
            Assert.Throws<SelectorParseException>(
                () => SelectorParser.Parse("div" + sb),
                "over-depth selector must throw the CATCHABLE parse exception (EC11 rule drop), not SOE");
        }

        [Test]
        public void Reasonably_nested_not_still_parses_PH1() {
            var sel = SelectorParser.Parse("div:not(:not(:is(.a, .b)))");
            Assert.That(sel, Is.Not.Null);
        }

        // ── PH2: nesting cross-product budget ─────────────────────────────

        [Test]
        public void Nesting_cross_product_bomb_is_budgeted_PH2() {
            // 114 chars -> 1,048,576 selectors pre-fix; 30 levels OOM'd.
            var sb = new StringBuilder(".a{");
            for (int i = 0; i < 30; i++) sb.Append("&,&{");
            sb.Append("color:red;");
            for (int i = 0; i < 31; i++) sb.Append("}");
            var sheet = CssParser.Parse(sb.ToString(), Lenient);
            int selectorCount = 0;
            foreach (var r in sheet.Rules) {
                if (r is StyleRule sr) selectorCount += sr.Selectors.Count;
            }
            Assert.That(selectorCount, Is.LessThanOrEqualTo(70000),
                "expansion must respect the per-sheet budget (2^30 pre-fix = OOM)");
        }

        [Test]
        public void Normal_nesting_still_expands_correctly_PH2() {
            var sheet = CssParser.Parse(".a { color: blue; &.b { color: red; } }", Lenient);
            bool found = false;
            foreach (var r in sheet.Rules) {
                if (r is StyleRule sr && sr.Selectors.Contains(".a.b")) found = true;
            }
            Assert.That(found, Is.True, "&.b under .a must expand to .a.b");
        }

        // ── PH3: tokenizer bad-string / bad-url recovery ──────────────────

        [Test]
        public void Unterminated_string_does_not_blank_the_sheet_PH3() {
            // Pre-fix ConsumeString threw regardless of ThrowOnError=false;
            // the exception escaped to WevaDocument which nulled its state —
            // one bad token anywhere blanked the ENTIRE document.
            string css = ".a { color: red; }\n"
                + ".b { content: \"oops\n; color: blue; }\n"
                + ".c { color: green; }";
            Stylesheet sheet = null;
            Assert.DoesNotThrow(() => sheet = CssParser.Parse(css, Lenient),
                "lenient parse must recover per CSS Syntax bad-string rules");
            bool aOk = false, cOk = false;
            foreach (var r in sheet.Rules) {
                if (r is StyleRule sr) {
                    if (sr.Selectors.Contains(".a")) aOk = true;
                    if (sr.Selectors.Contains(".c")) cOk = true;
                }
            }
            Assert.That(aOk, Is.True, "rule before the bad string survives");
            Assert.That(cOk, Is.True, "rule after the bad string survives");
        }

        [Test]
        public void Malformed_url_does_not_blank_the_sheet_PH3() {
            string css = ".a { color: red; } .b { background: url(a\"b); } .c { color: green; }";
            Stylesheet sheet = null;
            Assert.DoesNotThrow(() => sheet = CssParser.Parse(css, Lenient));
            bool cOk = false;
            foreach (var r in sheet.Rules) {
                if (r is StyleRule sr && sr.Selectors.Contains(".c")) cOk = true;
            }
            Assert.That(cOk, Is.True, "rule after the bad url survives");
        }

        [Test]
        public void Strict_mode_still_throws_on_bad_string_PH3() {
            Assert.Throws<CssParseException>(
                () => CssParser.Parse(".b { content: \"oops\n; }", new ParseOptions { ThrowOnError = true }));
        }

        // ── PH4: repeat() count clamp ─────────────────────────────────────

        [Test]
        public void Huge_repeat_count_is_clamped_PH4() {
            // repeat(2000000000, 1px [a] 2px) previously allocated billions
            // of track entries + a List<string> per grid line at layout time.
            var ctx = LengthContext.Default;
            var template = GridTrackParser.Parse("repeat(2000000000, 1px [a] 2px)", ctx);
            Assert.That(template.Tracks.Count, Is.EqualTo(GridTrackParser.MaxRepeatCount * 2),
                "count must clamp to MaxRepeatCount (pattern of 2 tracks)");
        }

        [Test]
        public void Normal_repeat_is_unaffected_PH4() {
            var ctx = LengthContext.Default;
            var template = GridTrackParser.Parse("repeat(3, 1fr 2fr)", ctx);
            Assert.That(template.Tracks.Count, Is.EqualTo(6));
        }
    }
}
