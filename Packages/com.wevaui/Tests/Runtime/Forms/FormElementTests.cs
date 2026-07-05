using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class FormElementTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        [Test]
        public void CollectFormData_includes_named_text_inputs() {
            var doc = Html("<form><input name=\"u\" value=\"alice\"><input name=\"p\" value=\"x\"></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            var data = form.CollectFormData();
            Assert.That(data["u"], Is.EqualTo("alice"));
            Assert.That(data["p"], Is.EqualTo("x"));
        }

        [Test]
        public void CollectFormData_excludes_disabled_controls() {
            var doc = Html("<form><input name=\"a\" value=\"1\"><input name=\"b\" value=\"2\" disabled></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            var data = form.CollectFormData();
            Assert.That(data.ContainsKey("a"), Is.True);
            Assert.That(data.ContainsKey("b"), Is.False);
        }

        [Test]
        public void CollectFormData_includes_only_checked_checkboxes() {
            var doc = Html("<form><input name=\"k\" type=\"checkbox\" value=\"y\" checked><input name=\"j\" type=\"checkbox\" value=\"y\"></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            var data = form.CollectFormData();
            Assert.That(data.ContainsKey("k"), Is.True);
            Assert.That(data["k"], Is.EqualTo("y"));
            Assert.That(data.ContainsKey("j"), Is.False);
        }

        [Test]
        public void Submit_fires_event_with_collected_data() {
            var doc = Html("<form><input name=\"q\" value=\"hi\"></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            FormSubmitEvent received = null;
            form.Submitted += e => received = e;
            form.Submit();
            Assert.That(received, Is.Not.Null);
            Assert.That(received.Data["q"], Is.EqualTo("hi"));
        }

        [Test]
        public void Reset_restores_default_values_and_checked_state() {
            var doc = Html("<form><input name=\"a\" value=\"orig\"><input name=\"b\" type=\"checkbox\" value=\"1\"></form>");
            var formEl = doc.GetElementsByTagName("form").First();
            var form = new FormElement(formEl);

            var inputs = formEl.Children.OfType<Element>().ToArray();
            inputs[0].SetAttribute("value", "changed");
            inputs[1].SetAttribute("checked", "");

            form.DoReset();

            Assert.That(inputs[0].GetAttribute("value"), Is.EqualTo("orig"));
            Assert.That(inputs[1].HasAttribute("checked"), Is.False);
        }

        [Test]
        public void Submit_captures_submitter_name_value_into_data() {
            var doc = Html("<form><input name=\"u\" value=\"alice\"><button name=\"action\" value=\"save\" type=\"submit\">Save</button></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            var btn = doc.GetElementsByTagName("button").First();
            FormSubmitEvent received = null;
            form.Submitted += e => received = e;
            form.Submit(btn);
            Assert.That(received, Is.Not.Null);
            Assert.That(received.Data["action"], Is.EqualTo("save"));
            Assert.That(received.Data["u"], Is.EqualTo("alice"));
        }

        [Test]
        public void Submit_PreventDefault_is_observable() {
            var doc = Html("<form><input name=\"u\" value=\"x\"></form>");
            var form = new FormElement(doc.GetElementsByTagName("form").First());
            form.Submitted += e => e.PreventDefault();
            bool result = form.Submit();
            Assert.That(result, Is.False);
        }
    }
}
