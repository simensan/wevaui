using NUnit.Framework;
using Weva.Css.Selectors;

namespace Weva.Tests.Css.Selectors {
    public class SelectorParserTests {
        static CompiledSelector P(string s) => SelectorParser.Parse(s);

        [Test]
        public void Universal_selector() {
            var s = P("*");
            Assert.That(s.Sequence.Compounds, Has.Count.EqualTo(1));
            Assert.That(s.Sequence.Compounds[0].Parts[0], Is.InstanceOf<UniversalSelector>());
        }

        [Test]
        public void Type_selector() {
            var s = P("div");
            var t = s.Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(t, Is.Not.Null);
            Assert.That(t.TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Type_selector_lowercased() {
            var s = P("DIV");
            var t = s.Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(t.TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Class_selector() {
            var s = P(".foo");
            var c = s.Sequence.Compounds[0].Parts[0] as ClassSelector;
            Assert.That(c, Is.Not.Null);
            Assert.That(c.ClassName, Is.EqualTo("foo"));
        }

        [Test]
        public void Id_selector() {
            var s = P("#bar");
            var i = s.Sequence.Compounds[0].Parts[0] as IdSelector;
            Assert.That(i, Is.Not.Null);
            Assert.That(i.Id, Is.EqualTo("bar"));
        }

        [Test]
        public void Compound_tag_class_id() {
            var s = P("div.foo#bar");
            var c = s.Sequence.Compounds[0];
            Assert.That(c.Parts, Has.Count.EqualTo(3));
            Assert.That(c.Parts[0], Is.InstanceOf<TypeSelector>());
            Assert.That(c.Parts[1], Is.InstanceOf<ClassSelector>());
            Assert.That(c.Parts[2], Is.InstanceOf<IdSelector>());
        }

        [Test]
        public void Multiple_classes() {
            var s = P(".a.b.c");
            Assert.That(s.Sequence.Compounds[0].Parts, Has.Count.EqualTo(3));
        }

        [Test]
        public void Attribute_presence() {
            var s = P("[disabled]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Name, Is.EqualTo("disabled"));
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.Exists));
        }

        [Test]
        public void Attribute_equals_unquoted() {
            var s = P("[type=text]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.Equals));
            Assert.That(a.Value, Is.EqualTo("text"));
        }

        [Test]
        public void Attribute_equals_double_quoted() {
            var s = P("[name=\"x y\"]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Value, Is.EqualTo("x y"));
        }

        [Test]
        public void Attribute_equals_single_quoted() {
            var s = P("[name='hi']");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Value, Is.EqualTo("hi"));
        }

        [Test]
        public void Attribute_whitespace_contains() {
            var s = P("[class~=foo]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.WhitespaceContains));
        }

        [Test]
        public void Attribute_dash_match() {
            var s = P("[lang|=en]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.DashMatch));
        }

        [Test]
        public void Attribute_prefix() {
            var s = P("[href^=\"https://\"]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.Prefix));
            Assert.That(a.Value, Is.EqualTo("https://"));
        }

        [Test]
        public void Attribute_suffix() {
            var s = P("[src$=\".png\"]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.Suffix));
        }

        [Test]
        public void Attribute_substring() {
            var s = P("[title*=foo]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Operator, Is.EqualTo(AttributeOperator.Substring));
        }

        [Test]
        public void Attribute_name_lowercased() {
            var s = P("[CLASS=x]");
            var a = s.Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(a.Name, Is.EqualTo("class"));
        }

        [Test]
        public void Descendant_combinator() {
            var s = P("a b");
            Assert.That(s.Sequence.Compounds, Has.Count.EqualTo(2));
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.Descendant));
        }

        [Test]
        public void Child_combinator() {
            var s = P("a > b");
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.Child));
        }

        [Test]
        public void Adjacent_sibling_combinator() {
            var s = P("a + b");
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.AdjacentSibling));
        }

        [Test]
        public void General_sibling_combinator() {
            var s = P("a ~ b");
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.GeneralSibling));
        }

        [Test]
        public void Combinators_without_spaces() {
            var s = P("a>b+c~d");
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.Child));
            Assert.That(s.Sequence.Combinators[1], Is.EqualTo(Combinator.AdjacentSibling));
            Assert.That(s.Sequence.Combinators[2], Is.EqualTo(Combinator.GeneralSibling));
        }

