using NUnit.Framework;
using Weva.Css;

namespace Weva.Tests.Css.Parsing {
    public class NestingTests {
        static StyleRule[] StyleRules(Stylesheet sheet) {
            var list = new System.Collections.Generic.List<StyleRule>();
            CollectStyleRules(sheet.Rules, list);
            return list.ToArray();
        }

        static void CollectStyleRules(System.Collections.Generic.List<Rule> rules, System.Collections.Generic.List<StyleRule> output) {
            foreach (var r in rules) {
                if (r is StyleRule sr) output.Add(sr);
                else if (r is MediaRule mr) CollectStyleRules(mr.Rules, output);
                else if (r is ContainerRule cr) CollectStyleRules(cr.Rules, output);
            }
        }

        [Test]
        public void Ampersand_with_pseudo_expands_to_two_rules() {
            var sheet = CssParser.Parse(@".btn { color: red; &:hover { color: blue; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(2));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".btn"));
            Assert.That(rules[0].Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(rules[0].Declarations[0].ValueText, Is.EqualTo("red"));
            Assert.That(rules[1].Selectors[0], Is.EqualTo(".btn:hover"));
            Assert.That(rules[1].Declarations[0].ValueText, Is.EqualTo("blue"));
        }

        [Test]
        public void Bare_descendant_nests_as_descendant_combinator() {
            var sheet = CssParser.Parse(@".outer { .inner { color: red; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".outer .inner"));
            Assert.That(rules[0].Declarations[0].ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Multiple_parent_selectors_expand_per_parent() {
            var sheet = CssParser.Parse(@"a, b { &:hover { color: red; } }");
            var rules = StyleRules(sheet);
            // Parent has no declarations, so only the nested rule is emitted —
            // expanded once per parent.
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors, Has.Count.EqualTo(2));
            Assert.That(rules[0].Selectors, Does.Contain("a:hover"));
            Assert.That(rules[0].Selectors, Does.Contain("b:hover"));
        }

        [Test]
        public void Self_combinator_expands_with_each_parent() {
            var sheet = CssParser.Parse(@".btn { & + & { margin-left: 4px; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".btn + .btn"));
        }

        [Test]
        public void Triple_nested_expands_correctly() {
            var sheet = CssParser.Parse(@"
                .a { color: red;
                    .b {
                        .c { color: blue; }
                    }
                }
            ");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(2));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".a"));
            Assert.That(rules[1].Selectors[0], Is.EqualTo(".a .b .c"));
        }

        [Test]
        public void Child_combinator_expands() {
            var sheet = CssParser.Parse(@".outer { & > .child { color: red; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(1));
            // Replacing & with ".outer" yields ".outer > .child".
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".outer > .child"));
        }

        [Test]
        public void Bare_selector_starting_with_combinator_handled() {
            // When child starts with a combinator, the parent must be prefixed.
            // This case is covered by the bare-no-`&` rule (the descendant
            // prefix). For `> .child`, treating "> .child" as "no &" → prefix
            // with parent + space → "parent  > .child" (extra space, still valid).
            var sheet = CssParser.Parse(@".outer { > .child { color: red; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors[0].Contains(".child"), Is.True);
            Assert.That(rules[0].Selectors[0].Contains(">"), Is.True);
        }

        [Test]
        public void Nested_media_wraps_inner_rules() {
            var sheet = CssParser.Parse(@"
                .card {
                    @media (max-width: 600px) {
                        & { color: red; }
                    }
                }
            ");
            // Top-level rules should include a MediaRule wrapping a StyleRule.
            Assert.That(sheet.Rules, Has.Count.EqualTo(1));
            Assert.That(sheet.Rules[0], Is.InstanceOf<MediaRule>());
            var mr = (MediaRule)sheet.Rules[0];
            Assert.That(mr.Rules, Has.Count.EqualTo(1));
            var inner = (StyleRule)mr.Rules[0];
            Assert.That(inner.Selectors[0], Is.EqualTo(".card"));
        }

        [Test]
        public void Top_level_bare_nested_selector_is_descendant() {
            // CSS Nesting allows bare selectors at top-level inside a parent;
            // the parent's selector forms the descendant prefix.
            var sheet = CssParser.Parse(@".x { p { color: red; } }");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".x p"));
        }

        [Test]
        public void Empty_parent_only_nested_emits_just_nested() {
            var sheet = CssParser.Parse(@".x { &:hover { color: red; } }");
            var rules = StyleRules(sheet);
            // Parent has zero declarations, so only the nested rule is emitted.
            Assert.That(rules, Has.Length.EqualTo(1));
            Assert.That(rules[0].Selectors[0], Is.EqualTo(".x:hover"));
        }

        [Test]
        public void Nesting_works_with_compound_at_top() {
            var sheet = CssParser.Parse(@"
                .btn { padding: 4px;
                    &.primary { color: red; }
                    &.secondary { color: blue; }
                }
            ");
            var rules = StyleRules(sheet);
            Assert.That(rules, Has.Length.EqualTo(3));
            Assert.That(rules[1].Selectors[0], Is.EqualTo(".btn.primary"));
            Assert.That(rules[2].Selectors[0], Is.EqualTo(".btn.secondary"));
        }
    }
}
