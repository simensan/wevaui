using System.Linq;
using NUnit.Framework;
using Weva.Components;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Components {
    public class ComponentRegistryTests {
        [Test]
        public void Register_and_TryGet_round_trip() {
            var reg = new ComponentRegistry();
            var tpl = new Element("template");
            reg.Register("card", tpl);
            Assert.That(reg.TryGet("card", out var got), Is.True);
            Assert.That(got, Is.SameAs(tpl));
        }

        [Test]
        public void Register_overwrites_existing() {
            var reg = new ComponentRegistry();
            var first = new Element("template");
            var second = new Element("template");
            reg.Register("card", first);
            reg.Register("card", second);
            Assert.That(reg.TryGet("card", out var got), Is.True);
            Assert.That(got, Is.SameAs(second));
        }

        [Test]
        public void RegisterAllFromDocument_walks_template_instances() {
            var doc = HtmlParser.Parse(
                "<template id=\"card\"><div></div></template>" +
                "<template id=\"hero\"><h1></h1></template>");
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            Assert.That(reg.TryGet("card", out _), Is.True);
            Assert.That(reg.TryGet("hero", out _), Is.True);
            Assert.That(reg.Count, Is.EqualTo(2));
        }

        [Test]
        public void Templates_without_id_are_skipped() {
            var doc = HtmlParser.Parse("<template><div></div></template>");
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryGet_returns_false_for_unknown_tag() {
            var reg = new ComponentRegistry();
            Assert.That(reg.TryGet("ghost", out var got), Is.False);
            Assert.That(got, Is.Null);
        }

        [Test]
        public void Tag_lookup_is_case_insensitive() {
            var reg = new ComponentRegistry();
            var tpl = new Element("template");
            reg.Register("Card", tpl);
            Assert.That(reg.TryGet("card", out var a), Is.True);
            Assert.That(reg.TryGet("CARD", out var b), Is.True);
            Assert.That(a, Is.SameAs(tpl));
            Assert.That(b, Is.SameAs(tpl));
        }

        [Test]
        public void Empty_registry_returns_no_names() {
            var reg = new ComponentRegistry();
            Assert.That(reg.RegisteredNames.ToList(), Is.Empty);
            Assert.That(reg.Count, Is.EqualTo(0));
        }

        [Test]
        public void Templates_inside_other_templates_not_auto_registered() {
            var doc = HtmlParser.Parse(
                "<template id=\"outer\">" +
                "  <template id=\"inner\"><div></div></template>" +
                "</template>");
            var reg = new ComponentRegistry();
            reg.RegisterAllFromDocument(doc);
            Assert.That(reg.TryGet("outer", out _), Is.True);
            Assert.That(reg.TryGet("inner", out _), Is.False);
            Assert.That(reg.Count, Is.EqualTo(1));
        }

        [Test]
        public void Contains_returns_correct_membership() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"));
            Assert.That(reg.Contains("card"), Is.True);
            Assert.That(reg.Contains("CARD"), Is.True);
            Assert.That(reg.Contains("absent"), Is.False);
        }

        [Test]
        public void Clear_removes_all_registrations() {
            var reg = new ComponentRegistry();
            reg.Register("card", new Element("template"));
            reg.Register("hero", new Element("template"));
            reg.Clear();
            Assert.That(reg.Count, Is.EqualTo(0));
            Assert.That(reg.TryGet("card", out _), Is.False);
        }
    }
}
