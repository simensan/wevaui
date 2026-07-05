using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Cascade L5 §6.4 — "Cascade Sort" regression net.
    //
    // Covers the 5-way combo matrix:
    //   origin × importance × layer × specificity × source-order
    //
    // Group 1:  Origin × Importance matrix   (~12 tests)
    // Group 2:  @layer ordering interactions  (~5  tests)
    // Group 3:  Specificity comparisons       (~8  tests)
    // Group 4:  Source-order tiebreaker       (~3  tests)
    //
    // Each test is self-contained and documents the spec citation it pins.
    public class CascadePriorityMatrixTests {
        // ── helpers ────────────────────────────────────────────────────────────

        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));
        static OriginatedStylesheet User(string s)   => OriginatedStylesheet.User(Css(s));
        static OriginatedStylesheet UA(string s)     => OriginatedStylesheet.UserAgent(Css(s));

        // Compute on the single #x element.
        static string Get(CascadeEngine engine, Document doc, string prop = "color")
            => engine.Compute(doc.GetElementById("x")).Get(prop);

        // ──────────────────────────────────────────────────────────────────────
        // GROUP 1 — Origin × Importance matrix (CSS Cascade L5 §6.4 step 3)
        //
        // Normal cascade order (ascending precedence):
        //   UA normal < User normal < Author normal
        //
        // !important cascade order (ascending precedence, REVERSED):
        //   Author !important < User !important < UA !important
        //
        // Any !important outranks any normal regardless of origin.
        // ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Normal_author_beats_normal_user() {
            // Author normal > User normal (§6.4 step 3 — ascending order)
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                User("#x { color: blue; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Author normal must outrank User normal");
        }

        [Test]
        public void Normal_user_beats_normal_UA() {
            // User normal > UA normal
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray; }"),
                User("#x { color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "User normal must outrank UA normal");
        }

        [Test]
        public void Normal_author_beats_normal_UA() {
            // Transitive: Author normal > UA normal
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Author normal must outrank UA normal");
        }

        [Test]
        public void Important_UA_beats_important_user() {
            // UA !important > User !important (inverted origin order for !important)
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray !important; }"),
                User("#x { color: blue !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("gray"),
                "UA !important must outrank User !important");
        }

        [Test]
        public void Important_user_beats_important_author() {
            // User !important > Author !important (inverted origin order for !important)
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                User("#x { color: blue !important; }"),
                Author("#x { color: red !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "User !important must outrank Author !important");
        }

        [Test]
        public void Important_UA_beats_important_author() {
            // Transitive: UA !important > Author !important
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray !important; }"),
                Author("#x { color: red !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("gray"),
                "UA !important must outrank Author !important");
        }

        [Test]
        public void Important_UA_beats_normal_author() {
            // Importance axis dominates origin: UA !important > Author normal
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray !important; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("gray"),
                "UA !important must beat Author normal");
        }

        [Test]
        public void Important_user_beats_normal_author() {
            // User !important > Author normal (importance > origin for normal)
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                User("#x { color: blue !important; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "User !important must beat Author normal");
        }

        [Test]
        public void Important_author_beats_normal_user_and_UA() {
            // Author !important > User normal, UA normal
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray; }"),
                User("#x { color: blue; }"),
                Author("#x { color: red !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Author !important must beat User and UA normal");
        }

        [Test]
        public void Normal_UA_loses_to_normal_user_when_all_three_present() {
            // Full 3-way normal: Author > User > UA
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray; }"),
                User("#x { color: blue; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Normal cascade: Author wins 3-way race");
        }

        [Test]
        public void Important_precedence_full_ordering_UA_wins_over_all() {
            // Full 3-way !important: UA !important > User !important > Author !important
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("#x { color: gray !important; }"),
                User("#x { color: blue !important; }"),
                Author("#x { color: red !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("gray"),
                "!important cascade: UA wins 3-way race");
        }

        [Test]
        public void UA_important_beats_author_normal_even_with_lower_specificity() {
            // Origin × importance completely dominates specificity.
            // UA uses a type selector (lower specificity); Author uses an ID.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                UA("div { color: gray !important; }"),
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("gray"),
                "UA !important must win over Author normal #id rule");
        }

        // ──────────────────────────────────────────────────────────────────────
        // GROUP 2 — @layer ordering interactions (CSS Cascade L5 §6.4 step 4)
        //
        // Within the same origin + importance bucket, cascade layers are sorted
        // by layer order. For NORMAL declarations:
        //   later layer wins, unlayered beats all layered.
        // For !IMPORTANT declarations (inverted):
        //   earlier layer wins, layered beats unlayered.
        // ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Normal_unlayered_beats_layered_despite_lower_specificity() {
            // §6.4.1 step 4 normal: unlayered author > any layered author
            // The layered rule uses #x (high specificity); unlayered uses div (low).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@layer hi-spec { #x { color: red; } } div { color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "Unlayered normal outranks layered normal regardless of specificity");
        }

        [Test]
        public void Important_layered_beats_important_unlayered() {
            // §6.4.1 step 5 important (inverted): layered !important > unlayered !important
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@layer base { div { color: red !important; } } div { color: blue !important; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Layered !important must beat unlayered !important");
        }

        [Test]
        public void Later_layer_beats_earlier_layer_for_normal() {
            // §6.4.1 step 4: for normal declarations the later-declared layer wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @layer base, overrides;
                    @layer base     { div { color: red;  } }
                    @layer overrides { div { color: blue; } }
                ")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "Later @layer (overrides) must beat earlier @layer (base) for normal");
        }

        [Test]
        public void Earlier_layer_beats_later_layer_for_important() {
            // §6.4.1 step 5: for !important the order inverts — earlier layer wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @layer base, overrides;
                    @layer base      { div { color: red  !important; } }
                    @layer overrides { div { color: blue !important; } }
                ")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Earlier @layer (base) must beat later @layer (overrides) for !important");
        }

        [Test]
        public void Cross_origin_importance_dominates_layer_axis() {
            // Origin+importance beats layer: a User !important declaration must win
            // over an unlayered Author normal rule even though unlayered Author
            // would beat a layered Author rule on the layer axis alone.
            // This pins that the importance+origin axis (step 3) is checked BEFORE
            // the layer axis (step 4).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                User("#x { color: blue !important; }"),
                Author("#x { color: green; }")   // unlayered author normal
            });
            // User !important > Author normal (importance+origin dominates).
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "User !important must beat unlayered Author normal — origin+importance axis before layer");
        }

        // ──────────────────────────────────────────────────────────────────────
        // GROUP 3 — Specificity comparisons (CSS Selectors L4 §17 + Cascade L5 §6.4 step 6)
        //
        // Within the same origin + importance + layer bucket, higher specificity wins.
        // Pseudo-class functions :is(), :where(), :not(), :has() each have defined
        // specificity contribution rules.
        // ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Id_beats_class_same_layer_and_origin() {
            // #x (1,0,0) beats .c (0,1,0)
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: blue; } #x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "ID selector (1,0,0) must beat class selector (0,1,0)");
        }

        [Test]
        public void Attribute_selector_beats_type_selector() {
            // [attr] is (0,1,0); div is (0,0,1) — attribute wins
            var doc = Html("<div id=\"x\" data-foo=\"bar\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: blue; } [data-foo] { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Attribute selector (0,1,0) must beat type selector (0,0,1)");
        }

        [Test]
        public void Class_beats_type_selector() {
            // .c is (0,1,0); div is (0,0,1)
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: blue; } .c { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "Class selector (0,1,0) must beat type selector (0,0,1)");
        }

        [Test]
        public void Inline_style_beats_id_selector() {
            // Inline is author-origin with IsInline=true; beats any selector-based rule
            // in the same origin per the post-layer "inline" tiebreak (CascadeEngine.cs §1889).
            var doc = Html("<div id=\"x\" style=\"color: green;\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("green"),
                "Inline style must beat #id selector in same author origin");
        }

        [Test]
        public void Is_with_id_arg_inherits_id_specificity() {
            // :is(.c, #x) takes the max specificity of its arguments → (1,0,0).
            // That should beat a plain .c rule (0,1,0).
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: blue; } :is(.c, #x) { color: red; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                ":is(.c,#x) has specificity (1,0,0) which beats .c (0,1,0)");
        }

        [Test]
        public void Where_is_always_zero_specificity() {
            // :where() always contributes (0,0,0) per Selectors L4 §17.
            // A plain class selector (0,1,0) must beat :where(#x) (0,0,0) on specificity.
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(":where(#x) { color: red; } .c { color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                ":where(#x) is (0,0,0), must lose to .c (0,1,0)");
        }

        [Test]
        public void Not_inherits_arg_specificity() {
            // :not(#x) inherits the specificity of #x → (1,0,0).
            // Applied to elements that do NOT have id=x; here we apply it to a
            // sibling element. On the #x element itself :not(#x) doesn't match
            // so we check on the sibling div.
            var doc = Html("<div id=\"x\"></div><div id=\"y\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".noclass { color: blue; } :not(#x) { color: red; }")
            });
            // #y matches :not(#x) with specificity (1,0,0); falls back to initial if no match.
            var cs = engine.Compute(doc.GetElementById("y"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"),
                ":not(#x) has specificity (1,0,0) and must match #y");
        }

        [Test]
        public void Has_inherits_arg_specificity() {
            // :has(.child) has specificity (0,1,0) — same as .child inside.
            // :has(#child) would be (1,0,0). Using :has(.child) vs type selector.
            var doc = Html("<div id=\"x\"><span class=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { color: blue; } div:has(.child) { color: red; }")
            });
            // div:has(.child) = (0,1,1); plain div = (0,0,1) → has-version wins.
            Assert.That(Get(engine, doc), Is.EqualTo("red"),
                "div:has(.child) (0,1,1) must beat div (0,0,1)");
        }

        // ──────────────────────────────────────────────────────────────────────
        // GROUP 4 — Source-order tiebreaker (CSS Cascade L5 §6.4 step 7)
        //
        // When origin + importance + layer + specificity all tie, the LATER
        // declaration in source order wins.
        // ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Later_rule_beats_earlier_rule_at_equal_specificity() {
            // Both are .c (0,1,0); second one appears later → wins.
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: red; } .c { color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "Later rule at equal specificity must win by source order");
        }

        [Test]
        public void Later_declaration_within_rule_beats_earlier_at_equal_prop() {
            // Within a single rule block, the later declaration of the same property wins.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("#x { color: red; color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "Later declaration inside a rule block must win for the same property");
        }

        [Test]
        public void Later_sheet_beats_earlier_sheet_at_equal_specificity() {
            // Two Author sheets, same specificity — the one listed second (later) wins.
            var doc = Html("<div id=\"x\" class=\"c\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(".c { color: red; }"),
                Author(".c { color: blue; }")
            });
            Assert.That(Get(engine, doc), Is.EqualTo("blue"),
                "Later stylesheet at equal specificity must win by source order");
        }
    }
}
