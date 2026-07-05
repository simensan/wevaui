using NUnit.Framework;
using Weva.Components;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Components.Scoping {
    public class ComponentExpanderScopingTests {
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
            // RegisterAllFromDocument leaves the stylesheet entry empty; re-register
            // with the parsed sheet to attach scope.
            reg.TryGet(tag, out var tpl);
            reg.Register(tag, tpl, CssParser.Parse(css));
            return reg;
        }

        [Test]
        public void Cloned_descendants_get_scope_attribute() {
            var doc = Html("<template id=\"card\"><div class=\"root\"><span>x</span></div></template><card></card>");
            var reg = RegistryWith(doc, "card", ".root {}");
            new ComponentExpander(reg).Expand(doc);
            reg.TryGetScopeId("card", out var id);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var span = (Element)root.Children[0];
            Assert.That(root.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(id.Value));
            Assert.That(span.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(id.Value));
        }

        [Test]
        public void Host_element_gets_host_marker() {
            var doc = Html("<template id=\"card\"><div></div></template><card></card>");
            var reg = RegistryWith(doc, "card", ":host {}");
            new ComponentExpander(reg).Expand(doc);
            reg.TryGetScopeId("card", out var id);

            var card = FindByTag(doc, "card");
            Assert.That(card.GetAttribute(ScopeMarkers.HostAttribute), Is.EqualTo(id.Value));
            Assert.That(card.HasAttribute(ScopeMarkers.ScopeAttribute), Is.False, "host must not carry the descendant scope marker");
        }

        [Test]
        public void Slot_projected_light_dom_does_not_get_scope_attribute() {
            var doc = Html("<template id=\"card\"><div class=\"root\"><slot></slot></div></template><card><p class=\"body\">x</p></card>");
            var reg = RegistryWith(doc, "card", ".root {}");
            new ComponentExpander(reg).Expand(doc);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var p = (Element)root.Children[0];
            Assert.That(p.HasAttribute(ScopeMarkers.ScopeAttribute), Is.False);
        }

        [Test]
        public void Nested_components_use_their_own_scope_for_clones() {
            var doc = Html(
                "<template id=\"outer\"><div class=\"o\"><inner></inner></div></template>" +
                "<template id=\"inner\"><span class=\"i\"></span></template>" +
                "<outer></outer>");
            var reg = RegistryWith(doc, "outer", ".o {}");
            reg.TryGet("inner", out var innerTpl);
            reg.Register("inner", innerTpl, CssParser.Parse(".i {}"));
            new ComponentExpander(reg).Expand(doc);
            reg.TryGetScopeId("outer", out var outerId);
            reg.TryGetScopeId("inner", out var innerId);

            var outer = FindByTag(doc, "outer");
            var div = (Element)outer.Children[0];
            // div was cloned from outer's template body
            Assert.That(div.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(outerId.Value));
            var innerHost = (Element)div.Children[0];
            // innerHost was cloned from outer's template body — carries outer scope
            Assert.That(innerHost.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(outerId.Value));
            // and it became a host for inner — carries inner host marker
            Assert.That(innerHost.GetAttribute(ScopeMarkers.HostAttribute), Is.EqualTo(innerId.Value));
            // The span (clone of inner template) gets the inner scope only — NOT the outer scope.
            var span = (Element)innerHost.Children[0];
            Assert.That(span.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(innerId.Value));
        }

        [Test]
        public void Already_expanded_host_is_not_restamped() {
            var doc = Html("<template id=\"card\"><div></div></template><card></card>");
            var reg = RegistryWith(doc, "card", ":host {}");
            var expander = new ComponentExpander(reg);
            expander.Expand(doc);
            var card = FindByTag(doc, "card");
            int childCount = card.Children.Count;
            expander.Expand(doc);
            Assert.That(card.Children.Count, Is.EqualTo(childCount));
        }

        [Test]
        public void Scope_id_stable_across_two_instances_of_same_component() {
            var doc = Html("<template id=\"card\"><div class=\"root\"></div></template><card></card><card></card>");
            var reg = RegistryWith(doc, "card", ".root {}");
            new ComponentExpander(reg).Expand(doc);
            reg.TryGetScopeId("card", out var id);

            int count = 0;
            foreach (var card in doc.GetElementsByTagName("card")) {
                var root = (Element)card.Children[0];
                Assert.That(root.GetAttribute(ScopeMarkers.ScopeAttribute), Is.EqualTo(id.Value));
                count++;
            }
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Host_keeps_its_original_attributes_plus_host_marker() {
            var doc = Html("<template id=\"card\"><div></div></template><card title=\"hi\" class=\"big\"></card>");
            var reg = RegistryWith(doc, "card", ":host {}");
            new ComponentExpander(reg).Expand(doc);
            reg.TryGetScopeId("card", out var id);

            var card = FindByTag(doc, "card");
            Assert.That(card.GetAttribute("title"), Is.EqualTo("hi"));
            Assert.That(card.GetAttribute("class"), Is.EqualTo("big"));
            Assert.That(card.GetAttribute(ScopeMarkers.HostAttribute), Is.EqualTo(id.Value));
        }

        [Test]
        public void Light_dom_keeps_its_original_attributes_after_projection() {
            var doc = Html("<template id=\"card\"><div class=\"root\"><slot></slot></div></template><card><p class=\"body\" id=\"pid\">x</p></card>");
            var reg = RegistryWith(doc, "card", ".root {}");
            new ComponentExpander(reg).Expand(doc);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            var p = (Element)root.Children[0];
            Assert.That(p.GetAttribute("class"), Is.EqualTo("body"));
            Assert.That(p.GetAttribute("id"), Is.EqualTo("pid"));
            Assert.That(p.HasAttribute(ScopeMarkers.ScopeAttribute), Is.False);
        }

        [Test]
        public void Component_without_stylesheet_does_not_stamp_scope() {
            var doc = Html("<template id=\"card\"><div class=\"root\"></div></template><card></card>");
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            new ComponentExpander(reg).Expand(doc);

            var card = FindByTag(doc, "card");
            var root = (Element)card.Children[0];
            Assert.That(root.HasAttribute(ScopeMarkers.ScopeAttribute), Is.False);
            Assert.That(card.HasAttribute(ScopeMarkers.HostAttribute), Is.False);
        }

        [Test]
        public void Stamping_attribute_fires_mutation_through_invalidation_tracker() {
            var doc = Html("<template id=\"card\"><div class=\"root\"></div></template><card></card>");
            var reg = RegistryWith(doc, "card", ".root {}");
            var tracker = new InvalidationTracker();
            tracker.Attach(doc);
            tracker.Clear();

            new ComponentExpander(reg).Expand(doc);

            // The stamping went through SetAttribute → Mutated event → tracker picked
            // it up. There is at least one Style or Structure dirty marker.
            Assert.That(tracker.HasAny(InvalidationKind.Style | InvalidationKind.Structure), Is.True);
        }
    }
}
