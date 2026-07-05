using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Generated Content L3 §2 — multi-segment content concatenation and
    // counter() / counters() function resolution via ICounterContext.
    //
    // These tests cover:
    //   1. Multi-segment string + attr() concatenation.
    //   2. counter() single-value resolution with and without a style arg.
    //   3. counters() ancestor-chain resolution with separator and style.
    //   4. Edge cases: missing attr fallback, missing counter, invalid forms.
    //
    // The tests inject a SimpleCounterContext stub (defined below) to supply
    // counter values without requiring a full box-tree walk. This lets us pin
    // the resolver contract independently of BoxBuilder.
    public class GeneratedContentMultiSegmentTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        // ── Multi-segment string + attr() concatenation ────────────────────

        [Test]
        public void String_plus_attr_concatenates_correctly() {
            // CSS GC L3 §2: `content: "Hello " attr(data-name)` → "Hello world"
            var doc = Html("<div data-name=\"world\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("\"Hello \" attr(data-name)", div),
                Is.EqualTo("Hello world"));
        }

        [Test]
        public void String_plus_attr_plus_string_concatenates_three_segments() {
            // content: "Name: " attr(data-name) "!" → "Name: Alice!"
            var doc = Html("<div data-name=\"Alice\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("\"Name: \" attr(data-name) \"!\"", div),
                Is.EqualTo("Name: Alice!"));
        }

        [Test]
        public void Two_consecutive_attr_calls_concatenate() {
            // content: attr(data-first) attr(data-last) → "JohnDoe"
            var doc = Html("<div data-first=\"John\" data-last=\"Doe\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("attr(data-first) attr(data-last)", div),
                Is.EqualTo("JohnDoe"));
        }

        [Test]
        public void Counter_plus_string_in_multi_segment() {
            // content: counter(c) " items" → "3 items"
            var ctx = new SimpleCounterContext("c", new[] { 3 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c) \" items\"", null, ctx),
                Is.EqualTo("3 items"));
        }

        [Test]
        public void Missing_attr_in_multi_segment_uses_empty_string() {
            // content: attr(missing) " end" → " end" (absent attr → "")
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("attr(missing) \" end\"", div),
                Is.EqualTo(" end"));
        }

        [Test]
        public void Attr_with_quoted_fallback_in_multi_segment() {
            // content: attr(missing, "X") " end" → "X end"
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("attr(missing, \"X\") \" end\"", div),
                Is.EqualTo("X end"));
        }

        [Test]
        public void Single_quoted_string_round_trips_unchanged() {
            // Single string segment should still work as before.
            Assert.That(
                CascadeEngine.ResolveContentString("\"Hello\""),
                Is.EqualTo("Hello"));
        }

        [Test]
        public void Empty_string_segment_round_trips() {
            // content: "" → ""
            Assert.That(
                CascadeEngine.ResolveContentString("\"\""),
                Is.EqualTo(""));
        }

        [Test]
        public void Normal_returns_null() {
            Assert.That(CascadeEngine.ResolveContentString("normal"), Is.Null);
        }

        [Test]
        public void None_returns_null() {
            Assert.That(CascadeEngine.ResolveContentString("none"), Is.Null);
        }

        // ── counter() resolution ───────────────────────────────────────────

        [Test]
        public void Counter_single_value_resolves_decimal() {
            // counter(c) → "5" when c=5
            var ctx = new SimpleCounterContext("c", new[] { 5 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c)", null, ctx),
                Is.EqualTo("5"));
        }

        [Test]
        public void Counter_with_upper_roman_style() {
            // counter(c, upper-roman) → "IV" when c=4
            var ctx = new SimpleCounterContext("c", new[] { 4 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c, upper-roman)", null, ctx),
                Is.EqualTo("IV"));
        }

        [Test]
        public void Counter_with_lower_roman_style() {
            // counter(c, lower-roman) → "iv" when c=4
            var ctx = new SimpleCounterContext("c", new[] { 4 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c, lower-roman)", null, ctx),
                Is.EqualTo("iv"));
        }

        [Test]
        public void Counter_with_upper_alpha_style() {
            // counter(c, upper-alpha) → "A" for 1, "Z" for 26, "AA" for 27
            var ctx1 = new SimpleCounterContext("c", new[] { 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c, upper-alpha)", null, ctx1),
                Is.EqualTo("A"));

            var ctx27 = new SimpleCounterContext("c", new[] { 27 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c, upper-alpha)", null, ctx27),
                Is.EqualTo("AA"));
        }

        [Test]
        public void Counter_with_lower_alpha_style() {
            // counter(c, lower-alpha) → "a" for 1
            var ctx = new SimpleCounterContext("c", new[] { 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c, lower-alpha)", null, ctx),
                Is.EqualTo("a"));
        }

        [Test]
        public void Counter_missing_returns_empty_string_without_context() {
            // counter(c) with null ctx → ""  (no pseudo box suppression)
            Assert.That(
                CascadeEngine.ResolveContentString("counter(c)", null, null),
                Is.EqualTo(""));
        }

        [Test]
        public void Counter_undefined_name_returns_empty_string() {
            // counter(undefined) when only "c" is defined → ""
            var ctx = new SimpleCounterContext("c", new[] { 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counter(undefined)", null, ctx),
                Is.EqualTo(""));
        }

        // ── counters() resolution ─────────────────────────────────────────

        [Test]
        public void Counters_single_level_returns_single_value() {
            // counters(item, ".") with one scope level → "1"
            var ctx = new SimpleCounterContext("item", new[] { 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(item, \".\")", null, ctx),
                Is.EqualTo("1"));
        }

        [Test]
        public void Counters_two_level_joins_with_separator() {
            // counters(item, ".") with two scopes [1, 1] → "1.1"
            var ctx = new SimpleCounterContext("item", new[] { 1, 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(item, \".\")", null, ctx),
                Is.EqualTo("1.1"));
        }

        [Test]
        public void Counters_three_level_joins_correctly() {
            // counters(item, ".") with scopes [1, 2, 3] → "1.2.3"
            var ctx = new SimpleCounterContext("item", new[] { 1, 2, 3 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(item, \".\")", null, ctx),
                Is.EqualTo("1.2.3"));
        }

        [Test]
        public void Counters_three_level_upper_roman() {
            // counters(item, ".", upper-roman) → "I.II.III"
            var ctx = new SimpleCounterContext("item", new[] { 1, 2, 3 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(item, \".\", upper-roman)", null, ctx),
                Is.EqualTo("I.II.III"));
        }

        [Test]
        public void Counters_missing_counter_returns_empty_string() {
            // counters(missing, ".") with null ctx → ""
            Assert.That(
                CascadeEngine.ResolveContentString("counters(missing, \".\")", null, null),
                Is.EqualTo(""));
        }

        [Test]
        public void Counters_undefined_counter_name_returns_empty_string() {
            // counters(other, ".") when only "item" is defined → ""
            var ctx = new SimpleCounterContext("item", new[] { 1 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(other, \".\")", null, ctx),
                Is.EqualTo(""));
        }

        [Test]
        public void Counters_dash_separator() {
            // counters(s, "-") → "2-3"
            var ctx = new SimpleCounterContext("s", new[] { 2, 3 });
            Assert.That(
                CascadeEngine.ResolveContentString("counters(s, \"-\")", null, ctx),
                Is.EqualTo("2-3"));
        }

        // ── FormatCounterValue unit tests ─────────────────────────────────

        [Test]
        public void Format_decimal_one_through_five() {
            for (int i = 1; i <= 5; i++) {
                Assert.That(CascadeEngine.FormatCounterValue(i, "decimal"),
                    Is.EqualTo(i.ToString()),
                    $"decimal {i}");
            }
        }

        [Test]
        public void Format_upper_roman_spot_checks() {
            Assert.That(CascadeEngine.FormatCounterValue(1, "upper-roman"), Is.EqualTo("I"));
            Assert.That(CascadeEngine.FormatCounterValue(4, "upper-roman"), Is.EqualTo("IV"));
            Assert.That(CascadeEngine.FormatCounterValue(9, "upper-roman"), Is.EqualTo("IX"));
            Assert.That(CascadeEngine.FormatCounterValue(14, "upper-roman"), Is.EqualTo("XIV"));
            Assert.That(CascadeEngine.FormatCounterValue(40, "upper-roman"), Is.EqualTo("XL"));
            Assert.That(CascadeEngine.FormatCounterValue(90, "upper-roman"), Is.EqualTo("XC"));
            Assert.That(CascadeEngine.FormatCounterValue(400, "upper-roman"), Is.EqualTo("CD"));
            Assert.That(CascadeEngine.FormatCounterValue(900, "upper-roman"), Is.EqualTo("CM"));
            Assert.That(CascadeEngine.FormatCounterValue(1994, "upper-roman"), Is.EqualTo("MCMXCIV"));
        }

        [Test]
        public void Format_lower_roman_is_lowercase_of_upper() {
            Assert.That(CascadeEngine.FormatCounterValue(4, "lower-roman"), Is.EqualTo("iv"));
            Assert.That(CascadeEngine.FormatCounterValue(1994, "lower-roman"), Is.EqualTo("mcmxciv"));
        }

        [Test]
        public void Format_alpha_26_letter_cycle() {
            Assert.That(CascadeEngine.FormatCounterValue(26, "upper-alpha"), Is.EqualTo("Z"));
            Assert.That(CascadeEngine.FormatCounterValue(26, "lower-alpha"), Is.EqualTo("z"));
        }

        // ── attr() single-segment backward compat ─────────────────────────

        [Test]
        public void Single_attr_with_present_attribute_resolves() {
            var doc = Html("<div data-val=\"42\"></div>");
            var div = FindByTag(doc, "div");
            Assert.That(
                CascadeEngine.ResolveContentString("attr(data-val)", div),
                Is.EqualTo("42"));
        }

        [Test]
        public void Single_attr_with_none_fallback_returns_null() {
            var doc = Html("<div></div>");
            var div = FindByTag(doc, "div");
            // attr(missing, none) → null (suppress pseudo box)
            Assert.That(
                CascadeEngine.ResolveContentString("attr(missing, none)", div),
                Is.Null);
        }

        // ── Helpers ────────────────────────────────────────────────────────

        static Element FindByTag(Document doc, string tag) => FindByTag((Node)doc, tag);

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        // ── SimpleCounterContext stub ─────────────────────────────────────

        // Minimal ICounterContext that holds a single named counter with a
        // fixed list of ancestor scope values (outermost → innermost).
        // Quote depth is a no-op stub (0, no increment/decrement) because
        // these tests focus on counter() / counters() resolution.
        sealed class SimpleCounterContext : ICounterContext {
            readonly string _name;
            readonly int[] _values; // outermost-first ancestor chain

            public SimpleCounterContext(string name, int[] values) {
                _name = name;
                _values = values ?? new int[0];
            }

            // Returns the innermost (last) scope value for the named counter.
            public int GetCounterValue(string name) {
                if (name != _name || _values.Length == 0) return ICounterContext.NotFound;
                return _values[_values.Length - 1];
            }

            // Returns the full ancestor chain for the named counter.
            public int[] GetCounterValues(string name) {
                if (name != _name) return null;
                return _values;
            }

            // Quote depth — stub for counter-focused tests.
            public int QuoteDepth => 0;
            public void IncrementQuoteDepth() { }
            public void DecrementQuoteDepth() { }
        }
    }
}
