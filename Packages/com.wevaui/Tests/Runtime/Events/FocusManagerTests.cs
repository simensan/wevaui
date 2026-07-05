using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    public class FocusManagerTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        [Test]
        public void Button_is_focusable() {
            var doc = Html("<button id=\"b\">x</button>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("b")), Is.True);
        }

        [Test]
        public void Div_is_not_focusable() {
            var doc = Html("<div id=\"d\"></div>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("d")), Is.False);
        }

        [Test]
        public void Disabled_button_not_focusable() {
            var doc = Html("<button id=\"b\" disabled>x</button>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("b")), Is.False);
        }

        [Test]
        public void Anchor_with_href_is_focusable() {
            var doc = Html("<a id=\"a\" href=\"#\">x</a>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("a")), Is.True);
        }

        [Test]
        public void Anchor_without_href_not_focusable() {
            var doc = Html("<a id=\"a\">x</a>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("a")), Is.False);
        }

        [Test]
        public void Tabindex_negative_one_not_in_tab_order_but_programmatic() {
            var doc = Html("<div id=\"d\" tabindex=\"-1\"></div>");
            var fm = new FocusManager();
            var d = doc.GetElementById("d");
            Assert.That(fm.IsFocusable(d), Is.False);
            Assert.That(fm.IsProgrammaticallyFocusable(d), Is.True);
        }

        [Test]
        public void Tabindex_zero_is_focusable() {
            var doc = Html("<div id=\"d\" tabindex=\"0\"></div>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("d")), Is.True);
        }

        [Test]
        public void Tabindex_positive_is_focusable_with_value() {
            var doc = Html("<div id=\"d\" tabindex=\"3\"></div>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("d")), Is.True);
            Assert.That(fm.TabIndex(doc.GetElementById("d")), Is.EqualTo(3));
        }

        [Test]
        public void Input_select_textarea_focusable() {
            var doc = Html("<input id=\"i\"><select id=\"s\"></select><textarea id=\"t\"></textarea>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("i")), Is.True);
            Assert.That(fm.IsFocusable(doc.GetElementById("s")), Is.True);
            Assert.That(fm.IsFocusable(doc.GetElementById("t")), Is.True);
        }

        [Test]
        public void Tab_order_naturals_in_document_order() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button></div>");
            var fm = new FocusManager();
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var c = doc.GetElementById("c");
            Assert.That(fm.NextFocusable(doc, null, false), Is.SameAs(a));
            Assert.That(fm.NextFocusable(doc, a, false), Is.SameAs(b));
            Assert.That(fm.NextFocusable(doc, b, false), Is.SameAs(c));
        }

        [Test]
        public void Tab_order_positives_before_naturals_ascending() {
            var doc = Html("<div>" +
                "<button id=\"a\"></button>" +
                "<button id=\"b\" tabindex=\"2\"></button>" +
                "<button id=\"c\" tabindex=\"1\"></button>" +
                "<button id=\"d\"></button>" +
                "</div>");
            var fm = new FocusManager();
            var first = fm.NextFocusable(doc, null, false);
            Assert.That(first, Is.SameAs(doc.GetElementById("c")));
            var second = fm.NextFocusable(doc, first, false);
            Assert.That(second, Is.SameAs(doc.GetElementById("b")));
            var third = fm.NextFocusable(doc, second, false);
            Assert.That(third, Is.SameAs(doc.GetElementById("a")));
            var fourth = fm.NextFocusable(doc, third, false);
            Assert.That(fourth, Is.SameAs(doc.GetElementById("d")));
        }

        [Test]
        public void Tab_wraps_from_last_to_first() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button></div>");
            var fm = new FocusManager();
            var b = doc.GetElementById("b");
            var a = doc.GetElementById("a");
            Assert.That(fm.NextFocusable(doc, b, false), Is.SameAs(a));
        }

        [Test]
        public void Reverse_navigation() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button><button id=\"c\"></button></div>");
            var fm = new FocusManager();
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var c = doc.GetElementById("c");
            Assert.That(fm.NextFocusable(doc, c, true), Is.SameAs(b));
            Assert.That(fm.NextFocusable(doc, b, true), Is.SameAs(a));
            Assert.That(fm.NextFocusable(doc, a, true), Is.SameAs(c));
        }

        [Test]
        public void Disabled_skipped_in_tab_order() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\" disabled></button><button id=\"c\"></button></div>");
            var fm = new FocusManager();
            var a = doc.GetElementById("a");
            var c = doc.GetElementById("c");
            Assert.That(fm.NextFocusable(doc, a, false), Is.SameAs(c));
        }

        [Test]
        public void Hidden_skipped_in_tab_order() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"hidden\"></button><button id=\"c\"></button></div>");
            var hiddenSet = new HashSet<string> { "hidden" };
            var fm = new FocusManager {
                IsHidden = e => e.Id != null && hiddenSet.Contains(e.Id)
            };
            var a = doc.GetElementById("a");
            var c = doc.GetElementById("c");
            Assert.That(fm.NextFocusable(doc, a, false), Is.SameAs(c));
        }

        [Test]
        public void TabIndex_returns_minus_one_for_non_focusable() {
            var doc = Html("<div id=\"d\"></div>");
            var fm = new FocusManager();
            Assert.That(fm.TabIndex(doc.GetElementById("d")), Is.EqualTo(-1));
        }

        [Test]
        public void TabIndex_returns_zero_for_natural_focusable() {
            var doc = Html("<button id=\"b\"></button>");
            var fm = new FocusManager();
            Assert.That(fm.TabIndex(doc.GetElementById("b")), Is.EqualTo(0));
        }

        // a11y regression: an author-styled "fake button" (div + role="button" +
        // tabindex="0") must join the keyboard focus order. role="button" alone
        // is metadata only — without tabindex the div remains unfocusable. This
        // documents that behavior so we don't accidentally start treating role
        // as a focus trigger.
        [Test]
        public void Div_with_role_button_and_tabindex_zero_is_focusable() {
            var doc = Html("<div id=\"d\" role=\"button\" tabindex=\"0\" aria-label=\"Close\"></div>");
            var fm = new FocusManager();
            var d = doc.GetElementById("d");
            Assert.That(fm.IsFocusable(d), Is.True);
            Assert.That(fm.TabIndex(d), Is.EqualTo(0));
            // role attribute is preserved on the parsed element
            Assert.That(d.GetAttribute("role"), Is.EqualTo("button"));
            Assert.That(d.GetAttribute("aria-label"), Is.EqualTo("Close"));
        }

        [Test]
        public void Role_alone_does_not_make_element_focusable() {
            var doc = Html("<div id=\"d\" role=\"button\"></div>");
            var fm = new FocusManager();
            Assert.That(fm.IsFocusable(doc.GetElementById("d")), Is.False);
        }
    }
}
