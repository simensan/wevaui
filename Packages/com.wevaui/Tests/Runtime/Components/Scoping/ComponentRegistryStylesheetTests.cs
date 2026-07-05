using System.Linq;
using NUnit.Framework;
using Weva.Components;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Dom;

namespace Weva.Tests.Components.Scoping {
    public class ComponentRegistryStylesheetTests {
        [Test]
        public void Register_with_stylesheet_stores_it() {
            var reg = new ComponentRegistry();
            var sheet = CssParser.Parse(".foo { color: red; }");
            reg.Register("card", new Element("template"), sheet);
            Assert.That(reg.TryGetStylesheet("card", out var got), Is.True);
            Assert.That(got, Is.Not.Null);
            Assert.That(got.Original, Is.SameAs(sheet));
        }

        [Test]
        public void TryGetStylesheet_returns_false_when_no_stylesheet_registered() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"));
            Assert.That(reg.TryGetStylesheet("card", out var got), Is.False);
            Assert.That(got, Is.Null);
        }

        [Test]
        public void Register_without_stylesheet_works() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"));
            Assert.That(reg.TryGet("card", out _), Is.True);
            Assert.That(reg.TryGetScopeId("card", out var id), Is.False);
            Assert.That(id.IsEmpty, Is.True);
        }

        [Test]
        public void Re_register_replaces_stylesheet() {
            var reg = new ComponentRegistry();
            var first = CssParser.Parse(".a { color: red; }");
            var second = CssParser.Parse(".b { color: blue; }");
            reg.Register("card", new Element("template"), first);
            reg.Register("card", new Element("template"), second);
            Assert.That(reg.TryGetStylesheet("card", out var got), Is.True);
            Assert.That(got.Original, Is.SameAs(second));
        }

        [Test]
        public void Re_register_without_stylesheet_clears_previous_stylesheet() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"), CssParser.Parse(".a { color: red; }"));
            reg.Register("card", new Element("template"));
            Assert.That(reg.TryGetStylesheet("card", out _), Is.False);
            Assert.That(reg.TryGetScopeId("card", out _), Is.False);
        }

        [Test]
        public void AllStylesheets_enumerates() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"), CssParser.Parse(".a { color: red; }"));
            reg.Register("hero", new Element("template"), CssParser.Parse(".b { color: blue; }"));
            var all = reg.AllStylesheets;
            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(all.Keys.OrderBy(s => s).ToArray(), Is.EqualTo(new[] { "card", "hero" }));
        }

        [Test]
        public void TryGetScopeId_returns_stable_id_for_same_name() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"), CssParser.Parse(".x {}"));
            reg.TryGetScopeId("card", out var first);
            // re-register same name — same id returned (deterministic Generate).
            reg.Register("card", new Element("template"), CssParser.Parse(".x {}"));
            reg.TryGetScopeId("card", out var second);
            Assert.That(first.Value, Is.EqualTo(second.Value));
        }

        [Test]
        public void Two_components_get_different_scope_ids() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"), CssParser.Parse(".x {}"));
            reg.Register("hero", new Element("template"), CssParser.Parse(".x {}"));
            reg.TryGetScopeId("card", out var a);
            reg.TryGetScopeId("hero", out var b);
            Assert.That(a.Value, Is.Not.EqualTo(b.Value));
        }

        [Test]
        public void Clear_removes_stylesheets() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"), CssParser.Parse(".x {}"));
            reg.Clear();
            Assert.That(reg.TryGetStylesheet("card", out _), Is.False);
            Assert.That(reg.TryGetScopeId("card", out _), Is.False);
            Assert.That(reg.AllStylesheets.Count, Is.EqualTo(0));
        }
    }
}
