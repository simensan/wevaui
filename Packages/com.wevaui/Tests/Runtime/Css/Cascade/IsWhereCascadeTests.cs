using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Selectors L4 §17 + §4.2 — :is() and :where() cascade interaction.
    //
    // :is(.a, .b) { ... } matches any element satisfying at least one argument
    // and contributes the MAX specificity of its arguments to the rule.
    // :where(.a, .b) { ... } always contributes (0,0,0) regardless of arguments.
    //
    // Spec references:
    //   :is()    — CSS Selectors L4 §4.2 (forgiving selector list, §17 specificity)
    //   :where() — CSS Selectors L4 §4.2 (zero specificity variant of :is())
    //   Cascade  — CSS Cascade L5 §6.4.3 (specificity comparison)
    public class IsWhereCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ── :is() matching ──────────────────────────────────────────────────

        [Test]
        public void Is_matches_any_arg_in_list() {
            // :is(div, p) must match both <div> and <p>.
            var doc = Html("<div id=\"d\"></div><p id=\"p\"></p>");
            var engine = new CascadeEngine(new[] { Author(":is(div, p) { color: red; }") });
            Assert.That(engine.Compute(doc.GetElementById("d")).Get("color"), Is.EqualTo("red"),
                ":is(div, p) must match <div>");
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                ":is(div, p) must match <p>");
        }

        [Test]
        public void Is_does_not_match_elements_outside_arg_list() {
            var doc = Html("<div id=\"d\"></div><span id=\"s\"></span>");
            var engine = new CascadeEngine(new[] { Author(":is(div, p) { color: red; }") });
            Assert.That(engine.Compute(doc.GetElementById("s")).Get("color"), Is.Not.EqualTo("red"),
                ":is(div, p) must NOT match <span>");
        }

        [Test]
        public void Is_with_class_arg_matches_element_carrying_class() {
            var doc = Html("<div id=\"x\" class=\"card\"></div><div id=\"y\"></div>");
            var engine = new CascadeEngine(new[] { Author(":is(.card, .hero) { font-weight: bold; }") });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("font-weight"), Is.EqualTo("bold"),
                "element with class 'card' must match :is(.card, .hero)");
            Assert.That(engine.Compute(doc.GetElementById("y")).Get("font-weight"), Is.Not.EqualTo("bold"),
                "element without any listed class must not match");
        }

        // ── :is() specificity: takes the MAX of its arguments ───────────────

        [Test]
        public void Is_id_arg_raises_specificity_above_plain_class() {
            // :is(.c, #x) → (1,0,0) which beats .c → (0,1,0).
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: blue; } :is(.c, #x) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                ":is(.c,#x) specificity (1,0,0) must beat .c (0,1,0)");
        }

        [Test]
        public void Is_nested_in_compound_accumulates_specificity() {
            // div:is(.hero, #main) has specificity (1,0,1) when #main is the max arg.
            // .c rule has (0,1,0) and loses to (1,0,1).
            var doc = Html("<div id=\"main\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: blue; } div:is(.hero, #main) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("main")).Get("color"), Is.EqualTo("red"),
                "div:is(.hero,#main) specificity (1,0,1) beats .c (0,1,0)");
        }

        [Test]
        public void Is_multiple_args_all_classes_specificity_is_single_class() {
            // :is(.a, .b, .c) → max arg is (0,1,0); rule loses to #id (1,0,0).
            var doc = Html("<div id=\"x\" class=\"a\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":is(.a, .b, .c) { color: red; } #x { color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"),
                "#x (1,0,0) must beat :is(.a,.b,.c) (0,1,0)");
        }

        // ── :where() matching ───────────────────────────────────────────────

        [Test]
        public void Where_matches_element_per_arg_list() {
            // :where() matches identically to :is() — only specificity differs.
            var doc = Html("<div id=\"x\" class=\"card\"></div><span id=\"s\"></span>");
            var engine = new CascadeEngine(new[] { Author(":where(div, .card) { color: red; }") });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("s")).Get("color"), Is.Not.EqualTo("red"));
        }

        // ── :where() specificity: always zero ───────────────────────────────

        [Test]
        public void Where_id_arg_still_zero_specificity_loses_to_class() {
            // :where(#x) → (0,0,0); .c → (0,1,0). The class rule wins even
            // though #x would normally be (1,0,0).
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":where(#x) { color: red; } .c { color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"),
                ":where(#x) is (0,0,0); .c (0,1,0) must win");
        }

        [Test]
        public void Where_loses_to_type_selector_on_source_order_only() {
            // :where(#x) is (0,0,0); `div` is (0,0,1).
            // Both match the element; div comes later → source order wins within
            // the same specificity bucket (actually div > :where so div wins by specificity).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":where(#x) { color: red; } div { color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("blue"),
                "div (0,0,1) must beat :where(#x) (0,0,0) by specificity");
        }

        [Test]
        public void Where_loses_to_later_same_zero_specificity_rule_by_source_order() {
            // Two :where() rules with (0,0,0) — last in source order wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":where(#x) { color: red; } :where(div) { color: green; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("green"),
                "when both are (0,0,0), later source order wins");
        }

        // ── :is() vs :where() competing on same element ─────────────────────

        [Test]
        public void Is_beats_where_with_id_arg_even_though_where_comes_later() {
            // :is(#x) → (1,0,0) wins over :where(#x) → (0,0,0).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":where(#x) { color: green; } :is(#x) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("x")).Get("color"), Is.EqualTo("red"),
                ":is(#x) (1,0,0) must win over :where(#x) (0,0,0) regardless of order");
        }

        [Test]
        public void Where_as_zero_specificity_grouping_anchor() {
            // Authoring pattern: :where(main, article) groups selectors without
            // boosting specificity so local overrides can use low-specificity selectors.
            // .card rule (0,1,0) overrides :where(main, article) p (0,0,1).
            var doc = Html("<main><p id=\"p\" class=\"card\">x</p></main>");
            var engine = new CascadeEngine(new[] {
                Author(":where(main, article) p { color: blue; } .card { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                ".card (0,1,0) must override :where(main,article) p (0,0,1) because :where contributes 0");
        }

        // ── Nested :is() ────────────────────────────────────────────────────

        [Test]
        public void Nested_is_in_is_matches_through_outer() {
            // :is(:is(div, p)) is equivalent to :is(div, p).
            var doc = Html("<div id=\"d\"></div><p id=\"p\"></p><span id=\"s\"></span>");
            var engine = new CascadeEngine(new[] { Author(":is(:is(div, p)) { color: red; }") });
            Assert.That(engine.Compute(doc.GetElementById("d")).Get("color"), Is.EqualTo("red"),
                ":is(:is(div, p)) must match div");
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                ":is(:is(div, p)) must match p");
            Assert.That(engine.Compute(doc.GetElementById("s")).Get("color"), Is.Not.EqualTo("red"),
                ":is(:is(div, p)) must NOT match span");
        }

        // ── Combinator + :is() / :where() ───────────────────────────────────

        [Test]
        public void Is_in_descendant_combinator_position() {
            // `div :is(span, em) { ... }` — span is a descendant of div.
            var doc = Html("<div><span id=\"s\">x</span></div><span id=\"out\">y</span>");
            var engine = new CascadeEngine(new[] { Author("div :is(span, em) { color: red; }") });
            Assert.That(engine.Compute(doc.GetElementById("s")).Get("color"), Is.EqualTo("red"),
                "descendant span matched via :is()");
            Assert.That(engine.Compute(doc.GetElementById("out")).Get("color"), Is.Not.EqualTo("red"),
                "span outside div must not match `div :is(span, em)`");
        }

        [Test]
        public void Where_in_descendant_combinator_position() {
            // `main :where(p, ul) { ... }` — p is a descendant of main.
            // Only (0,0,1) specificity from the `main` compound — :where contributes 0.
            var doc = Html("<main><p id=\"p\">x</p></main><p id=\"out\">y</p>");
            var engine = new CascadeEngine(new[] {
                Author("main :where(p, ul) { color: red; } .override { color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                "descendant p matched via :where()");
            Assert.That(engine.Compute(doc.GetElementById("out")).Get("color"), Is.Not.EqualTo("red"),
                "p outside main must not match `main :where(p, ul)`");
        }

        // ── Forgiving selector list (CSS Selectors L4 §4.2) ─────────────────

        [Test]
        public void Is_silently_drops_invalid_alternates_and_matches_valid_ones() {
            // `:is(:unknown-pseudo, p)` should parse without throwing and
            // match <p> — the invalid alternate is silently dropped per
            // CSS Selectors L4 §4.2 forgiving selector list semantics.
            var doc = Html("<p id=\"p\">x</p><div id=\"d\">y</div>");
            var engine = new CascadeEngine(new[] {
                Author(":is(:unknown-pseudo, p) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                "valid alternate p must still match after invalid alternate is dropped");
            Assert.That(engine.Compute(doc.GetElementById("d")).Get("color"), Is.Not.EqualTo("red"),
                "div must not match :is(:unknown, p)");
        }

        [Test]
        public void Where_with_all_invalid_alternates_produces_never_matching_rule() {
            // `:where(:bogus1, :bogus2)` — all alternates invalid → empty
            // inner list. The rule parses successfully but never matches.
            // The companion .other rule must still apply.
            var doc = Html("<p id=\"p\" class=\"other\">x</p>");
            var engine = new CascadeEngine(new[] {
                Author(":where(:bogus1, :bogus2) { color: red; } .other { color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("blue"),
                ":where with all-invalid alternates must never match, so .other wins");
        }

        [Test]
        public void Forgiving_skip_respects_nested_parentheses() {
            // `:is(:not(:bogus(.x, .y)), p)` — the first alternate contains
            // a comma inside nested parens which must NOT terminate the
            // alternate. The whole :not(:bogus(...)) parses as one alternate
            // and (because :bogus is unknown) it gets dropped. The :p alternate
            // remains valid.
            var doc = Html("<p id=\"p\">x</p>");
            var engine = new CascadeEngine(new[] {
                Author(":is(:not(:bogus(.x, .y)), p) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                "nested parens inside an invalid alternate must not break alternate boundaries");
        }

        [Test]
        public void Forgiving_skip_respects_attribute_string_commas() {
            // `:is([data-x="a,b"]:bogus, p)` — comma inside an attribute
            // value string must NOT split the alternate. The first alternate
            // is invalid (`:bogus`) and gets dropped; p remains.
            var doc = Html("<p id=\"p\">x</p>");
            var engine = new CascadeEngine(new[] {
                Author(":is([data-x=\"a,b\"]:bogus, p) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("p")).Get("color"), Is.EqualTo("red"),
                "comma inside attribute string must not split alternates");
        }
    }
}
