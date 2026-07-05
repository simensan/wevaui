using NUnit.Framework;
using Weva.Css.Selectors;

// W7 DevTools: verify that CompiledSelector.SourceText retains the original
// authored selector text so the DevTools cascade trace can display it.
//
// Design: text is captured at parse time (SelectorParser.Parse stores the
// trimmed input string). For selector-list rules CascadeEngine calls
// SelectorParser.ParseSelectorListCompiled which slices per complex selector.
// No allocation happens during matching.

namespace Weva.Tests.Css.Selectors {
    public class SelectorSourceTextTests {
        static CompiledSelector P(string s) => SelectorParser.Parse(s);

        // ------------------------------------------------------------------ //
        //  Simple selectors                                                    //
        // ------------------------------------------------------------------ //

        [Test]
        public void Type_selector_retains_source_text() {
            var s = P("div");
            Assert.That(s.SourceText, Is.EqualTo("div"));
        }

        [Test]
        public void Class_selector_retains_source_text() {
            var s = P(".card");
            Assert.That(s.SourceText, Is.EqualTo(".card"));
        }

        [Test]
        public void Id_selector_retains_source_text() {
            var s = P("#hero");
            Assert.That(s.SourceText, Is.EqualTo("#hero"));
        }

        [Test]
        public void Universal_selector_retains_source_text() {
            var s = P("*");
            Assert.That(s.SourceText, Is.EqualTo("*"));
        }

        // ------------------------------------------------------------------ //
        //  Compound selectors                                                  //
        // ------------------------------------------------------------------ //

        [Test]
        public void Compound_type_class_id_retains_source_text() {
            var s = P("div.foo#bar");
            Assert.That(s.SourceText, Is.EqualTo("div.foo#bar"));
        }

        [Test]
        public void Compound_multiple_classes_retains_source_text() {
            var s = P(".a.b.c");
            Assert.That(s.SourceText, Is.EqualTo(".a.b.c"));
        }

        // ------------------------------------------------------------------ //
        //  Complex selectors — all combinator types                           //
        // ------------------------------------------------------------------ //

        [Test]
        public void Descendant_combinator_retains_source_text() {
            var s = P(".parent .child");
            Assert.That(s.SourceText, Is.EqualTo(".parent .child"));
        }

        [Test]
        public void Child_combinator_retains_source_text() {
            var s = P(".card > .title");
            Assert.That(s.SourceText, Is.EqualTo(".card > .title"));
        }

        [Test]
        public void Adjacent_sibling_combinator_retains_source_text() {
            var s = P("h1 + p");
            Assert.That(s.SourceText, Is.EqualTo("h1 + p"));
        }

        [Test]
        public void General_sibling_combinator_retains_source_text() {
            var s = P("h1 ~ p");
            Assert.That(s.SourceText, Is.EqualTo("h1 ~ p"));
        }

        [Test]
        public void Multi_combinator_chain_retains_source_text() {
            var s = P(".card:hover > .title");
            Assert.That(s.SourceText, Is.EqualTo(".card:hover > .title"));
        }

        // ------------------------------------------------------------------ //
        //  Attribute selectors                                                 //
        // ------------------------------------------------------------------ //

        [Test]
        public void Attribute_exists_retains_source_text() {
            var s = P("[disabled]");
            Assert.That(s.SourceText, Is.EqualTo("[disabled]"));
        }

        [Test]
        public void Attribute_equals_retains_source_text() {
            var s = P("[type=\"text\"]");
            Assert.That(s.SourceText, Is.EqualTo("[type=\"text\"]"));
        }

        [Test]
        public void Attribute_prefix_operator_retains_source_text() {
            var s = P("[href^=\"https\"]");
            Assert.That(s.SourceText, Is.EqualTo("[href^=\"https\"]"));
        }

        [Test]
        public void Attribute_suffix_operator_retains_source_text() {
            var s = P("[src$=\".png\"]");
            Assert.That(s.SourceText, Is.EqualTo("[src$=\".png\"]"));
        }

        [Test]
        public void Attribute_substring_operator_retains_source_text() {
            var s = P("[class*=\"icon\"]");
            Assert.That(s.SourceText, Is.EqualTo("[class*=\"icon\"]"));
        }

        [Test]
        public void Attribute_dash_match_retains_source_text() {
            var s = P("[lang|=\"en\"]");
            Assert.That(s.SourceText, Is.EqualTo("[lang|=\"en\"]"));
        }

        [Test]
        public void Attribute_whitespace_contains_retains_source_text() {
            var s = P("[class~=\"active\"]");
            Assert.That(s.SourceText, Is.EqualTo("[class~=\"active\"]"));
        }

        // ------------------------------------------------------------------ //
        //  Pseudo-classes                                                      //
        // ------------------------------------------------------------------ //

