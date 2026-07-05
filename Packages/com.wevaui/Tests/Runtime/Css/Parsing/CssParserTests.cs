using NUnit.Framework;
using Weva.Css;
using Weva.Parsing;

namespace Weva.Tests.Css.Parsing {
    public class CssParserTests {
        [Test]
        public void Empty_stylesheet() {
            var s = CssParser.Parse("");
            Assert.That(s.Rules, Has.Count.EqualTo(0));
        }

        [Test]
        public void Whitespace_only_stylesheet() {
            var s = CssParser.Parse("   \n\t ");
            Assert.That(s.Rules, Has.Count.EqualTo(0));
        }

        [Test]
        public void Single_rule_with_one_declaration() {
            var s = CssParser.Parse("a { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors, Has.Count.EqualTo(1));
            Assert.That(r.Selectors[0], Is.EqualTo("a"));
            Assert.That(r.Declarations, Has.Count.EqualTo(1));
            Assert.That(r.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(r.Declarations[0].ValueText, Is.EqualTo("red"));
            Assert.That(r.Declarations[0].Important, Is.False);
        }

        [Test]
        public void Multiple_declarations() {
            var s = CssParser.Parse("a { color: red; background: blue; padding: 10px; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations, Has.Count.EqualTo(3));
            Assert.That(r.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(r.Declarations[1].Property, Is.EqualTo("background"));
            Assert.That(r.Declarations[2].Property, Is.EqualTo("padding"));
            Assert.That(r.Declarations[2].ValueText, Is.EqualTo("10px"));
        }

        [Test]
        public void Trailing_semicolon_optional() {
            var s = CssParser.Parse("a { color: red; background: blue }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations, Has.Count.EqualTo(2));
            Assert.That(r.Declarations[1].ValueText, Is.EqualTo("blue"));
        }

        [Test]
        public void Important_attached_to_value() {
            var s = CssParser.Parse("a { color: red !important; }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.Important, Is.True);
            Assert.That(d.ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Important_with_space_around_bang() {
            var s = CssParser.Parse("a { color: red ! important; }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.Important, Is.True);
            Assert.That(d.ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Important_uppercase() {
            var s = CssParser.Parse("a { color: red !IMPORTANT; }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.Important, Is.True);
            Assert.That(d.ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Multiple_selectors_split_on_commas() {
            var s = CssParser.Parse("a, b, c { color: red; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors, Has.Count.EqualTo(3));
            Assert.That(r.Selectors[0], Is.EqualTo("a"));
            Assert.That(r.Selectors[1], Is.EqualTo("b"));
            Assert.That(r.Selectors[2], Is.EqualTo("c"));
        }

        [Test]
        public void Compound_selectors_preserved() {
            var s = CssParser.Parse(".foo > .bar:hover { color: red; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors[0], Is.EqualTo(".foo > .bar:hover"));
        }

        [Test]
        public void Selector_with_attribute_brackets_preserved() {
            var s = CssParser.Parse("input[type=\"text\"] { color: red; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors, Has.Count.EqualTo(1));
            Assert.That(r.Selectors[0], Does.Contain("input"));
            Assert.That(r.Selectors[0], Does.Contain("type"));
        }

        [Test]
        public void Media_rule_wraps_inner_rules() {
            var s = CssParser.Parse("@media (min-width: 600px) { .x { color: red } .y { color: blue } }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var m = (MediaRule)s.Rules[0];
            Assert.That(m.ConditionText, Is.EqualTo("(min-width: 600px)"));
            Assert.That(m.Rules, Has.Count.EqualTo(2));
            var inner = (StyleRule)m.Rules[0];
            Assert.That(inner.Selectors[0], Is.EqualTo(".x"));
            Assert.That(inner.Declarations[0].ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Keyframes_with_from_to_and_percentage_selectors() {
            var s = CssParser.Parse("@keyframes spin { from { rotate: 0 } 50% { rotate: 180deg } to { rotate: 360deg } }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var k = (KeyframesRule)s.Rules[0];
            Assert.That(k.Name, Is.EqualTo("spin"));
            Assert.That(k.Blocks, Has.Count.EqualTo(3));
            Assert.That(k.Blocks[0].Selector, Is.EqualTo("from"));
            Assert.That(k.Blocks[1].Selector, Is.EqualTo("50%"));
            Assert.That(k.Blocks[2].Selector, Is.EqualTo("to"));
            Assert.That(k.Blocks[2].Declarations[0].ValueText, Is.EqualTo("360deg"));
        }

        [Test]
        public void Keyframe_block_with_comma_selector() {
            var s = CssParser.Parse("@keyframes pulse { 50%, 75% { opacity: 1 } }");
            var k = (KeyframesRule)s.Rules[0];
            Assert.That(k.Blocks, Has.Count.EqualTo(1));
            Assert.That(k.Blocks[0].Selector, Is.EqualTo("50%, 75%"));
        }

        [Test]
        public void Import_rule_simple() {
            var s = CssParser.Parse("@import \"foo.css\";");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.Href, Is.EqualTo("foo.css"));
            Assert.That(imp.MediaConditionText, Is.EqualTo(""));
        }

        [Test]
        public void Import_rule_with_media() {
            var s = CssParser.Parse("@import \"foo.css\" screen;");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.Href, Is.EqualTo("foo.css"));
            Assert.That(imp.MediaConditionText, Is.EqualTo("screen"));
        }

        [Test]
        public void Import_rule_with_named_layer_parses_and_captures_name() {
            var s = CssParser.Parse("@import url(foo.css) layer(utilities);\na { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(2));
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.Href, Is.EqualTo("foo.css"));
            Assert.That(imp.HasLayer, Is.True);
            Assert.That(imp.Layer, Is.EqualTo("utilities"));
            Assert.That(imp.MediaConditionText, Is.EqualTo(""));
            var follow = (StyleRule)s.Rules[1];
            Assert.That(follow.Selectors[0], Is.EqualTo("a"));
        }

        [Test]
        public void Import_rule_with_anonymous_layer_parses() {
            var s = CssParser.Parse("@import url(foo.css) layer;");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.HasLayer, Is.True);
            Assert.That(imp.Layer, Is.Null);
        }

        [Test]
        public void Import_rule_layer_then_media_keeps_both() {
            var s = CssParser.Parse("@import url(foo.css) layer(base) screen;");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.HasLayer, Is.True);
            Assert.That(imp.Layer, Is.EqualTo("base"));
            Assert.That(imp.MediaConditionText, Is.EqualTo("screen"));
        }

        [Test]
        public void Import_rule_unterminated_layer_throws() {
            Assert.Throws<CssParseException>(() => CssParser.Parse("@import url(foo.css) layer("));
        }

        [Test]
        public void Import_rule_with_supports_parses_and_captures_text() {
            var s = CssParser.Parse("@import url(foo.css) supports(display: grid);\na { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(2));
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.Href, Is.EqualTo("foo.css"));
            Assert.That(imp.HasSupports, Is.True);
            Assert.That(imp.SupportsText, Is.EqualTo("display: grid"));
            Assert.That(imp.HasLayer, Is.False);
            Assert.That(imp.MediaConditionText, Is.EqualTo(""));
            var follow = (StyleRule)s.Rules[1];
            Assert.That(follow.Selectors[0], Is.EqualTo("a"));
        }

        [Test]
        public void Import_rule_layer_then_supports_keeps_both() {
            var s = CssParser.Parse("@import url(foo.css) layer(base) supports(display: grid);");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.HasLayer, Is.True);
            Assert.That(imp.Layer, Is.EqualTo("base"));
            Assert.That(imp.HasSupports, Is.True);
            Assert.That(imp.SupportsText, Is.EqualTo("display: grid"));
            Assert.That(imp.MediaConditionText, Is.EqualTo(""));
        }

        [Test]
        public void Import_rule_supports_then_media_keeps_both() {
            var s = CssParser.Parse("@import url(foo.css) supports(display: grid) screen and (min-width: 400px);");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.HasSupports, Is.True);
            Assert.That(imp.SupportsText, Is.EqualTo("display: grid"));
            Assert.That(imp.MediaConditionText, Is.EqualTo("screen and (min-width: 400px)"));
        }

        [Test]
        public void Import_rule_supports_with_nested_parens_balances() {
            var s = CssParser.Parse("@import url(foo.css) supports(not (display: grid));");
            var imp = (ImportRule)s.Rules[0];
            Assert.That(imp.HasSupports, Is.True);
            Assert.That(imp.SupportsText, Is.EqualTo("not (display: grid)"));
        }

        [Test]
        public void Import_rule_unterminated_supports_throws() {
            Assert.Throws<CssParseException>(() => CssParser.Parse("@import url(foo.css) supports("));
        }

        [Test]
        public void Var_function_preserved_in_value() {
            var s = CssParser.Parse("a { color: var(--accent); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("var(--accent)"));
        }

        [Test]
        public void Var_with_fallback_preserved() {
            var s = CssParser.Parse("a { color: var(--accent, blue); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("var(--accent, blue)"));
        }

        [Test]
        public void Calc_preserved_in_value() {
            var s = CssParser.Parse("a { width: calc(100% - 20px); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("calc(100% - 20px)"));
        }

        [Test]
        public void Linear_gradient_inner_comma_does_not_split_value() {
            var s = CssParser.Parse("a { background: linear-gradient(red, blue); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("linear-gradient(red, blue)"));
        }

        [Test]
        public void Comments_between_declarations_are_stripped() {
            var s = CssParser.Parse("a { color: red; /* comment */ background: blue; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations, Has.Count.EqualTo(2));
        }

        [Test]
        public void Comments_inside_selector_list_are_stripped() {
            var s = CssParser.Parse("a /* x */, b { color: red; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors, Has.Count.EqualTo(2));
            Assert.That(r.Selectors[0], Is.EqualTo("a"));
            Assert.That(r.Selectors[1], Is.EqualTo("b"));
        }

        [Test]
        public void Property_name_lowercased_value_case_preserved() {
            var s = CssParser.Parse("a { COLOR: Red; }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.Property, Is.EqualTo("color"));
            Assert.That(d.ValueText, Is.EqualTo("Red"));
        }

        [Test]
        public void Custom_property_preserved_exactly() {
            var s = CssParser.Parse("a { --my-Color: #FFAABB; }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.Property, Is.EqualTo("--my-color"));
            Assert.That(d.ValueText, Is.EqualTo("#FFAABB"));
        }

        [Test]
        public void Empty_rule_yields_zero_declarations() {
            var s = CssParser.Parse("a { }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations, Has.Count.EqualTo(0));
        }

        [Test]
        public void Strict_throws_on_missing_colon() {
            Assert.Throws<CssParseException>(() => CssParser.Parse("a { color red; }"));
        }

        [Test]
        public void Strict_throws_on_unterminated_string() {
            Assert.Throws<CssParseException>(() => CssParser.Parse("a { content: \"open ; }"));
        }

        [Test]
        public void Strict_throws_on_unmatched_brace() {
            Assert.Throws<CssParseException>(() => CssParser.Parse("a { color: red; "));
        }

        [Test]
        public void Lenient_skips_malformed_declaration_and_continues() {
            var opts = new ParseOptions { ThrowOnError = false };
            var s = CssParser.Parse("a { broken; color: red; }", opts);
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations, Has.Count.EqualTo(1));
            Assert.That(r.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(r.Declarations[0].ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Unknown_at_rule_skipped_no_throw_in_strict_mode() {
            var s = CssParser.Parse("@unknown thing; a { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors[0], Is.EqualTo("a"));
        }

        [Test]
        public void Unknown_at_rule_with_block_skipped() {
            var s = CssParser.Parse("@unknown { whatever } a { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors[0], Is.EqualTo("a"));
        }

        [Test]
        public void Hash_color_preserved_in_value() {
            var s = CssParser.Parse("a { color: #abc; background: #112233; }");
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Declarations[0].ValueText, Is.EqualTo("#abc"));
            Assert.That(r.Declarations[1].ValueText, Is.EqualTo("#112233"));
        }

        [Test]
        public void Url_preserved_in_value() {
            var s = CssParser.Parse("a { background-image: url(image.png); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("url(image.png)"));
        }

        [Test]
        public void Realworld_button_with_hover_and_media() {
            var css = @"
                .button {
                    display: inline-block;
                    padding: 8px 16px;
                    background: #4f46e5;
                    color: white;
                    border-radius: 4px;
                }
                .button:hover {
                    background: #6366f1;
                }
                @media (min-width: 768px) {
                    .button { padding: 12px 24px; }
                }";
            var s = CssParser.Parse(css);
            Assert.That(s.Rules, Has.Count.EqualTo(3));

            var r1 = (StyleRule)s.Rules[0];
            Assert.That(r1.Selectors[0], Is.EqualTo(".button"));
            Assert.That(r1.Declarations, Has.Count.EqualTo(5));
            Assert.That(r1.Declarations[1].Property, Is.EqualTo("padding"));
            Assert.That(r1.Declarations[1].ValueText, Is.EqualTo("8px 16px"));
            Assert.That(r1.Declarations[2].ValueText, Is.EqualTo("#4f46e5"));

            var r2 = (StyleRule)s.Rules[1];
            Assert.That(r2.Selectors[0], Is.EqualTo(".button:hover"));

            var m = (MediaRule)s.Rules[2];
            Assert.That(m.ConditionText, Is.EqualTo("(min-width: 768px)"));
            Assert.That(m.Rules, Has.Count.EqualTo(1));
            var inner = (StyleRule)m.Rules[0];
            Assert.That(inner.Selectors[0], Is.EqualTo(".button"));
            Assert.That(inner.Declarations[0].ValueText, Is.EqualTo("12px 24px"));
        }

        [Test]
        public void Exception_includes_line_and_column() {
            var ex = Assert.Throws<CssParseException>(() => CssParser.Parse("a {\n  color red;\n}"));
            Assert.That(ex.Line, Is.EqualTo(2));
            Assert.That(ex.Column, Is.GreaterThan(0));
        }

        [Test]
        public void Multiple_rules_at_top_level() {
            var s = CssParser.Parse(".a { color: red; } .b { color: blue; } .c { color: green; }");
            Assert.That(s.Rules, Has.Count.EqualTo(3));
        }

        [Test]
        public void Rgba_value_preserved() {
            var s = CssParser.Parse("a { color: rgba(255, 0, 0, 0.5); }");
            var d = ((StyleRule)s.Rules[0]).Declarations[0];
            Assert.That(d.ValueText, Is.EqualTo("rgba(255, 0, 0, 0.5)"));
        }

        [Test]
        public void Supports_grid_passes_when_grid_implemented() {
            // Fixed in #247: `@supports` is now parsed into a SupportsRule;
            // inner rules survive in the AST; the cascade gates `.target`
            // on `display: grid` evaluation.
            var s = CssParser.Parse(
                ".fallback { display: block; }" +
                "@supports (display: grid) { .target { display: grid; } }");
            Assert.That(s.Rules, Has.Count.EqualTo(2));
            var fallback = (StyleRule)s.Rules[0];
            Assert.That(fallback.Selectors[0], Is.EqualTo(".fallback"));
            var sup = (SupportsRule)s.Rules[1];
            Assert.That(sup.ConditionText, Does.Contain("display: grid"));
            Assert.That(sup.Rules, Has.Count.EqualTo(1));
            Assert.That(((StyleRule)sup.Rules[0]).Selectors[0], Is.EqualTo(".target"));
        }

        [Test]
        public void Lenient_recovers_from_unterminated_block_at_eof() {
            // Audit recovery: an unterminated `{` (the user is mid-edit and saves
            // partway through typing the closing brace) must not throw in lenient
            // mode. The earlier well-formed rule must survive intact, the broken
            // rule keeps whatever declarations it managed to parse, and the parser
            // does not advance past EOF. Hot-reload depends on this — the
            // HotReloadCoordinator only catches exceptions, so a non-throwing
            // parse must still produce a usable Stylesheet.
            var opts = new ParseOptions { ThrowOnError = false };
            var s = CssParser.Parse(".ok { color: red; } .broken { color: blue; ", opts);
            Assert.That(s.Rules, Has.Count.EqualTo(2));
            Assert.That(((StyleRule)s.Rules[0]).Selectors[0], Is.EqualTo(".ok"));
            Assert.That(((StyleRule)s.Rules[0]).Declarations[0].ValueText, Is.EqualTo("red"));
            var broken = (StyleRule)s.Rules[1];
            Assert.That(broken.Selectors[0], Is.EqualTo(".broken"));
            Assert.That(broken.Declarations[0].ValueText, Is.EqualTo("blue"));
        }

        [Test]
        public void Lenient_recovers_from_unterminated_trailing_comment() {
            var opts = new ParseOptions { ThrowOnError = false };
            var s = CssParser.Parse(".ok { color: red; } /* mid edit", opts);

            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var rule = (StyleRule)s.Rules[0];
            Assert.That(rule.Selectors[0], Is.EqualTo(".ok"));
            Assert.That(rule.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(rule.Declarations[0].ValueText, Is.EqualTo("red"));
        }

        [Test]
        public void Lenient_recovers_at_next_selector_after_unterminated_block() {
            // The classic mid-edit hazard: an unclosed block followed by a real
            // rule. The parser must not eat the next selector — `.next` should
            // emerge as its own rule even though `.broken`'s `}` is missing.
            var opts = new ParseOptions { ThrowOnError = false };
            var s = CssParser.Parse(".broken { color: red;  .next { color: blue; }", opts);
            // Without a closing brace before `.next`, the recovery path treats
            // `.next` as a nested rule per CSS Nesting (this is the documented
            // behavior — the parser cannot disambiguate). The outer rule must
            // still be present and the file must not throw.
            Assert.That(s.Rules.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(((StyleRule)s.Rules[0]).Selectors[0], Is.EqualTo(".broken"));
        }

        [Test]
        public void Property_at_rule_is_parsed_and_produces_an_AtPropertyRule_node() {
            // Updated 2026-05-31: @property (CSS Properties and Values API L1) is
            // now parsed by CssParser.ParseAtRule (case "property"). The rule is
            // represented as an AtPropertyRule AST node alongside any StyleRules.
            // This test was formerly "Property_at_rule_currently_dropped_by_parser"
            // which expected Count==1 (the @property block silently dropped). The
            // engine now handles @property so both rules survive.
            var s = CssParser.Parse(
                ".fallback { color: red; }" +
                "@property --hue { syntax: \"<number>\"; inherits: false; initial-value: 0; }");
            // Both rules survive — the @property block is now parsed, not skipped.
            Assert.That(s.Rules, Has.Count.EqualTo(2));
            var styleRule = (StyleRule)s.Rules[0];
            Assert.That(styleRule.Selectors[0], Is.EqualTo(".fallback"));
            Assert.That(s.Rules[1], Is.InstanceOf<AtPropertyRule>());
        }

        [Test]
        public void Namespace_rule_is_accepted_silently_and_does_not_consume_following_rules() {
            var s = CssParser.Parse(
                "@namespace svg url(\"http://www.w3.org/2000/svg\");\n" +
                ".foo { color: red; }");
            Assert.That(s.Rules, Has.Count.EqualTo(1));
            var r = (StyleRule)s.Rules[0];
            Assert.That(r.Selectors[0], Is.EqualTo(".foo"));
            Assert.That(r.Declarations, Has.Count.EqualTo(1));
            Assert.That(r.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(r.Declarations[0].ValueText, Is.EqualTo("red"));

            var s2 = CssParser.Parse(
                "@namespace url(\"http://www.w3.org/2000/svg\");\n" +
                ".bar { color: blue; }\n" +
                ".baz { color: green; }");
            Assert.That(s2.Rules, Has.Count.EqualTo(2));
            Assert.That(((StyleRule)s2.Rules[0]).Selectors[0], Is.EqualTo(".bar"));
            Assert.That(((StyleRule)s2.Rules[1]).Selectors[0], Is.EqualTo(".baz"));

            var opts = new ParseOptions { ThrowOnError = true };
            Assert.DoesNotThrow(() => CssParser.Parse(
                "@namespace svg url(\"http://www.w3.org/2000/svg\"); .foo {}", opts));
        }
    }
}
