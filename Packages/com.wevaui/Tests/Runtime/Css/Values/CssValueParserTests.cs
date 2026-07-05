using NUnit.Framework;
using Weva.Css.Values;

namespace Weva.Tests.Css.Values {
    public class CssValueParserTests {
        [Test]
        public void Single_keyword_auto() {
            var v = CssValueParser.Parse("auto");
            Assert.That(v, Is.InstanceOf<CssKeyword>());
            Assert.That(((CssKeyword)v).Identifier, Is.EqualTo("auto"));
        }

        [Test]
        public void Single_keyword_none() {
            var v = CssValueParser.Parse("none");
            Assert.That(v, Is.InstanceOf<CssKeyword>());
            Assert.That(((CssKeyword)v).Identifier, Is.EqualTo("none"));
        }

        [Test]
        public void Single_length() {
            var v = CssValueParser.Parse("12px");
            Assert.That(v, Is.InstanceOf<CssLength>());
        }

        [Test]
        public void Single_number() {
            var v = CssValueParser.Parse("1.5");
            Assert.That(v, Is.InstanceOf<CssNumber>());
            Assert.That(((CssNumber)v).Value, Is.EqualTo(1.5));
        }

        [Test]
        public void Single_percentage() {
            var v = CssValueParser.Parse("75%");
            Assert.That(v, Is.InstanceOf<CssPercentage>());
        }

