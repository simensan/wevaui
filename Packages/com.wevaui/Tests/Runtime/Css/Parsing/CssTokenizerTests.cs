using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css;

namespace Weva.Tests.Css.Parsing {
    public class CssTokenizerTests {
        static List<CssToken> Tokenize(string s) => new CssTokenizer(s).Tokenize();
        static List<CssToken> Strip(string s) =>
            new CssTokenizer(s).Tokenize().Where(t => t.Kind != CssTokenKind.Whitespace).ToList();

        [Test]
        public void Empty_input_yields_only_eof() {
            var t = Tokenize("");
            Assert.That(t, Has.Count.EqualTo(1));
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Eof));
        }

        [Test]
        public void Whitespace_only_yields_one_whitespace_then_eof() {
            var t = Tokenize("   \t\n  ");
            Assert.That(t, Has.Count.EqualTo(2));
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Whitespace));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Eof));
        }

        [Test]
        public void Whitespace_runs_collapse_into_single_token() {
            var t = Tokenize("a    \n\t b");
            Assert.That(t.Count(x => x.Kind == CssTokenKind.Whitespace), Is.EqualTo(1));
        }

        [Test]
        public void Single_ident() {
            var t = Strip("color");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("color"));
        }

        [Test]
        public void Ident_with_hyphen() {
            var t = Strip("font-size");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("font-size"));
        }

        [Test]
        public void Custom_property_ident() {
            var t = Strip("--my-var");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("--my-var"));
        }

        [TestCase("rgb")]
        [TestCase("var")]
        [TestCase("linear-gradient")]
        [TestCase("calc")]
        public void Function_tokens(string name) {
            var t = Strip(name + "(");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Function));
            Assert.That(t[0].Text, Is.EqualTo(name));
        }

        [TestCase("@media", "media")]
        [TestCase("@keyframes", "keyframes")]
        [TestCase("@import", "import")]
        [TestCase("@-custom-thing", "-custom-thing")]
        public void At_keyword(string input, string expected) {
            var t = Strip(input);
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.AtKeyword));
            Assert.That(t[0].Text, Is.EqualTo(expected));
        }

        [TestCase("#fff", "fff")]
        [TestCase("#ffffff", "ffffff")]
        [TestCase("#abc123", "abc123")]
        public void Hash_token(string input, string expected) {
            var t = Strip(input);
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Hash));
            Assert.That(t[0].Text, Is.EqualTo(expected));
        }

        [Test]
        public void Hash_with_no_name_chars_is_delim() {
            var t = Strip("# foo");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("#"));
        }

        [Test]
        public void Double_quoted_string() {
            var t = Strip("\"hello world\"");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.String));
            Assert.That(t[0].Text, Is.EqualTo("hello world"));
        }

        [Test]
        public void Single_quoted_string() {
            var t = Strip("'hello world'");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.String));
            Assert.That(t[0].Text, Is.EqualTo("hello world"));
        }

        [Test]
        public void String_with_escaped_double_quote() {
            var t = Strip("\"a\\\"b\"");
            Assert.That(t[0].Text, Is.EqualTo("a\"b"));
        }

        [Test]
        public void String_with_escaped_single_quote() {
            var t = Strip("'a\\'b'");
            Assert.That(t[0].Text, Is.EqualTo("a'b"));
        }

        [Test]
        public void String_with_escaped_newline_char() {
            var t = Strip("\"a\\nb\"");
            Assert.That(t[0].Text, Is.EqualTo("a\nb"));
        }

        [TestCase("0", 0.0)]
        [TestCase("42", 42.0)]
        [TestCase("1.5", 1.5)]
        [TestCase(".5", 0.5)]
        [TestCase("10.", 10.0)]
        [TestCase("-5", -5.0)]
        [TestCase("+5", 5.0)]
        [TestCase("1e2", 100.0)]
        [TestCase("1.5e1", 15.0)]
        [TestCase("-1.5e-1", -0.15)]
        public void Number_token(string input, double expected) {
            var t = Strip(input);
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Number));
            Assert.That(t[0].Number, Is.EqualTo(expected).Within(1e-9));
            Assert.That(t[0].Text, Is.EqualTo(input));
        }

        [TestCase("100%", 100.0)]
        [TestCase("50.5%", 50.5)]
        [TestCase("-25%", -25.0)]
        public void Percentage_token(string input, double expected) {
            var t = Strip(input);
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Percentage));
            Assert.That(t[0].Number, Is.EqualTo(expected).Within(1e-9));
            Assert.That(t[0].Unit, Is.EqualTo("%"));
        }

        [TestCase("12px", 12.0, "px")]
        [TestCase("1.5em", 1.5, "em")]
        [TestCase("2rem", 2.0, "rem")]
        [TestCase("50vh", 50.0, "vh")]
        [TestCase("-10pt", -10.0, "pt")]
        [TestCase("0deg", 0.0, "deg")]
        public void Dimension_token(string input, double value, string unit) {
            var t = Strip(input);
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Dimension));
            Assert.That(t[0].Number, Is.EqualTo(value).Within(1e-9));
            Assert.That(t[0].Unit, Is.EqualTo(unit));
        }

        [Test]
        public void Url_function_with_unquoted_argument() {
            var t = Strip("url(image.png)");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Url));
            Assert.That(t[0].Text, Is.EqualTo("image.png"));
        }

        [Test]
        public void Url_function_with_whitespace_around_argument() {
            var t = Strip("url(  image.png  )");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Url));
            Assert.That(t[0].Text, Is.EqualTo("image.png"));
        }

        [Test]
        public void Url_function_with_quoted_argument_emits_function_token() {
            var t = Strip("url(\"image.png\")");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Function));
            Assert.That(t[0].Text, Is.EqualTo("url"));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.String));
            Assert.That(t[1].Text, Is.EqualTo("image.png"));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.RParen));
        }

        [Test]
        public void Comma_colon_semicolon() {
            var t = Strip(", : ;");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Comma));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Colon));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.Semicolon));
        }

        [Test]
        public void Braces_parens_brackets() {
            var t = Strip("{}()[]");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.LBrace));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.RBrace));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.LParen));
            Assert.That(t[3].Kind, Is.EqualTo(CssTokenKind.RParen));
            Assert.That(t[4].Kind, Is.EqualTo(CssTokenKind.LBracket));
            Assert.That(t[5].Kind, Is.EqualTo(CssTokenKind.RBracket));
        }

        [Test]
        public void Delim_for_unrecognized_chars() {
            var t = Strip("&");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("&"));
        }

        [Test]
        public void Bang_is_delim() {
            var t = Strip("!");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("!"));
        }

        [Test]
        public void Comment_is_stripped() {
            var t = Strip("/* a comment */color");
            Assert.That(t, Has.Count.EqualTo(2));
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("color"));
        }

        [Test]
        public void Comment_does_not_nest_first_close_wins() {
            var t = Strip("/* a /* nested */ b */");
            Assert.That(t.Count(x => x.Kind == CssTokenKind.Ident), Is.EqualTo(1));
            Assert.That(t.First(x => x.Kind == CssTokenKind.Ident).Text, Is.EqualTo("b"));
        }

        [Test]
        public void Comment_with_internal_star_does_not_terminate() {
            var t = Strip("/* a * b */color");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("color"));
        }

        [Test]
        public void Cdo_and_cdc_are_skipped() {
            var t = Strip("<!-- color -->");
            Assert.That(t.Count, Is.EqualTo(2));
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("color"));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Eof));
        }

        [Test]
        public void Tracks_line_and_column_single_line() {
            var t = Strip("  color");
            Assert.That(t[0].Line, Is.EqualTo(1));
            Assert.That(t[0].Column, Is.EqualTo(3));
        }

        [Test]
        public void Tracks_line_and_column_multi_line() {
            var t = Strip("a\nb\n  c");
            Assert.That(t[0].Line, Is.EqualTo(1));
            Assert.That(t[0].Column, Is.EqualTo(1));
            Assert.That(t[1].Line, Is.EqualTo(2));
            Assert.That(t[1].Column, Is.EqualTo(1));
            Assert.That(t[2].Line, Is.EqualTo(3));
            Assert.That(t[2].Column, Is.EqualTo(3));
        }

        [Test]
        public void Tracks_column_after_comment() {
            var t = Strip("/* x */ y");
            var ident = t.First(x => x.Kind == CssTokenKind.Ident);
            Assert.That(ident.Line, Is.EqualTo(1));
            Assert.That(ident.Column, Is.EqualTo(9));
        }

        [Test]
        public void Unterminated_string_throws() {
            Assert.Throws<CssParseException>(() => Tokenize("\"open"));
        }

        [Test]
        public void Unterminated_string_at_newline_throws() {
            Assert.Throws<CssParseException>(() => Tokenize("\"open\nclose\""));
        }

        [Test]
        public void Unterminated_comment_throws() {
            Assert.Throws<CssParseException>(() => Tokenize("/* never ends"));
        }

        [Test]
        public void Selector_tokens() {
            var t = Strip(".foo > #bar");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("."));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[1].Text, Is.EqualTo("foo"));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[2].Text, Is.EqualTo(">"));
            Assert.That(t[3].Kind, Is.EqualTo(CssTokenKind.Hash));
            Assert.That(t[3].Text, Is.EqualTo("bar"));
        }

        [Test]
        public void Declaration_tokens() {
            var t = Strip("color: red;");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[0].Text, Is.EqualTo("color"));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Colon));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.Ident));
            Assert.That(t[2].Text, Is.EqualTo("red"));
            Assert.That(t[3].Kind, Is.EqualTo(CssTokenKind.Semicolon));
        }

        [Test]
        public void Function_with_arguments() {
            var t = Strip("rgb(255, 0, 0)");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Function));
            Assert.That(t[0].Text, Is.EqualTo("rgb"));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Number));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.Comma));
            Assert.That(t[3].Kind, Is.EqualTo(CssTokenKind.Number));
            Assert.That(t[4].Kind, Is.EqualTo(CssTokenKind.Comma));
            Assert.That(t[5].Kind, Is.EqualTo(CssTokenKind.Number));
            Assert.That(t[6].Kind, Is.EqualTo(CssTokenKind.RParen));
        }

        [Test]
        public void Plus_alone_is_delim() {
            var t = Strip("+");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("+"));
        }

        [Test]
        public void Dash_alone_is_delim() {
            var t = Strip("-");
            Assert.That(t[0].Kind, Is.EqualTo(CssTokenKind.Delim));
            Assert.That(t[0].Text, Is.EqualTo("-"));
        }

        [Test]
        public void Last_token_is_eof() {
            var t = Tokenize("a b c");
            Assert.That(t[t.Count - 1].Kind, Is.EqualTo(CssTokenKind.Eof));
        }

        [Test]
        public void Eof_line_and_col_track_to_end() {
            var t = Tokenize("ab\ncd");
            var eof = t[t.Count - 1];
            Assert.That(eof.Line, Is.EqualTo(2));
            Assert.That(eof.Column, Is.EqualTo(3));
        }

        [Test]
        public void Mixed_tokens_real_declaration() {
            var t = Strip("background: linear-gradient(red, blue);");
            Assert.That(t[0].Text, Is.EqualTo("background"));
            Assert.That(t[1].Kind, Is.EqualTo(CssTokenKind.Colon));
            Assert.That(t[2].Kind, Is.EqualTo(CssTokenKind.Function));
            Assert.That(t[2].Text, Is.EqualTo("linear-gradient"));
        }
    }
}
