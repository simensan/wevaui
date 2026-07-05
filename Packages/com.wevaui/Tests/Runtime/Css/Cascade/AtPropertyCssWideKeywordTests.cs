using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Properties and Values API L1 §2.4 + CSS Cascade L5 §7 — @property
    // custom property interaction with the five CSS-wide keywords.
    //
    // Spec invariants being tested (§2.4):
    //   initial  — forces the registered initial-value, ignoring any cascade result.
    //   inherit  — forces the parent's computed value (even for inherits: false props).
    //   unset    — behaves as `inherit` if inherits: true, else `initial`.
    //   revert   — rolls back to UA/user origin; in engine v1 that collapses to
    //              `initial` when only an author-origin rule exists (same outcome
    //              as `initial` for author-only stacks).
    //
    // These tests complement AtPropertyTests.cs which only tests authored values
    // without CSS-wide keywords.
    public class AtPropertyCssWideKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ---- initial on registered property ----

        [Test]
        public void Initial_keyword_restores_registered_initial_value() {
            // CSS Properties and Values L1 §2.4: `initial` on a registered custom
            // property yields the descriptor's initial-value, overriding any
            // authored value earlier in the cascade.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 8px;
                        inherits: false;
                    }
                    #x { --gap: 40px; --gap: initial; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--gap"), Is.EqualTo("8px"),
                "initial keyword must restore the descriptor's initial-value");
        }

        [Test]
        public void Initial_on_inheriting_registered_property_yields_initial_not_parent() {
            // Even when the parent has an authored value, `initial` on the child
            // must produce the descriptor initial-value, NOT the parent's value.
            // CSS Cascade L5 §7.1.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --brand {
                        syntax: ""<color>"";
                        initial-value: red;
                        inherits: true;
                    }
                    #parent { --brand: navy; }
                    #child  { --brand: initial; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--brand"), Is.EqualTo("red"),
                "initial must override inherited value even for inherits:true properties");
        }

        // ---- inherit on registered property with inherits: false ----

        [Test]
        public void Inherit_keyword_forces_parent_value_on_noninheriting_property() {
            // CSS Cascade L5 §7.2: `inherit` forces inheritance regardless of
            // the property's own `inherits` descriptor. Even an inherits:false
            // registered property yields the parent's value when `inherit` is set.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --size: 50px; }
                    #child  { --size: inherit; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--size"), Is.EqualTo("50px"),
                "inherit keyword must pull the parent value even for inherits:false");
        }

        [Test]
        public void Inherit_at_root_with_no_parent_yields_initial_value() {
            // CSS Cascade L5 §7.2: `inherit` at the root element (no parent) falls
            // back to the property's initial value since there is no parent to
            // inherit from.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 12px;
                        inherits: false;
                    }
                    #x { --size: inherit; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // Root has no parent; inherit resolves to initial-value (12px).
            Assert.That(cs.Get("--size"), Is.EqualTo("12px"),
                "inherit with no parent must yield the registered initial-value");
        }

        // ---- unset on registered property ----

        [Test]
        public void Unset_on_inherits_true_property_acts_as_inherit() {
            // CSS Cascade L5 §7.3: `unset` behaves as `inherit` for inheriting
            // properties. An inherits:true registered property with `unset` on
            // the child must see the parent's value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --theme {
                        syntax: ""<color>"";
                        initial-value: black;
                        inherits: true;
                    }
                    #parent { --theme: green; }
                    #child  { --theme: unset; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--theme"), Is.EqualTo("green"),
                "unset on inherits:true must act as inherit, yielding parent value");
        }

        [Test]
        public void Unset_on_inherits_false_property_acts_as_initial() {
            // CSS Cascade L5 §7.3: `unset` behaves as `initial` for non-inheriting
            // properties. An inherits:false registered property with `unset` on
            // the child must see the descriptor's initial-value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 4px;
                        inherits: false;
                    }
                    #parent { --size: 64px; }
                    #child  { --size: unset; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--size"), Is.EqualTo("4px"),
                "unset on inherits:false must act as initial, yielding descriptor initial-value");
        }

        // ATPROP-1 regression-anchor test was removed when the bug was fixed
        // in CascadeEngine.ComputeCustomProperties (intercepts `unset` for
        // inherits:false registered properties and resolves to initial-value
        // directly, since KeywordResolver doesn't know about the registry).

        // ---- revert on registered property ----

        [Test]
        public void Revert_on_registered_property_with_author_only_yields_initial() {
            // CSS Cascade L5 §7.4: `revert` discards the current origin's value and
            // rolls back to the lower origin. With only author-origin in play, the
            // result is the registered initial-value (no UA/user value to fall back to).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 8px;
                        inherits: false;
                    }
                    #x { --gap: 100px; --gap: revert; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // With no UA/user origin, revert collapses to initial (8px).
            Assert.That(cs.Get("--gap"), Is.EqualTo("8px"),
                "revert with no UA/user origin must yield the registered initial-value");
        }

        // ---- var() resolves through CSS-wide-keyword result ----

        [Test]
        public void Var_resolves_through_initial_on_registered_property() {
            // When a registered property is set to `initial`, downstream var()
            // references must see the descriptor's initial-value, not any
            // previously authored value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 8px;
                        inherits: true;
                    }
                    #parent { --gap: 40px; --gap: initial; }
                    #child  { padding: var(--gap); }
                ")
            });
            // parent resets --gap to its initial (8px), which child inherits.
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("padding"), Is.EqualTo("8px"),
                "var() on child must resolve through parent's initial (8px)");
        }

        [Test]
        public void Var_resolves_through_inherit_keyword_on_noninheriting_property() {
            // A non-inheriting property with `inherit` on the child should make
            // var() on the child see the parent's authored value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --size: 24px; }
                    #child  { --size: inherit; padding: var(--size); }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("padding"), Is.EqualTo("24px"),
                "var() must see inherited parent value when child uses `inherit` keyword");
        }

        // ---- initial-value used through var() when property unset at all levels ----

        [Test]
        public void Var_falls_back_to_registered_initial_when_property_never_set() {
            // If neither parent nor child has an authored value, var() must
            // resolve to the descriptor's initial-value (for inherits:false).
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --accent {
                        syntax: ""<color>"";
                        initial-value: lime;
                        inherits: false;
                    }
                    #child { color: var(--accent); }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("color"), Is.EqualTo("lime"),
                "var() must resolve to registered initial when no value set anywhere");
        }
    }
}
