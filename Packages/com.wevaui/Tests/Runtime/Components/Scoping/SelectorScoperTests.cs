using System.Linq;
using NUnit.Framework;
using Weva.Components.Scoping;

namespace Weva.Tests.Components.Scoping {
    public class SelectorScoperTests {
        const string Id = "uui-sc-card-1234abcd";

        static string ScopeOne(string sel) {
            var list = SelectorScoper.Scope(sel, Id);
            Assert.That(list.Count, Is.EqualTo(1), "expected exactly one rewritten selector for: " + sel);
            return list[0];
        }

        [Test]
        public void Single_class_is_appended_with_scope_attr() {
            Assert.That(ScopeOne(".foo"), Is.EqualTo(".foo[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Tag_is_appended_with_scope_attr() {
            Assert.That(ScopeOne("div"), Is.EqualTo("div[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Compound_attribute_appended_to_rightmost_compound() {
            Assert.That(ScopeOne("div.foo"), Is.EqualTo("div.foo[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Descendant_combinator_only_rightmost_gets_scope() {
            Assert.That(ScopeOne("a b"), Is.EqualTo("a b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Child_combinator_only_rightmost_gets_scope() {
            Assert.That(ScopeOne("a > b"), Is.EqualTo("a > b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Adjacent_sibling_combinator_only_rightmost_gets_scope() {
            Assert.That(ScopeOne("a + b"), Is.EqualTo("a + b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void General_sibling_combinator_only_rightmost_gets_scope() {
            Assert.That(ScopeOne("a ~ b"), Is.EqualTo("a ~ b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Comma_list_each_selector_gets_scope() {
            var rewritten = SelectorScoper.Scope("a, b", Id).ToList();
            Assert.That(rewritten.Count, Is.EqualTo(2));
            Assert.That(rewritten[0], Is.EqualTo("a[data-uui-scope=\"" + Id + "\"]"));
            Assert.That(rewritten[1], Is.EqualTo("b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Bare_host_becomes_host_marker_attribute() {
            Assert.That(ScopeOne(":host"), Is.EqualTo("[data-uui-host=\"" + Id + "\"]"));
        }

        [Test]
        public void Host_compound_with_class_uses_host_marker() {
            Assert.That(ScopeOne(":host.disabled"), Is.EqualTo("[data-uui-host=\"" + Id + "\"].disabled"));
        }

        [Test]
        public void Host_function_with_class_uses_host_marker() {
            Assert.That(ScopeOne(":host(.disabled)"), Is.EqualTo("[data-uui-host=\"" + Id + "\"].disabled"));
        }

        [Test]
        public void Host_function_with_alternative_list_expands_into_multiple_selectors() {
            var rewritten = SelectorScoper.Scope(":host(.a, .b)", Id).ToList();
            Assert.That(rewritten.Count, Is.EqualTo(2));
            Assert.That(rewritten[0], Is.EqualTo("[data-uui-host=\"" + Id + "\"].a"));
            Assert.That(rewritten[1], Is.EqualTo("[data-uui-host=\"" + Id + "\"].b"));
        }

        [Test]
        public void Host_function_with_alternative_list_expands_when_not_rightmost() {
            var rewritten = SelectorScoper.Scope(":host(.a, .b) > .item", Id).ToList();
            Assert.That(rewritten.Count, Is.EqualTo(2));
            Assert.That(rewritten[0], Is.EqualTo("[data-uui-host=\"" + Id + "\"].a > .item[data-uui-scope=\"" + Id + "\"]"));
            Assert.That(rewritten[1], Is.EqualTo("[data-uui-host=\"" + Id + "\"].b > .item[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Host_function_with_state_pseudo_class() {
            Assert.That(ScopeOne(":host(:hover)"), Is.EqualTo("[data-uui-host=\"" + Id + "\"]:hover"));
        }

        [Test]
        public void Pseudo_element_scope_attr_inserted_before() {
            Assert.That(ScopeOne("a::before"), Is.EqualTo("a[data-uui-scope=\"" + Id + "\"]::before"));
        }

        [Test]
        public void Pseudo_class_scope_attr_inserted_before() {
            Assert.That(ScopeOne("a:hover"), Is.EqualTo("a[data-uui-scope=\"" + Id + "\"]:hover"));
        }

        [Test]
        public void Combinator_with_pseudo_class_scope_attr_inserted_on_rightmost() {
            Assert.That(ScopeOne("a > b:hover"), Is.EqualTo("a > b[data-uui-scope=\"" + Id + "\"]:hover"));
        }

        [Test]
        public void Universal_selector_gets_scope_attr_appended() {
            Assert.That(ScopeOne("*"), Is.EqualTo("*[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Host_with_descendant_left_uses_host_marker_right_uses_scope() {
            Assert.That(ScopeOne(":host > .foo"), Is.EqualTo("[data-uui-host=\"" + Id + "\"] > .foo[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Id_selector_gets_scope_attr_appended() {
            Assert.That(ScopeOne("#start"), Is.EqualTo("#start[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Attribute_selector_gets_scope_attr_appended() {
            Assert.That(ScopeOne("[disabled]"), Is.EqualTo("[disabled][data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Three_part_chain_only_last_gets_scope() {
            Assert.That(ScopeOne("a > b > c"), Is.EqualTo("a > b > c[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Attribute_with_value_containing_special_chars_is_preserved() {
            Assert.That(ScopeOne("[data-x=\"a > b\"]"), Is.EqualTo("[data-x=\"a > b\"][data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Comma_inside_attribute_value_does_not_split_selector() {
            var rewritten = SelectorScoper.Scope("[data-x=\"a,b\"]", Id).ToList();
            Assert.That(rewritten.Count, Is.EqualTo(1));
            Assert.That(rewritten[0], Is.EqualTo("[data-x=\"a,b\"][data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Comma_inside_not_does_not_split_selector() {
            var rewritten = SelectorScoper.Scope(":not(.a, .b)", Id).ToList();
            Assert.That(rewritten.Count, Is.EqualTo(1));
            Assert.That(rewritten[0], Is.EqualTo(":not(.a, .b)[data-uui-scope=\"" + Id + "\"]"));
        }
    }
}