        [Test]
        public void Mixed_combinators() {
            var s = P("a > b c + d");
            Assert.That(s.Sequence.Compounds, Has.Count.EqualTo(4));
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.Child));
            Assert.That(s.Sequence.Combinators[1], Is.EqualTo(Combinator.Descendant));
            Assert.That(s.Sequence.Combinators[2], Is.EqualTo(Combinator.AdjacentSibling));
        }

        [Test]
        public void First_child_pseudo() {
            var s = P(":first-child");
            var p = s.Sequence.Compounds[0].Parts[0] as PseudoClassSelector;
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.FirstChild));
        }

        [Test]
        public void Last_child_pseudo() {
            var p = (PseudoClassSelector)P(":last-child").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.LastChild));
        }

        [Test]
        public void Only_child_pseudo() {
            var p = (PseudoClassSelector)P(":only-child").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.OnlyChild));
        }

        [Test]
        public void First_of_type_pseudo() {
            var p = (PseudoClassSelector)P(":first-of-type").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.FirstOfType));
        }

        [Test]
        public void Empty_pseudo() {
            var p = (PseudoClassSelector)P(":empty").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Empty));
        }

        [Test]
        public void Nth_child_an_plus_b() {
            var p = (PseudoClassSelector)P(":nth-child(2n+1)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.NthChild));
            Assert.That(p.Nth.A, Is.EqualTo(2));
            Assert.That(p.Nth.B, Is.EqualTo(1));
        }

        [Test]
        public void Nth_child_odd() {
            var p = (PseudoClassSelector)P(":nth-child(odd)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Nth.A, Is.EqualTo(2));
            Assert.That(p.Nth.B, Is.EqualTo(1));
        }

        [Test]
        public void Nth_child_even() {
            var p = (PseudoClassSelector)P(":nth-child(even)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Nth.A, Is.EqualTo(2));
            Assert.That(p.Nth.B, Is.EqualTo(0));
        }

        [Test]
        public void Nth_child_integer() {
            var p = (PseudoClassSelector)P(":nth-child(5)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Nth.A, Is.EqualTo(0));
            Assert.That(p.Nth.B, Is.EqualTo(5));
        }

        [Test]
        public void Nth_child_negative_n_plus_b() {
            var p = (PseudoClassSelector)P(":nth-child(-n+3)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Nth.A, Is.EqualTo(-1));
            Assert.That(p.Nth.B, Is.EqualTo(3));
        }

        [Test]
        public void Nth_child_just_n() {
            var p = (PseudoClassSelector)P(":nth-child(n)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Nth.A, Is.EqualTo(1));
            Assert.That(p.Nth.B, Is.EqualTo(0));
        }

        [Test]
        public void Nth_of_type() {
            var p = (PseudoClassSelector)P(":nth-of-type(2)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.NthOfType));
        }

        [Test]
        public void Nth_last_child() {
            var p = (PseudoClassSelector)P(":nth-last-child(1)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.NthLastChild));
        }

        [Test]
        public void Nth_last_of_type() {
            var p = (PseudoClassSelector)P(":nth-last-of-type(odd)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.NthLastOfType));
        }

        // Selectors L4 §6.6.5 — `:nth-child(An+B of <selector-list>)` must
        // parse successfully so the rest of the stylesheet survives.
        // TODO: the `of <selector>` filter is currently dropped silently; the
        // selector matches as plain `:nth-child(An+B)` until the matcher
        // honors the filter.
        [Test]
        public void Nth_child_of_selector_parses_and_drops_filter() {
            var p = (PseudoClassSelector)P(":nth-child(2 of .item)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.NthChild));
            Assert.That(p.Nth.A, Is.EqualTo(0));
            Assert.That(p.Nth.B, Is.EqualTo(2));

            // Regression guards: existing An+B / keyword forms unaffected.
            var anb = (PseudoClassSelector)P(":nth-child(2n+1)").Sequence.Compounds[0].Parts[0];
            Assert.That(anb.Nth.A, Is.EqualTo(2));
            Assert.That(anb.Nth.B, Is.EqualTo(1));

            var odd = (PseudoClassSelector)P(":nth-child(odd)").Sequence.Compounds[0].Parts[0];
            Assert.That(odd.Nth.A, Is.EqualTo(2));
            Assert.That(odd.Nth.B, Is.EqualTo(1));

            // Compound filter must also be accepted (no parse failure).
            Assert.DoesNotThrow(() => P(":nth-child(2n+1 of li.active, .pinned)"));
        }

        [Test]
        public void Not_with_class() {
            // After #258, :not() parses into InnerList (was InnerSimple,
            // single selector only). For a single-class arg the list has
            // one CompoundSequence containing one ClassSelector.
            var p = (PseudoClassSelector)P(":not(.foo)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Not));
            Assert.That(p.InnerList, Has.Count.EqualTo(1));
            var compound = p.InnerList[0].Compounds[0];
            Assert.That(compound.Parts[0], Is.InstanceOf<ClassSelector>());
        }

        [Test]
        public void Is_with_list() {
            var p = (PseudoClassSelector)P(":is(.a, .b, .c)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Is));
            Assert.That(p.InnerList, Has.Count.EqualTo(3));
        }

        [Test]
        public void Where_with_list() {
            var p = (PseudoClassSelector)P(":where(div, span)").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Where));
            Assert.That(p.InnerList, Has.Count.EqualTo(2));
        }

        [Test]
        public void State_pseudo_hover() {
            var p = (PseudoClassSelector)P(":hover").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Hover));
        }

        [Test]
        public void State_pseudo_focus() {
            var p = (PseudoClassSelector)P(":focus").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Focus));
        }

        [Test]
        public void State_pseudo_focus_visible() {
            var p = (PseudoClassSelector)P(":focus-visible").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.FocusVisible));
        }

        [Test]
        public void State_pseudo_active() {
            var p = (PseudoClassSelector)P(":active").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Active));
        }

        [Test]
        public void State_pseudo_disabled() {
            var p = (PseudoClassSelector)P(":disabled").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Disabled));
        }

        [TestCase(":enabled", "Enabled")]
        [TestCase(":required", "Required")]
        [TestCase(":optional", "Optional")]
        [TestCase(":read-only", "ReadOnly")]
        [TestCase(":read-write", "ReadWrite")]
        [TestCase(":valid", "Valid")]
        [TestCase(":invalid", "Invalid")]
        public void Form_state_pseudos_parse(string selector, string expected) {
            var p = (PseudoClassSelector)P(selector).Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind.ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void State_pseudo_checked() {
            var p = (PseudoClassSelector)P(":checked").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Checked));
        }

        [Test]
        public void State_pseudo_root() {
            var p = (PseudoClassSelector)P(":root").Sequence.Compounds[0].Parts[0];
            Assert.That(p.Kind, Is.EqualTo(PseudoClassKind.Root));
        }

        [Test]
        public void Pseudo_element_before() {
            var s = P("p::before");
            Assert.That(s.PseudoElement, Is.EqualTo("before"));
        }

        [Test]
        public void Legacy_single_colon_before_aliases_pseudo_element() {
            var s = P("p:before");
            Assert.That(s.PseudoElement, Is.EqualTo("before"));
        }

        [Test]
        public void Pseudo_element_after() {
            var s = P("::after");
            Assert.That(s.PseudoElement, Is.EqualTo("after"));
        }

        [Test]
        public void Legacy_single_colon_after_aliases_pseudo_element() {
            var s = P(":after");
            Assert.That(s.PseudoElement, Is.EqualTo("after"));
        }

        [Test]
        public void Pseudo_element_placeholder() {
            var s = P("input::placeholder");
            Assert.That(s.PseudoElement, Is.EqualTo("placeholder"));
        }

        [Test]
        public void Pseudo_element_selection() {
            var s = P("::selection");
            Assert.That(s.PseudoElement, Is.EqualTo("selection"));
        }

        [Test]
        public void Pseudo_element_marker() {
            var s = P("li::marker");
            Assert.That(s.PseudoElement, Is.EqualTo("marker"));
        }

        [Test]
        public void Combinators_chain() {
            var s = P("div.foo > span:hover");
            Assert.That(s.Sequence.Compounds, Has.Count.EqualTo(2));
            Assert.That(s.Sequence.Combinators[0], Is.EqualTo(Combinator.Child));
        }

        [Test]
        public void Empty_string_throws() {
            Assert.Throws<SelectorParseException>(() => P(""));
        }

        [Test]
        public void Double_dot_throws() {
            Assert.Throws<SelectorParseException>(() => P("..foo"));
        }

        [Test]
        public void Double_combinator_throws() {
            Assert.Throws<SelectorParseException>(() => P("div >> span"));
        }

        [Test]
        public void Open_bracket_throws() {
            Assert.Throws<SelectorParseException>(() => P("["));
        }

        [Test]
        public void Unknown_pseudo_throws() {
            Assert.Throws<SelectorParseException>(() => P(":unknown-pseudo"));
        }

        [Test]
        public void Unterminated_bracket_throws() {
            Assert.Throws<SelectorParseException>(() => P("[foo"));
        }

        [Test]
        public void Unknown_pseudo_element_throws() {
            Assert.Throws<SelectorParseException>(() => P("::nope"));
        }

        [Test]
        public void Functional_language_and_direction_pseudos_parse_arguments() {
            var lang = (PseudoClassSelector)P(":lang(en-US)").Sequence.Compounds[0].Parts[0];
            Assert.That(lang.Kind, Is.EqualTo(PseudoClassKind.Lang));
            Assert.That(lang.Argument, Is.EqualTo("en-US"));

            var dir = (PseudoClassSelector)P(":dir(rtl)").Sequence.Compounds[0].Parts[0];
            Assert.That(dir.Kind, Is.EqualTo(PseudoClassKind.Dir));
            Assert.That(dir.Argument, Is.EqualTo("rtl"));
        }

        [Test]
        public void Link_and_range_state_pseudos_parse() {
            Assert.That(((PseudoClassSelector)P(":link").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.Link));
            Assert.That(((PseudoClassSelector)P(":visited").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.Visited));
            Assert.That(((PseudoClassSelector)P(":any-link").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.AnyLink));
            Assert.That(((PseudoClassSelector)P(":target").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.Target));
            Assert.That(((PseudoClassSelector)P(":scope").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.Scope));
            Assert.That(((PseudoClassSelector)P(":in-range").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.InRange));
            Assert.That(((PseudoClassSelector)P(":out-of-range").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.OutOfRange));
        }

        [Test]
        public void Default_and_user_validation_pseudos_parse() {
            Assert.That(((PseudoClassSelector)P(":default").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.Default));
            Assert.That(((PseudoClassSelector)P(":user-valid").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.UserValid));
            Assert.That(((PseudoClassSelector)P(":user-invalid").Sequence.Compounds[0].Parts[0]).Kind, Is.EqualTo(PseudoClassKind.UserInvalid));
        }

        [Test]
        public void Trailing_combinator_throws() {
            Assert.Throws<SelectorParseException>(() => P("div >"));
        }

        [Test]
        public void Empty_not_throws() {
            Assert.Throws<SelectorParseException>(() => P(":not()"));
        }

        [Test]
        public void Combinator_at_start_throws() {
            Assert.Throws<SelectorParseException>(() => P("> div"));
        }

        [Test]
        public void Empty_class_name_throws() {
            Assert.Throws<SelectorParseException>(() => P("."));
        }

        [Test]
        public void Empty_id_throws() {
            Assert.Throws<SelectorParseException>(() => P("#"));
        }

        // CSS Selectors L4 §6.3.5 — namespace-prefixed attribute selectors
        // must parse so the containing rule is not dropped. The engine has no
        // @namespace machinery in v1; the parser drops the prefix and matches
        // on the local name.
        [Test]
        public void Attribute_namespace_prefixed_parses_and_uses_local_name() {
            Assert.DoesNotThrow(() => P("[ns|attr]"));
            Assert.DoesNotThrow(() => P("[*|attr]"));
            Assert.DoesNotThrow(() => P("[ns|attr=val]"));

            var bare = P("[attr]").Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(bare, Is.Not.Null);
            Assert.That(bare.Name, Is.EqualTo("attr"));
            Assert.That(bare.Operator, Is.EqualTo(AttributeOperator.Exists));

            var ns = P("[ns|attr]").Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(ns, Is.Not.Null);
            Assert.That(ns.Name, Is.EqualTo("attr"));
            Assert.That(ns.Operator, Is.EqualTo(AttributeOperator.Exists));

            var any = P("[*|attr]").Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(any, Is.Not.Null);
            Assert.That(any.Name, Is.EqualTo("attr"));
            Assert.That(any.Operator, Is.EqualTo(AttributeOperator.Exists));

            var nsEq = P("[ns|attr=val]").Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(nsEq, Is.Not.Null);
            Assert.That(nsEq.Name, Is.EqualTo("attr"));
            Assert.That(nsEq.Operator, Is.EqualTo(AttributeOperator.Equals));
            Assert.That(nsEq.Value, Is.EqualTo("val"));

            // Regression guard: `|=` dash-match is still the dash-match operator,
            // NOT a namespace separator.
            var dash = P("[lang|=en]").Sequence.Compounds[0].Parts[0] as AttributeSelector;
            Assert.That(dash, Is.Not.Null);
            Assert.That(dash.Name, Is.EqualTo("lang"));
            Assert.That(dash.Operator, Is.EqualTo(AttributeOperator.DashMatch));
            Assert.That(dash.Value, Is.EqualTo("en"));
        }

        // CSS Selectors L4 §6.1 — namespace-prefixed type selectors must parse
        // so the containing rule is not dropped. v1 has no @namespace machinery;
        // the parser drops the prefix and matches on the local name (same v1
        // simplification as the attribute-selector path).
        [Test]
        public void Type_selector_namespace_prefixed_parses_and_uses_local_name() {
            Assert.DoesNotThrow(() => P("svg|circle"));
            Assert.DoesNotThrow(() => P("*|div"));
            Assert.DoesNotThrow(() => P("|div"));

            var bare = P("div").Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(bare, Is.Not.Null);
            Assert.That(bare.TagName, Is.EqualTo("div"));

            var ns = P("svg|circle").Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(ns, Is.Not.Null);
            Assert.That(ns.TagName, Is.EqualTo("circle"));

            var any = P("*|div").Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(any, Is.Not.Null);
            Assert.That(any.TagName, Is.EqualTo("div"));

            var empty = P("|div").Sequence.Compounds[0].Parts[0] as TypeSelector;
            Assert.That(empty, Is.Not.Null);
            Assert.That(empty.TagName, Is.EqualTo("div"));

            var combined = P("svg|circle.icon").Sequence.Compounds[0];
            Assert.That(combined.Parts, Has.Count.EqualTo(2));
            var combinedType = combined.Parts[0] as TypeSelector;
            Assert.That(combinedType, Is.Not.Null);
            Assert.That(combinedType.TagName, Is.EqualTo("circle"));
            var combinedClass = combined.Parts[1] as ClassSelector;
            Assert.That(combinedClass, Is.Not.Null);
            Assert.That(combinedClass.ClassName, Is.EqualTo("icon"));
        }
    }
}
