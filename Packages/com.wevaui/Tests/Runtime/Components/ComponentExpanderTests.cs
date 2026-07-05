using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Components;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Components {
    public class ComponentExpanderTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static ComponentRegistry RegistryFor(Document doc) {
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            return reg;
        }

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Simple_expansion_inserts_template_clone_and_projects_default_slot() {
            var doc = Html("<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template><card><p>x</p></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var card = FindByTag(doc, "card");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.Children, Has.Count.EqualTo(1));
            var root = (Element)card.Children[0];
            Assert.That(root.TagName, Is.EqualTo("div"));
            Assert.That(root.GetAttribute("class"), Is.EqualTo("card-root"));
            Assert.That(root.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)root.Children[0]).TagName, Is.EqualTo("p"));
        }

        [Test]
        public void Host_element_tag_is_preserved() {
            var doc = Html("<template id=\"card\"><div></div></template><card></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var card = FindByTag(doc, "card");
            Assert.That(card, Is.Not.Null);
            Assert.That(card.TagName, Is.EqualTo("card"));
        }

        [Test]
        public void Host_attributes_are_preserved() {
            var doc = Html("<template id=\"card\"><div></div></template><card title=\"Hello\" class=\"big\"></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var card = FindByTag(doc, "card");
            Assert.That(card.GetAttribute("title"), Is.EqualTo("Hello"));
            Assert.That(card.GetAttribute("class"), Is.EqualTo("big"));
        }

        [Test]
        public void Template_content_is_not_mutated_by_expansion() {
            var doc = Html("<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template><card><p>x</p></card>");
            var template = doc.GetElementById("card");
            var templateBefore = template.Children;
            var firstChildBefore = templateBefore[0];
            int firstChildChildrenCountBefore = ((Element)firstChildBefore).Children.Count;

            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            Assert.That(template.Children, Has.Count.EqualTo(templateBefore.Count));
            var firstChildAfter = template.Children[0];
            Assert.That(firstChildAfter, Is.SameAs(firstChildBefore));
            Assert.That(((Element)firstChildAfter).TagName, Is.EqualTo("div"));
            Assert.That(((Element)firstChildAfter).Children, Has.Count.EqualTo(firstChildChildrenCountBefore));
            // The original slot inside the template body still exists.
            Assert.That(((Element)((Element)firstChildAfter).Children[0]).TagName, Is.EqualTo("slot"));
        }

        [Test]
        public void Recursive_expansion_inserts_nested_component_body() {
            var doc = Html(
                "<template id=\"outer\"><div class=\"outer\"><inner></inner></div></template>" +
                "<template id=\"inner\"><span class=\"inner\"></span></template>" +
                "<outer></outer>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var outer = FindByTag(doc, "outer");
            var div = (Element)outer.Children[0];
            Assert.That(div.GetAttribute("class"), Is.EqualTo("outer"));
            var innerHost = (Element)div.Children[0];
            Assert.That(innerHost.TagName, Is.EqualTo("inner"));
            Assert.That(innerHost.Children, Has.Count.EqualTo(1));
            var span = (Element)innerHost.Children[0];
            Assert.That(span.TagName, Is.EqualTo("span"));
            Assert.That(span.GetAttribute("class"), Is.EqualTo("inner"));
        }

        [Test]
        public void Cycle_between_two_templates_throws_after_depth_limit() {
            var doc = Html(
                "<template id=\"a\"><b></b></template>" +
                "<template id=\"b\"><a></a></template>" +
                "<a></a>");
            var expander = new ComponentExpander(RegistryFor(doc), 4);
            Assert.Throws<ComponentExpansionException>(() => expander.Expand(doc));
        }

        [Test]
        public void Expanding_twice_is_idempotent() {
            var doc = Html("<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template><card><p>x</p></card>");
            var reg = RegistryFor(doc);

            var first = new ComponentExpander(reg);
            first.Expand(doc);

            var card = FindByTag(doc, "card");
            int childCountAfterFirst = card.Children.Count;
            var firstRoot = card.Children[0];

            var second = new ComponentExpander(reg);
            second.Expand(doc);

            Assert.That(card.Children, Has.Count.EqualTo(childCountAfterFirst));
            Assert.That(card.Children[0], Is.SameAs(firstRoot));
        }

        [Test]
        public void Unknown_component_tag_remains_unchanged() {
            var doc = Html("<template id=\"card\"><div></div></template><ghost><p>kept</p></ghost>");
            var expander = new ComponentExpander(RegistryFor(doc));
            Assert.DoesNotThrow(() => expander.Expand(doc));

            var ghost = FindByTag(doc, "ghost");
            Assert.That(ghost, Is.Not.Null);
            Assert.That(ghost.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)ghost.Children[0]).TagName, Is.EqualTo("p"));
        }

        [Test]
        public void Light_dom_children_are_projected_not_template_children() {
            var doc = Html("<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template><card><p>light</p><span>dom</span></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            Assert.That(root.Children, Has.Count.EqualTo(2));
            Assert.That(((Element)root.Children[0]).TagName, Is.EqualTo("p"));
            Assert.That(((Element)root.Children[1]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void Named_slot_routing_works_end_to_end() {
            var doc = Html(
                "<template id=\"layout\">" +
                "<header><slot name=\"head\"></slot></header>" +
                "<main><slot></slot></main>" +
                "<footer><slot name=\"foot\"></slot></footer>" +
                "</template>" +
                "<layout>" +
                "<h1 slot=\"head\">T</h1>" +
                "<p>Body</p>" +
                "<small slot=\"foot\">copy</small>" +
                "</layout>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var layout = FindByTag(doc, "layout");
            var header = FindByTag(layout, "header");
            var main = FindByTag(layout, "main");
            var footer = FindByTag(layout, "footer");

            Assert.That(((Element)header.Children[0]).TagName, Is.EqualTo("h1"));
            Assert.That(((Element)main.Children[0]).TagName, Is.EqualTo("p"));
            Assert.That(((Element)footer.Children[0]).TagName, Is.EqualTo("small"));
        }

        [Test]
        public void Slot_fallback_works_end_to_end() {
            var doc = Html(
                "<template id=\"card\"><div><slot><em>fallback</em></slot></div></template>" +
                "<card></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var card = FindByTag(doc, "card");
            var div = (Element)card.Children[0];
            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)div.Children[0]).TagName, Is.EqualTo("em"));
        }

        [Test]
        public void ExpandSubtree_only_expands_within_provided_subtree() {
            var doc = Html(
                "<template id=\"card\"><div class=\"card-root\"></div></template>" +
                "<section><card></card></section>" +
                "<card></card>");
            var reg = RegistryFor(doc);
            var expander = new ComponentExpander(reg);

            var section = FindByTag(doc, "section");
            expander.ExpandSubtree(section);

            // The card inside the section is expanded.
            var sectionCard = FindByTag(section, "card");
            Assert.That(sectionCard.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)sectionCard.Children[0]).GetAttribute("class"), Is.EqualTo("card-root"));

            // The other card (outside the section) was not expanded.
            // (HtmlParser wraps fragments in <html><body>, so scan the
            // whole tree for a <card> that's not a descendant of <section>.)
            Element outerCard = null;
            foreach (var card in doc.GetElementsByTagName("card")) {
                bool inSection = false;
                for (var n = (Node)card.Parent; n != null; n = n.Parent) {
                    if (ReferenceEquals(n, section)) { inSection = true; break; }
                }
                if (!inSection) { outerCard = card; break; }
            }
            Assert.That(outerCard, Is.Not.Null);
            Assert.That(outerCard.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void After_expansion_cascade_computes_styles_for_slotted_elements() {
            var doc = Html(
                "<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template>" +
                "<card><p class=\"body\">x</p></card>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            var sheet = OriginatedStylesheet.Author(CssParser.Parse(".card-root { color: red; } .body { color: blue; }"));
            var engine = new CascadeEngine(new[] { sheet });

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var p = (Element)root.Children[0];

            var rootStyle = engine.Compute(root);
            var pStyle = engine.Compute(p);
            Assert.That(rootStyle.Get("color"), Is.EqualTo("red"));
            Assert.That(pStyle.Get("color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Mutations_during_expansion_fire_bubbling_events_to_invalidation_tracker() {
            var doc = Html("<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template><card><p>x</p></card>");
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();

            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            Assert.That(tracker.DirtyCount, Is.GreaterThan(0));
            Assert.That(tracker.HasAny(InvalidationKind.Structure), Is.True);
        }

        [Test]
        public void Self_referential_template_throws_after_depth_limit() {
            var doc = Html(
                "<template id=\"loop\"><loop></loop></template>" +
                "<loop></loop>");
            var expander = new ComponentExpander(RegistryFor(doc), 3);
            Assert.Throws<ComponentExpansionException>(() => expander.Expand(doc));
        }

        [Test]
        public void Expander_does_not_descend_into_template_definitions() {
            var doc = Html(
                "<template id=\"card\"><div></div></template>" +
                "<template id=\"holder\"><card></card></template>" +
                "<holder></holder>");
            var expander = new ComponentExpander(RegistryFor(doc));
            expander.Expand(doc);

            // The <card> reference inside the holder template should expand once we
            // cloned it into a holder host. But the original template body must remain
            // a literal <card></card> node with no children.
            var template = doc.GetElementById("holder");
            var literalCard = (Element)template.Children[0];
            Assert.That(literalCard.TagName, Is.EqualTo("card"));
            Assert.That(literalCard.Children, Has.Count.EqualTo(0));

            // The instantiated holder did expand its inner card.
            var holderHost = FindByTag(doc, "holder");
            var clonedCard = (Element)holderHost.Children[0];
            Assert.That(clonedCard.TagName, Is.EqualTo("card"));
            Assert.That(clonedCard.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)clonedCard.Children[0]).TagName, Is.EqualTo("div"));
        }
    }
}
