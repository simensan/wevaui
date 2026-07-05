using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Binding {
    public class UIElementBinderTests {
        public class Controller {
            [UIElement("start-button")] public Element StartButton;
            [UIElement("score-label")] public Element ScoreLabel;
            [UIElement("missing")] public Element Missing;
            public Element NotMarked;
        }

        public class WrongTypeController {
            [UIElement("the-id")] public string ShouldBeElement;
        }

        public class PrivateFieldController {
            [UIElement("priv")] Element priv;
            public Element ReadPriv() => priv;
        }

        public class BaseController {
            [UIElement("from-base")] public Element FromBase;
        }

        public class DerivedController : BaseController {
            [UIElement("from-derived")] public Element FromDerived;
        }

        [Test]
        public void Populates_field_from_element_id() {
            var doc = HtmlParser.Parse("<button id=\"start-button\">go</button>");
            var c = new Controller();
            var warnings = UIElementBinder.Populate(c, doc);
            Assert.That(c.StartButton, Is.Not.Null);
            Assert.That(c.StartButton.GetAttribute("id"), Is.EqualTo("start-button"));
        }

        [Test]
        public void Multiple_UIElement_fields_populated() {
            var doc = HtmlParser.Parse(
                "<div><button id=\"start-button\">x</button><span id=\"score-label\">0</span></div>");
            var c = new Controller();
            UIElementBinder.Populate(c, doc);
            Assert.That(c.StartButton, Is.Not.Null);
            Assert.That(c.ScoreLabel, Is.Not.Null);
        }

        [Test]
        public void Missing_id_leaves_field_null_and_records_warning() {
            var doc = HtmlParser.Parse("<button id=\"start-button\">go</button>");
            var c = new Controller();
            var warnings = UIElementBinder.Populate(c, doc);
            Assert.That(c.Missing, Is.Null);
            bool sawWarning = false;
            foreach (var w in warnings) {
                if (w.Contains("missing")) sawWarning = true;
            }
            Assert.That(sawWarning, Is.True);
        }

        [Test]
        public void Field_without_attribute_is_left_alone() {
            var doc = HtmlParser.Parse("<button id=\"start-button\">x</button>");
            var c = new Controller();
            UIElementBinder.Populate(c, doc);
            Assert.That(c.NotMarked, Is.Null);
        }

        [Test]
        public void Private_marked_field_populated() {
            var doc = HtmlParser.Parse("<div id=\"priv\"></div>");
            var c = new PrivateFieldController();
            UIElementBinder.Populate(c, doc);
            Assert.That(c.ReadPriv(), Is.Not.Null);
        }

        [Test]
        public void Inherited_field_populated() {
            var doc = HtmlParser.Parse("<div><div id=\"from-base\"></div><div id=\"from-derived\"></div></div>");
            var c = new DerivedController();
            UIElementBinder.Populate(c, doc);
            Assert.That(c.FromBase, Is.Not.Null);
            Assert.That(c.FromDerived, Is.Not.Null);
        }

        [Test]
        public void Field_type_mismatch_records_warning_and_leaves_null() {
            var doc = HtmlParser.Parse("<div id=\"the-id\"></div>");
            var c = new WrongTypeController();
            var warnings = UIElementBinder.Populate(c, doc);
            Assert.That(c.ShouldBeElement, Is.Null);
            bool sawWarning = false;
            foreach (var w in warnings) {
                if (w.Contains("the-id") && w.Contains("String")) sawWarning = true;
            }
            Assert.That(sawWarning, Is.True);
        }

        [Test]
        public void All_missing_ids_collect_warnings() {
            var doc = HtmlParser.Parse("<div></div>");
            var c = new Controller();
            var warnings = UIElementBinder.Populate(c, doc);
            Assert.That(warnings.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(c.StartButton, Is.Null);
            Assert.That(c.ScoreLabel, Is.Null);
            Assert.That(c.Missing, Is.Null);
        }
    }
}
