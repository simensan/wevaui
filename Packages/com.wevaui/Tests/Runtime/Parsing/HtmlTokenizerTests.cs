using System.Collections.Generic;
using NUnit.Framework;
using Weva.Parsing;

namespace Weva.Tests.Parsing {
    public class HtmlTokenizerTests {
        static List<HtmlToken> Tokenize(string s) => new HtmlTokenizer(s).Tokenize();

        [Test]
        public void Empty_input_yields_only_eof() {
            var t = Tokenize("");
            Assert.That(t, Has.Count.EqualTo(1));
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Eof));
        }

        [Test]
        public void Plain_text_yields_one_text_token() {
            var t = Tokenize("hello world");
            Assert.That(t, Has.Count.EqualTo(2));
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[0].Text, Is.EqualTo("hello world"));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.Eof));
        }

        [Test]
        public void Whitespace_text_is_preserved() {
            var t = Tokenize("  hi  ");
            Assert.That(t[0].Text, Is.EqualTo("  hi  "));
        }

        [Test]
        public void Single_start_tag() {
            var t = Tokenize("<div>");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[0].Name, Is.EqualTo("div"));
            Assert.That(t[0].SelfClosing, Is.False);
            Assert.That(t[0].Attributes, Has.Count.EqualTo(0));
        }

        [Test]
        public void Tag_name_is_lowercased() {
            var t = Tokenize("<DIV>");
            Assert.That(t[0].Name, Is.EqualTo("div"));
        }

        [Test]
        public void Start_and_end_tag() {
            var t = Tokenize("<div></div>");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.EndTag));
            Assert.That(t[1].Name, Is.EqualTo("div"));
        }

        [Test]
        public void Self_closing_tag() {
            var t = Tokenize("<br/>");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[0].Name, Is.EqualTo("br"));
            Assert.That(t[0].SelfClosing, Is.True);
        }

        [Test]
        public void Self_closing_with_space() {
            var t = Tokenize("<br />");
            Assert.That(t[0].SelfClosing, Is.True);
        }

        [Test]
        public void Self_closing_arbitrary_element() {
            var t = Tokenize("<div />");
            Assert.That(t[0].Name, Is.EqualTo("div"));
            Assert.That(t[0].SelfClosing, Is.True);
        }

        [Test]
        public void Double_quoted_attribute() {
            var t = Tokenize("<div id=\"main\">");
            Assert.That(t[0].Attributes, Has.Count.EqualTo(1));
            Assert.That(t[0].Attributes[0].Name, Is.EqualTo("id"));
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("main"));
        }

        [Test]
        public void Single_quoted_attribute() {
            var t = Tokenize("<div id='main'>");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("main"));
        }

        [Test]
        public void Unquoted_attribute() {
            var t = Tokenize("<div id=main>");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("main"));
        }

        [Test]
        public void Boolean_attribute_has_empty_value() {
            var t = Tokenize("<input disabled>");
            Assert.That(t[0].Attributes[0].Name, Is.EqualTo("disabled"));
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo(""));
        }

        [Test]
        public void Multiple_attributes() {
            var t = Tokenize("<input type=\"text\" name=\"x\" value=\"hi\">");
            Assert.That(t[0].Attributes, Has.Count.EqualTo(3));
            Assert.That(t[0].Attributes[0].Name, Is.EqualTo("type"));
            Assert.That(t[0].Attributes[1].Name, Is.EqualTo("name"));
            Assert.That(t[0].Attributes[2].Name, Is.EqualTo("value"));
        }

        [Test]
        public void Attribute_name_is_lowercased() {
            var t = Tokenize("<div CLASS=\"x\">");
            Assert.That(t[0].Attributes[0].Name, Is.EqualTo("class"));
        }

        [Test]
        public void Attribute_value_preserves_case_and_spaces() {
            var t = Tokenize("<div title=\"Hello World\">");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("Hello World"));
        }

        [Test]
        public void Attributes_separated_by_arbitrary_whitespace() {
            var t = Tokenize("<div   a=\"1\"   b=\"2\"   >");
            Assert.That(t[0].Attributes, Has.Count.EqualTo(2));
        }

        [Test]
        public void Attribute_with_equals_no_space() {
            var t = Tokenize("<div a=\"1\"b=\"2\">");
            Assert.That(t[0].Attributes, Has.Count.EqualTo(2));
            Assert.That(t[0].Attributes[1].Name, Is.EqualTo("b"));
            Assert.That(t[0].Attributes[1].Value, Is.EqualTo("2"));
        }

        [Test]
        public void Special_attribute_names_with_dashes_and_dots() {
            var t = Tokenize("<div data-id=\"1\" on-click=\"go\">");
            Assert.That(t[0].Attributes[0].Name, Is.EqualTo("data-id"));
            Assert.That(t[0].Attributes[1].Name, Is.EqualTo("on-click"));
        }

        [Test]
        public void Nested_tags_emit_in_order() {
            var t = Tokenize("<div><span></span></div>");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[0].Name, Is.EqualTo("div"));
            Assert.That(t[1].Name, Is.EqualTo("span"));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[2].Kind, Is.EqualTo(HtmlTokenKind.EndTag));
            Assert.That(t[2].Name, Is.EqualTo("span"));
            Assert.That(t[3].Kind, Is.EqualTo(HtmlTokenKind.EndTag));
            Assert.That(t[3].Name, Is.EqualTo("div"));
        }

        [Test]
        public void Mixed_text_and_elements() {
            var t = Tokenize("Hello <strong>world</strong>!");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[0].Text, Is.EqualTo("Hello "));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[1].Name, Is.EqualTo("strong"));
            Assert.That(t[2].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[2].Text, Is.EqualTo("world"));
            Assert.That(t[3].Kind, Is.EqualTo(HtmlTokenKind.EndTag));
            Assert.That(t[4].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[4].Text, Is.EqualTo("!"));
        }

        [Test]
        public void Comment_is_emitted() {
            var t = Tokenize("<!-- hello -->");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Comment));
            Assert.That(t[0].Text, Is.EqualTo(" hello "));
        }

        [Test]
        public void Empty_comment() {
            var t = Tokenize("<!---->");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Comment));
            Assert.That(t[0].Text, Is.EqualTo(""));
        }

        [Test]
        public void Comment_with_dashes_inside() {
            var t = Tokenize("<!-- a - b -- c -->");
            Assert.That(t[0].Text, Is.EqualTo(" a - b -- c "));
        }

        [TestCase("<!DOCTYPE html>")]
        [TestCase("<!doctype html>")]
        [TestCase("<!DOCTYPE HTML>")]
        public void Doctype_is_emitted(string input) {
            var t = Tokenize(input);
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.DocType));
        }

        [Test]
        public void Entity_amp_in_text() {
            var t = Tokenize("Tom &amp; Jerry");
            Assert.That(t[0].Text, Is.EqualTo("Tom & Jerry"));
        }

        [TestCase("&lt;", "<")]
        [TestCase("&gt;", ">")]
        [TestCase("&amp;", "&")]
        [TestCase("&quot;", "\"")]
        [TestCase("&apos;", "'")]
        public void Named_entities_resolve(string input, string expected) {
            var t = Tokenize(input);
            Assert.That(t[0].Text, Is.EqualTo(expected));
        }

        [TestCase("&#65;", "A")]
        [TestCase("&#x41;", "A")]
        [TestCase("&#x4A;", "J")]
        [TestCase("&#x2014;", "—")]
        public void Numeric_entities_resolve(string input, string expected) {
            var t = Tokenize(input);
            Assert.That(t[0].Text, Is.EqualTo(expected));
        }

        [Test]
        public void Unknown_named_entity_passes_through_literally() {
            var t = Tokenize("&notreal;");
            Assert.That(t[0].Text, Is.EqualTo("&notreal;"));
        }

        [Test]
        public void Lone_ampersand_is_literal_text() {
            var t = Tokenize("a & b");
            Assert.That(t[0].Text, Is.EqualTo("a & b"));
        }

        [Test]
        public void Entity_in_attribute_value() {
            var t = Tokenize("<a href=\"?a=1&amp;b=2\">");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("?a=1&b=2"));
        }

        [Test]
        public void Entity_in_unquoted_attribute_value() {
            var t = Tokenize("<a x=&amp;>");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("&"));
        }

        [Test]
        public void Tracks_line_and_column_of_tag() {
            var t = Tokenize("hello\n<div>");
            var tag = t[1];
            Assert.That(tag.Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(tag.Line, Is.EqualTo(2));
            Assert.That(tag.Column, Is.EqualTo(1));
        }

        [Test]
        public void Tracks_column_within_line() {
            var t = Tokenize("  <div>");
            var tag = t[1];
            Assert.That(tag.Column, Is.EqualTo(3));
            Assert.That(tag.Line, Is.EqualTo(1));
        }

        [Test]
        public void Unclosed_start_tag_throws() {
            Assert.Throws<HtmlParseException>(() => Tokenize("<div"));
        }

        [Test]
        public void Unclosed_attribute_value_throws() {
            Assert.Throws<HtmlParseException>(() => Tokenize("<div id=\"open"));
        }

        [Test]
        public void Empty_end_tag_throws() {
            Assert.Throws<HtmlParseException>(() => Tokenize("</>"));
        }

        [Test]
        public void Unterminated_comment_throws() {
            Assert.Throws<HtmlParseException>(() => Tokenize("<!-- never closed"));
        }

        [Test]
        public void Slash_without_close_throws() {
            Assert.Throws<HtmlParseException>(() => Tokenize("<div / id=\"x\">"));
        }

        [Test]
        public void Multiple_text_and_tag_alternation() {
            var t = Tokenize("a<b>c</b>d<e>f");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[0].Text, Is.EqualTo("a"));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[2].Text, Is.EqualTo("c"));
            Assert.That(t[3].Kind, Is.EqualTo(HtmlTokenKind.EndTag));
            Assert.That(t[4].Text, Is.EqualTo("d"));
            Assert.That(t[5].Kind, Is.EqualTo(HtmlTokenKind.StartTag));
            Assert.That(t[6].Text, Is.EqualTo("f"));
            Assert.That(t[7].Kind, Is.EqualTo(HtmlTokenKind.Eof));
        }

        [Test]
        public void Attribute_value_with_brackets_and_special_chars_when_quoted() {
            var t = Tokenize("<div title=\"a > b < c & d\">");
            Assert.That(t[0].Attributes[0].Value, Is.EqualTo("a > b < c & d"));
        }

        [Test]
        public void Comment_does_not_terminate_at_single_dash() {
            var t = Tokenize("<!-- not - yet -->after");
            Assert.That(t[0].Kind, Is.EqualTo(HtmlTokenKind.Comment));
            Assert.That(t[0].Text, Is.EqualTo(" not - yet "));
            Assert.That(t[1].Kind, Is.EqualTo(HtmlTokenKind.Text));
            Assert.That(t[1].Text, Is.EqualTo("after"));
        }
    }
}
