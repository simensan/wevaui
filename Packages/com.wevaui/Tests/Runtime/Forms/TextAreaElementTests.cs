using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class TextAreaElementTests {
        static Element NewTextArea() => new Element("textarea");

        [Test]
        public void Wrapping_non_textarea_throws() {
            Assert.Throws<System.ArgumentException>(() => new TextAreaElement(new Element("div")));
        }

        [Test]
        public void Value_collects_initial_content_from_TextNode_child() {
            var e = NewTextArea();
            e.AppendChild(new TextNode("hello"));
            var ta = new TextAreaElement(e);
            Assert.That(ta.Value, Is.EqualTo("hello"));
        }

        [Test]
        public void Value_returns_empty_string_when_textarea_is_empty() {
            var ta = new TextAreaElement(NewTextArea());
            Assert.That(ta.Value, Is.EqualTo(""));
        }

        [Test]
        public void Value_collects_mixed_text_and_element_children_recursively() {
            // <textarea>hello<b>world</b>foo</textarea> — per HTML spec the
            // text-equivalent content is the concatenation of all descendant
            // text nodes in document order: "helloworldfoo".
            var e = NewTextArea();
            e.AppendChild(new TextNode("hello"));
            var b = new Element("b");
            b.AppendChild(new TextNode("world"));
            e.AppendChild(b);
            e.AppendChild(new TextNode("foo"));
            var ta = new TextAreaElement(e);
            Assert.That(ta.Value, Is.EqualTo("helloworldfoo"));
        }

        [Test]
        public void Value_attribute_overrides_child_text_content() {
            // When a value= attribute is present, child text is ignored.
            var e = NewTextArea();
            e.AppendChild(new TextNode("ignored-child-text"));
            e.SetAttribute("value", "from-attribute");
            var ta = new TextAreaElement(e);
            Assert.That(ta.Value, Is.EqualTo("from-attribute"));
        }

        [Test]
        public void Setting_Value_reflects_on_attribute() {
            var e = NewTextArea();
            var ta = new TextAreaElement(e);
            ta.Value = "typed-in";
            Assert.That(e.GetAttribute("value"), Is.EqualTo("typed-in"));
            Assert.That(ta.Value, Is.EqualTo("typed-in"));
        }

        [Test]
        public void Setting_Value_overrides_pre_existing_child_text() {
            var e = NewTextArea();
            e.AppendChild(new TextNode("initial"));
            var ta = new TextAreaElement(e);
            Assert.That(ta.Value, Is.EqualTo("initial"));
            ta.Value = "replaced";
            Assert.That(ta.Value, Is.EqualTo("replaced"));
            Assert.That(e.GetAttribute("value"), Is.EqualTo("replaced"));
        }

        [Test]
        public void Value_attribute_set_to_empty_string_overrides_child_text() {
            var e = NewTextArea();
            e.AppendChild(new TextNode("present"));
            var ta = new TextAreaElement(e);
            ta.Value = "";
            Assert.That(ta.Value, Is.EqualTo(""));
        }

        [Test]
        public void Setting_null_Value_writes_empty_string_attribute() {
            var ta = new TextAreaElement(NewTextArea());
            ta.Value = null;
            Assert.That(ta.Element.GetAttribute("value"), Is.EqualTo(""));
            Assert.That(ta.Value, Is.EqualTo(""));
        }

        [Test]
        public void Placeholder_round_trips() {
            var ta = new TextAreaElement(NewTextArea());
            ta.Placeholder = "Type here...";
            Assert.That(ta.Element.GetAttribute("placeholder"), Is.EqualTo("Type here..."));
            Assert.That(ta.Placeholder, Is.EqualTo("Type here..."));
        }

        [Test]
        public void Name_round_trips() {
            var ta = new TextAreaElement(NewTextArea());
            ta.Name = "comments";
            Assert.That(ta.Name, Is.EqualTo("comments"));
            Assert.That(ta.Element.GetAttribute("name"), Is.EqualTo("comments"));
        }

        [Test]
        public void Disabled_toggles_attribute_presence() {
            var ta = new TextAreaElement(NewTextArea());
            Assert.That(ta.Disabled, Is.False);
            ta.Disabled = true;
            Assert.That(ta.Element.HasAttribute("disabled"), Is.True);
            ta.Disabled = false;
            Assert.That(ta.Element.HasAttribute("disabled"), Is.False);
        }

        [Test]
        public void ReadOnly_toggles_attribute_presence() {
            var ta = new TextAreaElement(NewTextArea());
            ta.ReadOnly = true;
            Assert.That(ta.Element.HasAttribute("readonly"), Is.True);
            ta.ReadOnly = false;
            Assert.That(ta.Element.HasAttribute("readonly"), Is.False);
        }

        [Test]
        public void Required_toggles_attribute_presence() {
            var ta = new TextAreaElement(NewTextArea());
            ta.Required = true;
            Assert.That(ta.Required, Is.True);
            ta.Required = false;
            Assert.That(ta.Required, Is.False);
        }

        [Test]
        public void Rows_and_Cols_parse_integer_attributes() {
            var e = NewTextArea();
            e.SetAttribute("rows", "8");
            e.SetAttribute("cols", "40");
            var ta = new TextAreaElement(e);
            Assert.That(ta.Rows, Is.EqualTo(8));
            Assert.That(ta.Cols, Is.EqualTo(40));
        }

        [Test]
        public void Rows_returns_null_when_attribute_missing_or_invalid() {
            var e = NewTextArea();
            var ta = new TextAreaElement(e);
            Assert.That(ta.Rows, Is.Null);
            e.SetAttribute("rows", "not-a-number");
            Assert.That(ta.Rows, Is.Null);
        }

        [Test]
        public void Value_via_HtmlParser_collects_inner_text() {
            var doc = HtmlParser.Parse("<textarea id=\"t\">parsed content</textarea>");
            var el = doc.GetElementById("t");
            var ta = new TextAreaElement(el);
            Assert.That(ta.Value, Is.EqualTo("parsed content"));
        }
    }
}
