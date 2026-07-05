using NUnit.Framework;
using Weva.Components;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Components {
    public class ComponentIntegrationTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        static Element FindByClass(Node n, string cls) {
            if (n is Element e && e.GetAttribute("class") == cls) return e;
            foreach (var c in n.Children) {
                var f = FindByClass(c, cls);
                if (f != null) return f;
            }
            return null;
        }

        static Document Expand(string source) {
            var doc = Html(source);
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            new ComponentExpander(reg).Expand(doc);
            return doc;
        }

        [Test]
        public void Card_with_title_and_body_slots() {
            var doc = Expand(
                "<template id=\"card\">" +
                "  <article class=\"card\">" +
                "    <header class=\"card-title\"><slot name=\"title\"></slot></header>" +
                "    <section class=\"card-body\"><slot></slot></section>" +
                "  </article>" +
                "</template>" +
                "<card>" +
                "<h2 slot=\"title\">My Title</h2>" +
                "<p>Body text</p>" +
                "</card>");

            var card = FindByTag(doc, "card");
            var article = FindByClass(card, "card");
            Assert.That(article, Is.Not.Null);
            var title = FindByClass(article, "card-title");
            var body = FindByClass(article, "card-body");
            Assert.That(((Element)title.Children[0]).TagName, Is.EqualTo("h2"));
            Assert.That(((TextNode)((Element)title.Children[0]).Children[0]).Data, Is.EqualTo("My Title"));
            Assert.That(((Element)body.Children[0]).TagName, Is.EqualTo("p"));
        }

        [Test]
        public void Layout_with_three_named_slots() {
            var doc = Expand(
                "<template id=\"layout\">" +
                "<header><slot name=\"header\"></slot></header>" +
                "<main><slot name=\"main\"></slot></main>" +
                "<footer><slot name=\"footer\"></slot></footer>" +
                "</template>" +
                "<layout>" +
                "<h1 slot=\"header\">Top</h1>" +
                "<div slot=\"main\">Middle</div>" +
                "<small slot=\"footer\">Bottom</small>" +
                "</layout>");

            var layout = FindByTag(doc, "layout");
            var header = FindByTag(layout, "header");
            var main = FindByTag(layout, "main");
            var footer = FindByTag(layout, "footer");

            Assert.That(((Element)header.Children[0]).TagName, Is.EqualTo("h1"));
            Assert.That(((Element)main.Children[0]).TagName, Is.EqualTo("div"));
            Assert.That(((Element)footer.Children[0]).TagName, Is.EqualTo("small"));
        }

        [Test]
        public void Nested_components_app_card_button() {
            var doc = Expand(
                "<template id=\"app\"><main class=\"app\"><slot></slot></main></template>" +
                "<template id=\"card\"><article class=\"card-root\"><slot></slot></article></template>" +
                "<template id=\"button\"><button class=\"btn\"><slot></slot></button></template>" +
                "<app><card><button>Click me</button></card></app>");

            var app = FindByTag(doc, "app");
            var appMain = (Element)app.Children[0];
            Assert.That(appMain.GetAttribute("class"), Is.EqualTo("app"));

            var card = (Element)appMain.Children[0];
            Assert.That(card.TagName, Is.EqualTo("card"));
            var article = (Element)card.Children[0];
            Assert.That(article.GetAttribute("class"), Is.EqualTo("card-root"));

            var btnHost = (Element)article.Children[0];
            Assert.That(btnHost.TagName, Is.EqualTo("button"));
            // Inner clone produced by the template — the host's only child is the cloned <button>.
            var btnClone = (Element)btnHost.Children[0];
            Assert.That(btnClone.TagName, Is.EqualTo("button"));
            Assert.That(btnClone.GetAttribute("class"), Is.EqualTo("btn"));
            Assert.That(((TextNode)btnClone.Children[0]).Data, Is.EqualTo("Click me"));
        }

        [Test]
        public void External_css_selectors_resolve_against_host_and_template_classes() {
            var doc = Expand(
                "<template id=\"card\"><div class=\"card-root\"><slot></slot></div></template>" +
                "<card title=\"Hello\"><p>Body</p></card>");

            var sheet = OriginatedStylesheet.Author(CssParser.Parse(
                ".card-root { background-color: white; padding: 8px; } " +
                "card[title] { color: navy; }"));
            var engine = new CascadeEngine(new[] { sheet });

            var host = FindByTag(doc, "card");
            var root = (Element)host.Children[0];

            var hostStyle = engine.Compute(host);
            Assert.That(hostStyle.Get("color"), Is.EqualTo("navy"));

            var rootStyle = engine.Compute(root);
            Assert.That(rootStyle.Get("background-color"), Is.EqualTo("white"));
        }
    }
}
