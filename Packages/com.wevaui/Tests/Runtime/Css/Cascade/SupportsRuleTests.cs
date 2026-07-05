using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Conditional 3 @supports. Parser creates a SupportsRule and the
    // cascade engine gates nested rules through SupportsEvaluator.
    public class SupportsRuleTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        [Test]
        public void Supports_rule_with_known_property_applies_inner_rules() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (display: flex) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_rule_with_unknown_property_does_not_apply() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (foo: bar) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Supports_not_clause_negates_known_property() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports not (display: flex) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Supports_not_clause_negates_unknown_property() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports not (foo: bar) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_and_clause_parses_and_emits_supports_rule() {
            var s = CssParser.Parse(
                ".fallback { color: green; }" +
                "@supports (display: flex) and (color: red) { .x { color: blue; } }" +
                ".tail { color: orange; }");
            Assert.That(s.Rules, Has.Count.EqualTo(3));
            Assert.That(s.Rules[0], Is.InstanceOf<StyleRule>());
            Assert.That(s.Rules[1], Is.InstanceOf<SupportsRule>());
            Assert.That(s.Rules[2], Is.InstanceOf<StyleRule>());
            var sup = (SupportsRule)s.Rules[1];
            Assert.That(sup.ConditionText, Does.Contain("display: flex"));
            Assert.That(sup.Rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void Supports_or_clause_parses_and_emits_supports_rule() {
            var s = CssParser.Parse(
                ".fallback { color: green; }" +
                "@supports (display: flex) or (display: grid) { .x { color: blue; } }" +
                ".tail { color: orange; }");
            Assert.That(s.Rules, Has.Count.EqualTo(3));
            Assert.That(s.Rules[1], Is.InstanceOf<SupportsRule>());
        }

        [Test]
        public void Supports_and_clause_requires_both_sides() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (display: flex) and (foo: bar) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Supports_or_clause_accepts_either_side() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (foo: bar) or (display: grid) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_nested_boolean_group_evaluates() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports ((foo: bar) or (display: grid)) and (color: red) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_selector_function_uses_selector_parser() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports selector(section:has(> .selected)) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_clip_path_basic_shape_applies_inner_rules() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (clip-path: inset(4px)) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_accepts_path_clip_path_shape() {
            // path() basic-shape support landed in B16 phase 1 (CPU/software path).
            // @supports must now report it as supported so authors can use it safely.
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (clip-path: path('M0 0 H100 V100 Z')) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_accepts_mask_and_backdrop_filter_values() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("@supports (mask-image: linear-gradient(90deg, transparent, black)) and (backdrop-filter: blur(4px)) { .x { color: red; } }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Supports_inside_media_nesting_applies_inner_rules() {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(
                new[] {
                    Author("@media (min-width: 1px) { @supports (display: flex) { .x { color: red; } } }")
                },
                MediaContext.Default(800, 600));
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }
    }
}