        [Test]
        public void Color_via_hex() {
            var v = CssValueParser.Parse("#abc");
            Assert.That(v, Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Color_via_rgb() {
            var v = CssValueParser.Parse("rgb(1, 2, 3)");
            Assert.That(v, Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Color_via_named() {
            var v = CssValueParser.Parse("blue");
            Assert.That(v, Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Double_quoted_string() {
            var v = CssValueParser.Parse("\"hello\"");
            Assert.That(v, Is.InstanceOf<CssString>());
            Assert.That(((CssString)v).Value, Is.EqualTo("hello"));
            Assert.That(v.Raw, Is.EqualTo("\"hello\""));
        }

        [Test]
        public void Single_quoted_string_preserves_quote() {
            var v = CssValueParser.Parse("'hello'");
            Assert.That(v, Is.InstanceOf<CssString>());
            Assert.That(((CssString)v).Value, Is.EqualTo("hello"));
            Assert.That(v.Raw, Is.EqualTo("'hello'"));
        }

        [Test]
        public void String_with_escape() {
            var v = CssValueParser.Parse("\"a\\nb\"");
            Assert.That(((CssString)v).Value, Is.EqualTo("a\nb"));
        }

        [Test]
        public void Url_with_quoted_string() {
            var v = CssValueParser.Parse("url(\"images/foo.png\")");
            Assert.That(v, Is.InstanceOf<CssUrl>());
            Assert.That(((CssUrl)v).Href, Is.EqualTo("images/foo.png"));
        }

        [Test]
        public void Url_bare_path() {
            var v = CssValueParser.Parse("url(images/foo.png)");
            Assert.That(v, Is.InstanceOf<CssUrl>());
            Assert.That(((CssUrl)v).Href, Is.EqualTo("images/foo.png"));
        }

        [Test]
        public void Var_simple() {
            var v = CssValueParser.Parse("var(--accent)");
            Assert.That(v, Is.InstanceOf<CssVariableReference>());
            var vr = (CssVariableReference)v;
            Assert.That(vr.Name, Is.EqualTo("--accent"));
            Assert.That(vr.Fallback, Is.Null);
        }

        [Test]
        public void Var_with_fallback() {
            var v = (CssVariableReference)CssValueParser.Parse("var(--accent, 12px)");
            Assert.That(v.Name, Is.EqualTo("--accent"));
            Assert.That(v.Fallback, Is.InstanceOf<CssLength>());
        }

        [Test]
        public void Var_with_color_fallback() {
            var v = (CssVariableReference)CssValueParser.Parse("var(--c, red)");
            Assert.That(v.Fallback, Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Calc_round_trips() {
            var v = (CssCalc)CssValueParser.Parse("calc(100% - 20px)");
            Assert.That(v.ToText(), Is.EqualTo("calc(100% - 20px)"));
        }

        [Test]
        public void Comma_separated_list() {
            var v = CssValueParser.Parse("Arial, sans-serif");
            Assert.That(v, Is.InstanceOf<CssValueList>());
            var list = (CssValueList)v;
            Assert.That(list.Separator, Is.EqualTo(CssValueListSeparator.Comma));
            Assert.That(list.Items.Count, Is.EqualTo(2));
        }

        [Test]
        public void Space_separated_list() {
            var v = CssValueParser.Parse("1px solid red");
            Assert.That(v, Is.InstanceOf<CssValueList>());
            var list = (CssValueList)v;
            Assert.That(list.Separator, Is.EqualTo(CssValueListSeparator.Space));
            Assert.That(list.Items.Count, Is.EqualTo(3));
            Assert.That(list.Items[0], Is.InstanceOf<CssLength>());
            Assert.That(list.Items[1], Is.InstanceOf<CssKeyword>());
            Assert.That(list.Items[2], Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Function_passthrough_linear_gradient() {
            var v = CssValueParser.Parse("linear-gradient(red, blue)");
            Assert.That(v, Is.InstanceOf<CssFunctionCall>());
            var f = (CssFunctionCall)v;
            Assert.That(f.Name, Is.EqualTo("linear-gradient"));
            Assert.That(f.Arguments.Count, Is.EqualTo(2));
            Assert.That(f.Arguments[0], Is.InstanceOf<CssColor>());
            Assert.That(f.Arguments[1], Is.InstanceOf<CssColor>());
        }

        [Test]
        public void Font_shorthand_mixed_separators() {
            var v = CssValueParser.Parse("bold 16px/1.5 Arial, sans-serif");
            Assert.That(v, Is.InstanceOf<CssValueList>());
            var outer = (CssValueList)v;
            Assert.That(outer.Separator, Is.EqualTo(CssValueListSeparator.Comma));
            Assert.That(outer.Items.Count, Is.EqualTo(2));
            Assert.That(outer.Items[0], Is.InstanceOf<CssValueList>());
        }

        [Test]
        public void Mismatched_paren_throws() {
            Assert.Throws<CssValueParseException>(() => CssValueParser.Parse("rgb(1, 2, 3"));
        }

        [Test]
        public void Bad_var_name_throws() {
            Assert.Throws<CssValueParseException>(() => CssValueParser.Parse("var(123)"));
        }

        [Test]
        public void Empty_throws() {
            Assert.Throws<CssValueParseException>(() => CssValueParser.Parse(""));
        }

        [Test]
        public void TryParse_accepts_valid() {
            Assert.That(CssValue.TryParse("12px", out var v), Is.True);
            Assert.That(v, Is.InstanceOf<CssLength>());
        }

        [Test]
        public void TryParse_rejects_invalid() {
            Assert.That(CssValue.TryParse("rgb(1,2", out _), Is.False);
        }

        [Test]
        public void Number_with_unknown_unit_throws() {
            Assert.Throws<CssValueParseException>(() => CssValueParser.Parse("12fizz"));
        }

        [Test]
        public void Identifier_value_is_keyword_when_not_color() {
            var v = CssValueParser.Parse("inherit");
            Assert.That(v, Is.InstanceOf<CssKeyword>());
        }

        [Test]
        public void Nested_var_in_function() {
            var v = (CssFunctionCall)CssValueParser.Parse("linear-gradient(var(--a), var(--b))");
            Assert.That(v.Arguments[0], Is.InstanceOf<CssVariableReference>());
            Assert.That(v.Arguments[1], Is.InstanceOf<CssVariableReference>());
        }
    }
}
