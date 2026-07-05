using System.Linq;
using NUnit.Framework;
using Weva.Components;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Components.Scoping {
    public class ScopedStylesIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        static ComponentRegistry RegistryWith(Document doc, string tag, string css) {
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            reg.TryGet(tag, out var tpl);
            reg.Register(tag, tpl, CssParser.Parse(css));
            return reg;
        }

        [Test]
        public void Component_rule_matches_inside_component_only() {
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"><p class=\"foo\">in</p></div></template>" +
                "<card></card>" +
                "<p class=\"foo\">out</p>");
            var reg = RegistryWith(doc, "card", ".foo { color: red; }");
            new ComponentExpander(reg).Expand(doc);

            var engine = new CascadeEngine(ComponentStyleIntegration.RewrittenStylesheets(reg));
            var card = FindByTag(doc, "card");
            var inside = FindByTag(card, "p");
            // outside p — must NOT be a descendant of card. Walk the whole
            // tree (HtmlParser wraps fragments in synthetic <html><body>, so
            // the sibling <p> isn't a direct child of Document anymore).
            Element outside = null;
            foreach (var p in doc.GetElementsByTagName("p")) {
                bool inCard = false;
                for (var n = (Node)p.Parent; n != null; n = n.Parent) {
                    if (n is Element pe && pe.TagName == "card") { inCard = true; break; }
                }
                if (!inCard) { outside = p; break; }
            }
            Assert.That(inside, Is.Not.Null);
            Assert.That(outside, Is.Not.Null);
            Assert.That(engine.Compute(inside).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(outside).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Host_rule_matches_host_element_only() {
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"></div></template>" +
                "<card></card>");
            var reg = RegistryWith(doc, "card", ":host { color: green; } .root { color: blue; }");
            new ComponentExpander(reg).Expand(doc);

            var engine = new CascadeEngine(ComponentStyleIntegration.RewrittenStylesheets(reg));
            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            Assert.That(engine.Compute(card).Get("color"), Is.EqualTo("green"));
            Assert.That(engine.Compute(root).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Slot_projected_children_not_matched_by_component_rule() {
            // .body inside the component would match .body author rule but NOT the
            // component's scoped .body rule.
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"><slot></slot></div></template>" +
                "<card><p class=\"body\">x</p></card>");
            var reg = RegistryWith(doc, "card", ".body { color: red; }");
            new ComponentExpander(reg).Expand(doc);

            var component = ComponentStyleIntegration.RewrittenStylesheets(reg).ToList();
            var author = OriginatedStylesheet.Author(CssParser.Parse(".body { color: blue; }"));
            var allSheets = component.Concat(new[] { author });
            var engine = new CascadeEngine(allSheets);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var p = (Element)root.Children[0];
            // p was light-dom — author .body rule matches; component .body rule must not.
            Assert.That(engine.Compute(p).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Two_instances_of_same_component_share_scope_id() {
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"><p class=\"foo\">x</p></div></template>" +
                "<card></card><card></card>");
            var reg = RegistryWith(doc, "card", ".foo { color: red; }");
            new ComponentExpander(reg).Expand(doc);

            var engine = new CascadeEngine(ComponentStyleIntegration.RewrittenStylesheets(reg));

            int matches = 0;
            foreach (var card in doc.GetElementsByTagName("card")) {
                var p = FindByTag(card, "p");
                if (engine.Compute(p).Get("color") == "red") matches++;
            }
            Assert.That(matches, Is.EqualTo(2));
        }

        [Test]
        public void Two_components_with_same_class_only_match_their_own_subtrees() {
            var doc = Html(
                "<template id=\"a\"><div class=\"foo\"></div></template>" +
                "<template id=\"b\"><div class=\"foo\"></div></template>" +
                "<a></a><b></b>");
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            reg.TryGet("a", out var aTpl);
            reg.TryGet("b", out var bTpl);
            reg.Register("a", aTpl, CssParser.Parse(".foo { color: red; }"));
            reg.Register("b", bTpl, CssParser.Parse(".foo { color: blue; }"));
            new ComponentExpander(reg).Expand(doc);

            var engine = new CascadeEngine(ComponentStyleIntegration.RewrittenStylesheets(reg));
            var a = FindByTag(doc, "a");
            var b = FindByTag(doc, "b");
            var aFoo = (Element)a.Children[0];
            var bFoo = (Element)b.Children[0];
            Assert.That(engine.Compute(aFoo).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(bFoo).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void End_to_end_register_expand_cascade_compute() {
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"><p>x</p></div></template>" +
                "<card></card>");
            var reg = RegistryWith(doc, "card", ".root { color: red; padding-left: 4px; } p { color: blue; }");
            new ComponentExpander(reg).Expand(doc);

            var engine = new CascadeEngine(ComponentStyleIntegration.RewrittenStylesheets(reg));
            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var p = (Element)root.Children[0];
            Assert.That(engine.Compute(root).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(root).Get("padding-left"), Is.EqualTo("4px"));
            Assert.That(engine.Compute(p).Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Outside_class_matches_dont_apply_inside_a_components_template_clones() {
            // Author writes .root globally — the COMPONENT TEMPLATE clone has the same
            // class, but only the component's scoped rule wins because the author
            // rule has lower specificity (no attribute selector). Both still match;
            // we verify the scoped rule wins via specificity (attribute selector).
            var doc = Html(
                "<template id=\"card\"><div class=\"root\"></div></template>" +
                "<card></card>");
            var reg = RegistryWith(doc, "card", ".root { color: red; }");
            new ComponentExpander(reg).Expand(doc);

            var component = ComponentStyleIntegration.RewrittenStylesheets(reg).ToList();
            var author = OriginatedStylesheet.Author(CssParser.Parse(".root { color: blue; }"));
            // Author first so component (with attribute selector → higher specificity)
            // wins on specificity, regardless of source order.
            var sheets = new[] { author }.Concat(component);
            var engine = new CascadeEngine(sheets);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            Assert.That(engine.Compute(root).Get("color"), Is.EqualTo("red"));
        }
    }
}
