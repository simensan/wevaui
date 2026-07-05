using NUnit.Framework;
using Weva.Figma.Json;

namespace Weva.Figma.Tests.Json
{
    [TestFixture]
    public class JsonParserTests
    {
        [Test]
        public void ParsesFlatObject()
        {
            JsonValue v = JsonParser.Parse("{\"a\":1,\"b\":\"x\",\"c\":true,\"d\":null}");
            Assert.That(v.Kind, Is.EqualTo(JsonKind.Object));
            Assert.That(v["a"].AsInt(), Is.EqualTo(1));
            Assert.That(v["b"].AsString(), Is.EqualTo("x"));
            Assert.That(v["c"].AsBool(), Is.True);
            Assert.That(v["d"].IsNull, Is.True);
        }

        [Test]
        public void ParsesNestedAndArrays()
        {
            JsonValue v = JsonParser.Parse("{\"items\":[{\"n\":1},{\"n\":2},{\"n\":3}]}");
            Assert.That(v["items"].Count, Is.EqualTo(3));
            Assert.That(v["items"][0]["n"].AsInt(), Is.EqualTo(1));
            Assert.That(v["items"][2]["n"].AsInt(), Is.EqualTo(3));
        }

        [Test]
        public void ParsesNumbersIncludingFractionAndExponent()
        {
            Assert.That(JsonParser.Parse("16").AsDouble(), Is.EqualTo(16).Within(1e-9));
            Assert.That(JsonParser.Parse("-3.5").AsDouble(), Is.EqualTo(-3.5).Within(1e-9));
            Assert.That(JsonParser.Parse("0.25").AsDouble(), Is.EqualTo(0.25).Within(1e-9));
            Assert.That(JsonParser.Parse("1e3").AsDouble(), Is.EqualTo(1000).Within(1e-9));
            Assert.That(JsonParser.Parse("2.5E-2").AsDouble(), Is.EqualTo(0.025).Within(1e-9));
        }

        [Test]
        public void ParsesStringEscapesAndUnicode()
        {
            JsonValue v = JsonParser.Parse("\"line\\nbreak \\\"q\\\" \\u00e9\\u0041\"");
            Assert.That(v.AsString(), Is.EqualTo("line\nbreak \"q\" éA"));
        }

        [Test]
        public void IgnoresWhitespaceBetweenTokens()
        {
            JsonValue v = JsonParser.Parse("  {\n  \"a\" : [ 1 , 2 ]\n}  ");
            Assert.That(v["a"].Count, Is.EqualTo(2));
        }

        [Test]
        public void MissingKeyReturnsNullSentinelNotThrow()
        {
            JsonValue v = JsonParser.Parse("{\"a\":1}");
            Assert.That(v["nope"].IsNull, Is.True);
            Assert.That(v["nope"]["deeper"].IsNull, Is.True);
            Assert.That(v["nope"].AsInt(42), Is.EqualTo(42));
        }

        [Test]
        public void WrongTypeAccessFallsBack()
        {
            JsonValue v = JsonParser.Parse("{\"a\":\"text\"}");
            Assert.That(v["a"].AsInt(7), Is.EqualTo(7));
            Assert.That(v["a"][0].IsNull, Is.True);
        }

        [Test]
        public void HasAndTryGetReportPresence()
        {
            JsonValue v = JsonParser.Parse("{\"a\":1}");
            Assert.That(v.Has("a"), Is.True);
            Assert.That(v.Has("b"), Is.False);
            Assert.That(v.TryGet("a", out var got), Is.True);
            Assert.That(got.AsInt(), Is.EqualTo(1));
        }

        [Test]
        public void EmptyContainers()
        {
            Assert.That(JsonParser.Parse("{}").Count, Is.EqualTo(0));
            Assert.That(JsonParser.Parse("[]").Count, Is.EqualTo(0));
        }

        [Test]
        public void TrailingContentThrows()
        {
            Assert.That(() => JsonParser.Parse("{} junk"), Throws.TypeOf<JsonParseException>());
        }

        [Test]
        public void MalformedThrows()
        {
            Assert.That(() => JsonParser.Parse("{\"a\":}"), Throws.TypeOf<JsonParseException>());
            Assert.That(() => JsonParser.Parse("[1,2"), Throws.TypeOf<JsonParseException>());
            Assert.That(() => JsonParser.Parse("\"unterminated"), Throws.TypeOf<JsonParseException>());
        }

        [Test]
        public void TryParseReturnsFalseOnError()
        {
            Assert.That(JsonParser.TryParse("not json", out _), Is.False);
            Assert.That(JsonParser.TryParse("{\"ok\":1}", out var v), Is.True);
            Assert.That(v["ok"].AsInt(), Is.EqualTo(1));
        }

        [Test]
        public void NumberFormattingIsInvariant()
        {
            // Round-trip through AsString uses invariant culture (dot decimal).
            JsonValue v = JsonParser.Parse("3.5");
            Assert.That(v.AsString(), Is.EqualTo("3.5"));
        }
    }
}
