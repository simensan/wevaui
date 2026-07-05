using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Forms;
using Weva.Parsing;

namespace Weva.Tests.Forms {
    public class SelectElementTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Element Tag(Document d, string t) => d.GetElementsByTagName(t).First();

        [Test]
        public void Options_enumerates_all_option_children() {
            var doc = Html("<select><option value=\"a\">A</option><option value=\"b\">B</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            var values = sel.Options.Select(o => o.Value).ToArray();
            Assert.That(values, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void SelectedOption_follows_selected_attribute() {
            var doc = Html("<select><option value=\"a\">A</option><option value=\"b\" selected>B</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            Assert.That(sel.SelectedOption.Value, Is.EqualTo("b"));
        }

        [Test]
        public void Single_select_first_option_default_when_none_selected() {
            var doc = Html("<select><option value=\"a\">A</option><option value=\"b\">B</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            Assert.That(sel.SelectedOption.Value, Is.EqualTo("a"));
        }

        [Test]
        public void Setting_Value_marks_matching_option_selected_only() {
            var doc = Html("<select><option value=\"a\">A</option><option value=\"b\">B</option><option value=\"c\">C</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            sel.Value = "b";
            var selected = sel.Options.Where(o => o.Selected).Select(o => o.Value).ToArray();
            Assert.That(selected, Is.EqualTo(new[] { "b" }));
            Assert.That(sel.Value, Is.EqualTo("b"));
        }

        [Test]
        public void ClearSelection_unselects_all_options() {
            var doc = Html("<select><option value=\"a\" selected>A</option><option value=\"b\" selected>B</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            sel.ClearSelection();
            Assert.That(sel.Options.All(o => !o.Selected), Is.True);
        }

        [Test]
        public void Multiple_attribute_allows_multi_selection() {
            var doc = Html("<select multiple><option value=\"a\" selected>A</option><option value=\"b\" selected>B</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            Assert.That(sel.Multiple, Is.True);
            var sels = sel.SelectedOptions.Select(o => o.Value).ToArray();
            Assert.That(sels, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Option_value_falls_back_to_inner_text_when_no_value_attr() {
            var doc = Html("<select><option>Apple</option></select>");
            var sel = new SelectElement(Tag(doc, "select"));
            Assert.That(sel.Options.First().Value, Is.EqualTo("Apple"));
        }
    }
}
