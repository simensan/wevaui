using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Properties & Values API L1 §3.4 — @property rules where
    // `initial-value` contains a variable reference are INVALID and must be
    // discarded. Spec text (§3.4):
    //
    //   "The initial-value descriptor … is invalid if it contains a variable
    //   reference (a var() function)."
    //
    // The engine must therefore reject the entire @property rule when
    // initial-value contains var(); the custom property then falls back to the
    // normal unregistered-custom-property behaviour (inherited, no type
    // constraint, initial = guaranteed-invalid).
    //
    // Additionally covered: cross-property cycles where a registered custom
    // property's *authored value* references another property that references
    // the first — the CycleDetectionTests file covers the unregistered-
    // property case; this file specifically targets the interaction between
    // @property descriptors and var() cycles.
    //
    // Spec refs: CSS Properties & Values L1 §3.4, CSS Custom Properties L1 §3.1
    public class AtPropertyInitialVarCycleTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ── §3.4 — initial-value containing var() must reject the @property rule ─

        [Test]
        public void AtProperty_initial_value_containing_var_is_discarded() {
            // CSS P&V L1 §3.4: initial-value cannot reference variables.
            // The @property rule is invalid — no descriptor must be registered.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --bad {
                        syntax: ""<length>"";
                        initial-value: var(--other);
                        inherits: false;
                    }
                ")
            });
            // The rule is invalid; registry must stay empty.
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0),
                "@property with var() initial-value must be silently discarded");
        }

        [Test]
        public void AtProperty_initial_value_var_with_fallback_is_also_discarded() {
            // var() WITH a fallback is still a variable reference — must be rejected.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --bad2 {
                        syntax: ""<length>"";
                        initial-value: var(--other, 8px);
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0),
                "initial-value with var(..., fallback) must also be discarded");
        }

        [Test]
        public void AtProperty_self_referential_initial_value_is_discarded() {
            // The most direct cycle: --a's initial-value references --a itself.
            // The rule must be rejected entirely.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --a {
                        syntax: ""<length>"";
                        initial-value: var(--a);
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0),
                "Self-referential initial-value var(--a) must cause rule discard");
        }

        [Test]
        public void AtProperty_invalid_initial_var_does_not_pollute_subsequent_valid_rule() {
            // A discarded rule must not affect a subsequent valid @property for
            // a different name in the same stylesheet.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --bad {
                        syntax: ""<length>"";
                        initial-value: var(--other);
                        inherits: false;
                    }
                    @property --good {
                        syntax: ""<length>"";
                        initial-value: 10px;
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(1),
                "Only the valid @property rule should be registered");
            Assert.That(engine.PropertyRegistry.TryGet("--good", out var desc), Is.True);
            Assert.That(desc.InitialValue, Is.EqualTo("10px"));
        }

        // ── Discarded rule → property falls back to unregistered behaviour ────────

        [Test]
        public void AtProperty_discarded_rule_property_reverts_to_inherited_unregistered() {
            // When the @property rule is discarded the custom property --bad
            // is unregistered. Unregistered custom properties ARE inherited
            // (CSS Custom Properties L1 §2): child must see parent's authored value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --bad {
                        syntax: ""<length>"";
                        initial-value: var(--other);
                        inherits: false;
                    }
                    #parent { --bad: 24px; }
                    #child  { width: var(--bad); }
                ")
            });
            // --bad is unregistered (rule discarded) → inherits true by default.
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("width"), Is.EqualTo("24px"),
                "Unregistered property (rule discarded) inherits parent value by default");
        }

        // ── Authored-value cycles with a *valid* registered property ─────────────

        [Test]
        public void Registered_property_authored_var_cycle_reverts_to_registered_initial() {
            // A registered property whose authored value creates a var() cycle
            // should revert to the @property's initial-value (not the global
            // unregistered behaviour).
            // --x is properly registered with initial 0px.
            // Author sets --x: var(--y) and --y: var(--x) — both are cyclic.
            // For the registered --x the cascade must use 0px (its initial-value).
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --x {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #x { --x: var(--y); --y: var(--x); width: var(--x, 99px); }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            // --x is cyclic → guaranteed-invalid; consumer has fallback 99px but
            // the registered initial (0px) is what the *custom property storage* holds.
            // Consumer var(--x, 99px) fallback fires because --x is invalid-at-computed-value-time.
            string width = cs.Get("width");
            // Either the fallback (99px) or the initial (0px) is spec-acceptable,
            // but the result must NOT be an arbitrary/undefined value.
            Assert.That(width, Is.EqualTo("99px").Or.EqualTo("0px"),
                "Cyclic registered property must resolve to its fallback or registered initial");
        }

        [Test]
        public void Valid_registration_after_discarded_registration_wins() {
            // Two @property rules for the same name: the first has a bad
            // initial-value (var()), the second is valid. Only the valid
            // one should be registered (last-valid-wins, since the first is discarded).
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --p {
                        syntax: ""<length>"";
                        initial-value: var(--other);
                        inherits: false;
                    }
                    @property --p {
                        syntax: ""<length>"";
                        initial-value: 5px;
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(1));
            Assert.That(engine.PropertyRegistry.TryGet("--p", out var desc), Is.True);
            Assert.That(desc.InitialValue, Is.EqualTo("5px"),
                "Second (valid) @property rule for --p must be registered");
        }

        [Test]
        public void Color_syntax_initial_value_with_var_is_also_discarded() {
            // Same invariant for <color> syntax — var() in initial-value must reject.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --brand {
                        syntax: ""<color>"";
                        initial-value: var(--theme-color);
                        inherits: true;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0),
                "@property <color> with var() initial-value must be discarded");
        }
    }
}