        [Test]
        public void Pseudo_class_hover_retains_source_text() {
            var s = P(":hover");
            Assert.That(s.SourceText, Is.EqualTo(":hover"));
        }

        [Test]
        public void Pseudo_class_focus_visible_retains_source_text() {
            var s = P(":focus-visible");
            Assert.That(s.SourceText, Is.EqualTo(":focus-visible"));
        }

        [Test]
        public void Pseudo_class_first_child_retains_source_text() {
            var s = P(":first-child");
            Assert.That(s.SourceText, Is.EqualTo(":first-child"));
        }

        // ------------------------------------------------------------------ //
        //  Functional pseudo-classes                                           //
        // ------------------------------------------------------------------ //

        [Test]
        public void Nth_child_simple_retains_source_text() {
            var s = P(":nth-child(2)");
            Assert.That(s.SourceText, Is.EqualTo(":nth-child(2)"));
        }

        [Test]
        public void Nth_child_an_plus_b_retains_source_text() {
            var s = P(":nth-child(2n+1)");
            Assert.That(s.SourceText, Is.EqualTo(":nth-child(2n+1)"));
        }

        [Test]
        public void Nth_child_odd_retains_source_text() {
            var s = P(":nth-child(odd)");
            Assert.That(s.SourceText, Is.EqualTo(":nth-child(odd)"));
        }

        [Test]
        public void Not_pseudo_retains_source_text() {
            var s = P(":not(.disabled)");
            Assert.That(s.SourceText, Is.EqualTo(":not(.disabled)"));
        }

        [Test]
        public void Is_pseudo_retains_source_text() {
            var s = P(":is(h1, h2, h3)");
            Assert.That(s.SourceText, Is.EqualTo(":is(h1, h2, h3)"));
        }

        [Test]
        public void Where_pseudo_retains_source_text() {
            var s = P(":where(.a, .b)");
            Assert.That(s.SourceText, Is.EqualTo(":where(.a, .b)"));
        }

        // ------------------------------------------------------------------ //
        //  Pseudo-elements                                                     //
        // ------------------------------------------------------------------ //

        [Test]
        public void Pseudo_element_before_retains_source_text() {
            var s = P(".item::before");
            Assert.That(s.SourceText, Is.EqualTo(".item::before"));
        }

        [Test]
        public void Pseudo_element_after_retains_source_text() {
            var s = P("div::after");
            Assert.That(s.SourceText, Is.EqualTo("div::after"));
        }

        // ------------------------------------------------------------------ //
        //  Whitespace trimming                                                 //
        // ------------------------------------------------------------------ //

        [Test]
        public void Leading_trailing_whitespace_is_trimmed_from_source_text() {
            // Selectors from parsed CSS often have surrounding whitespace.
            var s = P("  .foo  ");
            // The parser already strips leading whitespace in ParseSequence;
            // Parse() trims the whole input string.
            Assert.That(s.SourceText, Is.EqualTo(".foo"));
        }

        // ------------------------------------------------------------------ //
        //  Selector list — each compiled selector carries its own slice       //
        // ------------------------------------------------------------------ //

        [Test]
        public void ParseSelectorListCompiled_each_entry_has_own_slice() {
            var list = SelectorParser.ParseSelectorListCompiled("h1, h2, h3");
            Assert.That(list, Has.Count.EqualTo(3));
            Assert.That(list[0].SourceText, Is.EqualTo("h1"));
            Assert.That(list[1].SourceText, Is.EqualTo("h2"));
            Assert.That(list[2].SourceText, Is.EqualTo("h3"));
        }

        [Test]
        public void ParseSelectorListCompiled_complex_selectors_each_have_own_slice() {
            var list = SelectorParser.ParseSelectorListCompiled(".card > .title, .box .label");
            Assert.That(list, Has.Count.EqualTo(2));
            Assert.That(list[0].SourceText, Is.EqualTo(".card > .title"));
            Assert.That(list[1].SourceText, Is.EqualTo(".box .label"));
        }

        [Test]
        public void ParseSelectorListCompiled_single_selector_has_full_text() {
            var list = SelectorParser.ParseSelectorListCompiled("div.foo#bar");
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0].SourceText, Is.EqualTo("div.foo#bar"));
        }

        [Test]
        public void ParseSelectorListCompiled_whitespace_around_comma_trimmed_per_slice() {
            var list = SelectorParser.ParseSelectorListCompiled("  .a  ,  .b  ");
            Assert.That(list, Has.Count.EqualTo(2));
            Assert.That(list[0].SourceText, Is.EqualTo(".a"));
            Assert.That(list[1].SourceText, Is.EqualTo(".b"));
        }

        [Test]
        public void SourceText_not_null_never_returns_null() {
            // Edge case: Parse should never yield a null SourceText.
            var s = P("*");
            Assert.That(s.SourceText, Is.Not.Null);
        }
    }
}
