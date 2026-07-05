using NUnit.Framework;
using Weva.Components.Scoping;
using Weva.Css;

namespace Weva.Tests.Components.Scoping {
    public class StylesheetScoperTests {
        const string Id = "uui-sc-card-1234abcd";

        [Test]
        public void Empty_stylesheet_yields_empty_rewritten() {
            var input = CssParser.Parse("");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.Rewritten.Rules.Count, Is.EqualTo(0));
            Assert.That(scoped.Original, Is.SameAs(input));
        }

        [Test]
        public void ScopeId_stored_on_result() {
            var input = CssParser.Parse(".x { color: red; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.ScopeId.Value, Is.EqualTo(Id));
        }

        [Test]
        public void Multiple_style_rules_each_rewritten() {
            var input = CssParser.Parse(".a { color: red; } .b { color: blue; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.Rewritten.Rules.Count, Is.EqualTo(2));
            var r0 = (StyleRule)scoped.Rewritten.Rules[0];
            var r1 = (StyleRule)scoped.Rewritten.Rules[1];
            Assert.That(r0.Selectors[0], Is.EqualTo(".a[data-uui-scope=\"" + Id + "\"]"));
            Assert.That(r1.Selectors[0], Is.EqualTo(".b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Media_rule_wraps_rewritten_inner_rules() {
            var input = CssParser.Parse("@media (min-width: 800px) { .x { color: red; } }");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.Rewritten.Rules.Count, Is.EqualTo(1));
            var media = (MediaRule)scoped.Rewritten.Rules[0];
            Assert.That(media.Rules.Count, Is.EqualTo(1));
            var inner = (StyleRule)media.Rules[0];
            Assert.That(inner.Selectors[0], Is.EqualTo(".x[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Keyframes_rule_passes_through_unchanged() {
            var input = CssParser.Parse("@keyframes spin { 0% { opacity: 0; } 100% { opacity: 1; } }");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.Rewritten.Rules.Count, Is.EqualTo(1));
            var kf = (KeyframesRule)scoped.Rewritten.Rules[0];
            Assert.That(kf.Name, Is.EqualTo("spin"));
        }

        [Test]
        public void Import_rule_passes_through_unchanged() {
            var input = CssParser.Parse("@import \"theme.css\";");
            var scoped = StylesheetScoper.Scope(input, Id);
            Assert.That(scoped.Rewritten.Rules.Count, Is.EqualTo(1));
            var ir = (ImportRule)scoped.Rewritten.Rules[0];
            Assert.That(ir.Href, Is.EqualTo("theme.css"));
        }

        [Test]
        public void Original_is_not_mutated() {
            var input = CssParser.Parse(".x { color: red; }");
            var beforeSel = ((StyleRule)input.Rules[0]).Selectors[0];
            StylesheetScoper.Scope(input, Id);
            var afterSel = ((StyleRule)input.Rules[0]).Selectors[0];
            Assert.That(afterSel, Is.EqualTo(beforeSel));
        }

        [Test]
        public void Comma_separated_selector_expanded_correctly() {
            var input = CssParser.Parse(".a, .b { color: red; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            var sr = (StyleRule)scoped.Rewritten.Rules[0];
            Assert.That(sr.Selectors.Count, Is.EqualTo(2));
            Assert.That(sr.Selectors[0], Is.EqualTo(".a[data-uui-scope=\"" + Id + "\"]"));
            Assert.That(sr.Selectors[1], Is.EqualTo(".b[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Host_alternative_list_expands_to_multiple_selectors() {
            var input = CssParser.Parse(":host(.a, .b) { color: red; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            var sr = (StyleRule)scoped.Rewritten.Rules[0];
            Assert.That(sr.Selectors.Count, Is.EqualTo(2));
            Assert.That(sr.Selectors[0], Is.EqualTo("[data-uui-host=\"" + Id + "\"].a"));
            Assert.That(sr.Selectors[1], Is.EqualTo("[data-uui-host=\"" + Id + "\"].b"));
        }

        [Test]
        public void Host_alternative_list_expands_before_descendant_selector() {
            var input = CssParser.Parse(":host(.a, .b) .item { color: red; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            var sr = (StyleRule)scoped.Rewritten.Rules[0];
            Assert.That(sr.Selectors.Count, Is.EqualTo(2));
            Assert.That(sr.Selectors[0], Is.EqualTo("[data-uui-host=\"" + Id + "\"].a .item[data-uui-scope=\"" + Id + "\"]"));
            Assert.That(sr.Selectors[1], Is.EqualTo("[data-uui-host=\"" + Id + "\"].b .item[data-uui-scope=\"" + Id + "\"]"));
        }

        [Test]
        public void Declarations_carried_through_unchanged() {
            var input = CssParser.Parse(".x { color: red; padding: 4px; }");
            var scoped = StylesheetScoper.Scope(input, Id);
            var sr = (StyleRule)scoped.Rewritten.Rules[0];
            Assert.That(sr.Declarations.Count, Is.EqualTo(2));
            Assert.That(sr.Declarations[0].Property, Is.EqualTo("color"));
            Assert.That(sr.Declarations[1].Property, Is.EqualTo("padding"));
        }
    }
}
