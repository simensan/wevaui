using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Properties and Values API Level 1 — `@property` typed custom property tests.
    // Covers parsing, registry round-trip, initial-value, inherits flag, and
    // syntax validation for the <length>, <color>, and <number> types.
    public class AtPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        // ---- Parsing / registry round-trip ----

        [Test]
        public void AtProperty_parsed_and_registered() {
            // A complete, valid @property rule registers one descriptor.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --my-len {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(1));
            Assert.That(engine.PropertyRegistry.TryGet("--my-len", out var desc), Is.True);
            Assert.That(desc.Name, Is.EqualTo("--my-len"));
            Assert.That(desc.Syntax, Is.EqualTo("<length>"));
            Assert.That(desc.InitialValue, Is.EqualTo("0px"));
            Assert.That(desc.Inherits, Is.False);
        }

        [Test]
        public void AtProperty_initial_value_returned_when_property_not_set() {
            // When no rule sets the property the descriptor's initial-value must be used.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 8px;
                        inherits: false;
                    }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--gap"), Is.EqualTo("8px"));
        }

        [Test]
        public void AtProperty_initial_value_returned_for_inheriting_property_at_root() {
            // A registered inheriting property uses initial-value at the root element
            // (no parent to inherit from).
            var doc = Html("<div id=\"root\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --brand {
                        syntax: ""<color>"";
                        initial-value: red;
                        inherits: true;
                    }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("root"));
            Assert.That(cs.Get("--brand"), Is.EqualTo("red"));
        }

        // ---- inherits: true propagates ----

        [Test]
        public void AtProperty_inherits_true_propagates_to_child() {
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --theme {
                        syntax: ""<color>"";
                        initial-value: blue;
                        inherits: true;
                    }
                    #parent { --theme: green; }
                ")
            });
            // Child should inherit parent's --theme value.
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--theme"), Is.EqualTo("green"));
        }

        [Test]
        public void AtProperty_inherits_true_uses_initial_at_root_if_unset() {
            // Inheriting property at root falls back to initial-value since no parent exists.
            var doc = Html("<section id=\"s\"><div id=\"x\"></div></section>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --count {
                        syntax: ""<integer>"";
                        initial-value: 0;
                        inherits: true;
                    }
                ")
            });
            // Neither section nor x sets --count; both should see the initial value.
            var csSection = engine.Compute(doc.GetElementById("s"));
            var csChild = engine.Compute(doc.GetElementById("x"));
            Assert.That(csSection.Get("--count"), Is.EqualTo("0"));
            Assert.That(csChild.Get("--count"), Is.EqualTo("0"));
        }

        // ---- inherits: false does NOT propagate ----

        [Test]
        public void AtProperty_inherits_false_does_not_propagate_to_child() {
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --size: 50px; }
                ")
            });
            // Child must NOT inherit parent's --size; should get initial-value (0px).
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--size"), Is.EqualTo("0px"));
        }

        [Test]
        public void AtProperty_inherits_false_child_can_set_own_value() {
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --size {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --size: 50px; }
                    #child  { --size: 20px; }
                ")
            });
            var csParent = engine.Compute(doc.GetElementById("parent"));
            var csChild = engine.Compute(doc.GetElementById("child"));
            Assert.That(csParent.Get("--size"), Is.EqualTo("50px"));
            Assert.That(csChild.Get("--size"), Is.EqualTo("20px"));
        }

        // ---- Missing descriptors cause discard ----

        [Test]
        public void AtProperty_missing_syntax_is_discarded() {
            // Without `syntax`, the at-rule must be silently discarded.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --x {
                        initial-value: 0px;
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void AtProperty_missing_initial_value_is_discarded() {
            // Without `initial-value`, the at-rule must be silently discarded.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --x {
                        syntax: ""<length>"";
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void AtProperty_missing_inherits_is_discarded() {
            // Without `inherits`, the at-rule must be silently discarded.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --x {
                        syntax: ""<length>"";
                        initial-value: 0px;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0));
        }

        [Test]
        public void AtProperty_discarded_rule_custom_property_behaves_as_untyped() {
            // When the @property rule is discarded the property behaves like an
            // ordinary (unregistered) custom property: it inherits by default.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --x {
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --x: hello; }
                ")
            });
            // --x cascades as untyped, so it inherits to the child.
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("--x"), Is.EqualTo("hello"));
        }

        // ---- Invalid initial-value for declared syntax causes discard ----

        [Test]
        public void AtProperty_invalid_initial_value_for_syntax_is_discarded() {
            // `initial-value: red` is not a valid <length>, so the rule must be discarded.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --bad {
                        syntax: ""<length>"";
                        initial-value: red;
                        inherits: false;
                    }
                ")
            });
            Assert.That(engine.PropertyRegistry.Count, Is.EqualTo(0));
        }

        // ---- Authored value matching / not matching syntax ----

        [Test]
        public void AtProperty_authored_value_matching_syntax_is_applied() {
            // 10px matches <length> — the value is used as-is.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #x { --gap: 10px; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--gap"), Is.EqualTo("10px"));
        }

        [Test]
        public void AtProperty_authored_value_not_matching_syntax_falls_back_to_initial() {
            // "hello" is not a <length>; the cascade must substitute the initial-value.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #x { --gap: hello; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--gap"), Is.EqualTo("0px"));
        }

        // ---- syntax: "*" (universal) ----

        [Test]
        public void AtProperty_universal_syntax_accepts_any_value() {
            // syntax: "*" means any token sequence is valid.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --raw {
                        syntax: ""*"";
                        initial-value: none;
                        inherits: false;
                    }
                    #x { --raw: 42 hello world; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--raw"), Is.EqualTo("42 hello world"));
        }

        [Test]
        public void AtProperty_universal_syntax_registered() {
            // syntax: "*" with a non-empty initial-value is valid per spec.
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --raw {
                        syntax: ""*"";
                        initial-value: 0;
                        inherits: true;
                    }
                ")
            });
            // Rule is registered — the descriptor itself is valid.
            Assert.That(engine.PropertyRegistry.TryGet("--raw", out var d), Is.True);
            Assert.That(d.Syntax, Is.EqualTo("*"));
            Assert.That(d.Inherits, Is.True);
        }

        // ---- <color> and <number> syntax ----

        [Test]
        public void AtProperty_color_syntax_accepts_valid_color() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --accent {
                        syntax: ""<color>"";
                        initial-value: #ff0000;
                        inherits: true;
                    }
                    #x { --accent: #00ff00; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--accent"), Is.EqualTo("#00ff00"));
        }

        [Test]
        public void AtProperty_color_syntax_rejects_non_color_falls_back_to_initial() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --accent {
                        syntax: ""<color>"";
                        initial-value: #ff0000;
                        inherits: true;
                    }
                    #x { --accent: 42px; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--accent"), Is.EqualTo("#ff0000"));
        }

        [Test]
        public void AtProperty_number_syntax_accepts_valid_number() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --ratio {
                        syntax: ""<number>"";
                        initial-value: 1;
                        inherits: false;
                    }
                    #x { --ratio: 0.75; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--ratio"), Is.EqualTo("0.75"));
        }

        [Test]
        public void AtProperty_number_syntax_rejects_length_falls_back_to_initial() {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --ratio {
                        syntax: ""<number>"";
                        initial-value: 1;
                        inherits: false;
                    }
                    #x { --ratio: 10px; }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("--ratio"), Is.EqualTo("1"));
        }

        // ---- Round-trip: inheritance flag is honoured through var() ----

        [Test]
        public void AtProperty_inherits_false_var_resolves_to_initial_on_child() {
            // Non-inheriting property; child's var(--gap) should resolve to the
            // initial-value (not the parent's authored value).
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --gap {
                        syntax: ""<length>"";
                        initial-value: 0px;
                        inherits: false;
                    }
                    #parent { --gap: 24px; }
                    #child  { width: var(--gap); }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            // --gap is non-inheriting; child must resolve to the initial 0px.
            Assert.That(cs.Get("width"), Is.EqualTo("0px"));
        }

        [Test]
        public void AtProperty_inherits_true_var_resolves_to_parent_value_on_child() {
            // Inheriting property; child's var(--color) should see parent's value.
            var doc = Html("<div id=\"parent\"><div id=\"child\"></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(@"
                    @property --brand {
                        syntax: ""<color>"";
                        initial-value: black;
                        inherits: true;
                    }
                    #parent { --brand: navy; }
                    #child  { color: var(--brand); }
                ")
            });
            var cs = engine.Compute(doc.GetElementById("child"));
            Assert.That(cs.Get("color"), Is.EqualTo("navy"));
        }
    }
}
