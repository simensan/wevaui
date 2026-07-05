using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // TG3 — Direct unit coverage for `RawValueParser` (internal static helper
    // under `Runtime/Paint/Conversion/RawValueParser.cs`).
    //
    // The class splits comma- / space-separated CSS value strings while
    // respecting parenthesis depth, and caches its results in two static
    // Dictionary<string, List<string>> tables with a documented
    // "do-not-mutate" lifetime contract. These tests pin:
    //   * the depth-aware splitter behaviour (commas / spaces / nested parens),
    //   * the cache identity contract (same input → SAME List<string> ref),
    //   * the soft cache cap (clear-on-overflow keeps the dict bounded).
    //
    // The static caches are visible across tests in the same AppDomain. Each
    // test uses input strings deliberately unique to the test method so a
    // sibling test that hit the overflow path can't poison identity probes.
    public class RawValueParserTests {
        // ---------------- Comma-split ----------------

        [Test]
        public void Comma_split_three_simple_items_trims_outer_whitespace_TG3() {
            var parts = RawValueParser.SplitTopLevelCommas("red, blue, green");
            Assert.That(parts.Count, Is.EqualTo(3));
            Assert.That(parts[0], Is.EqualTo("red"));
            Assert.That(parts[1], Is.EqualTo("blue"));
            Assert.That(parts[2], Is.EqualTo("green"));
        }

        // ---------------- Space-split ----------------

        [Test]
        public void Space_split_three_lengths_TG3() {
            var parts = RawValueParser.SplitTopLevelSpaces("10px 20px 30px");
            Assert.That(parts.Count, Is.EqualTo(3));
            Assert.That(parts[0], Is.EqualTo("10px"));
            Assert.That(parts[1], Is.EqualTo("20px"));
            Assert.That(parts[2], Is.EqualTo("30px"));
        }

        // ---------------- Parens-respecting (comma) ----------------

        [Test]
        public void Comma_split_respects_parens_rgb_function_is_one_item_TG3() {
            // The commas inside `rgb(255, 0, 0)` are at depth 1 and must NOT
            // split the outer list. A naive splitter would emit 4 items
            // ("rgb(255", "0", "0)", "blue"); we expect 2.
            var parts = RawValueParser.SplitTopLevelCommas("rgb(255, 0, 0), blue");
            Assert.That(parts.Count, Is.EqualTo(2));
            Assert.That(parts[0], Is.EqualTo("rgb(255, 0, 0)"));
            Assert.That(parts[1], Is.EqualTo("blue"));
        }

        [Test]
        public void Comma_split_respects_nested_parens_calc_with_var_TG3() {
            // Two levels of nesting — `calc(10px + var(--x))` carries depth 2
            // at the inner `var(`. The outer comma between the calc() result
            // and `5px` is the only depth-0 comma.
            var parts = RawValueParser.SplitTopLevelCommas("calc(10px + var(--x)), 5px");
            Assert.That(parts.Count, Is.EqualTo(2));
            Assert.That(parts[0], Is.EqualTo("calc(10px + var(--x))"));
            Assert.That(parts[1], Is.EqualTo("5px"));
        }

        [Test]
        public void Space_split_respects_parens_full_rgb_function_token_TG3() {
            var parts = RawValueParser.SplitTopLevelSpaces("rgb(255, 0, 0) 10px");
            Assert.That(parts.Count, Is.EqualTo(2));
            Assert.That(parts[0], Is.EqualTo("rgb(255, 0, 0)"));
            Assert.That(parts[1], Is.EqualTo("10px"));
        }

        // ---------------- Cache identity / do-not-mutate ----------------

        [Test]
        public void Comma_cache_hit_returns_same_instance_TG3() {
            // Regression pin for the cache. Use a string that's deliberately
            // unique to this test method so neighbour-test cache-cap clears
            // can't race.
            const string key = "TG3_cache_hit_comma_unique_marker_red, blue";
            var first = RawValueParser.SplitTopLevelCommas(key);
            var second = RawValueParser.SplitTopLevelCommas(key);
            Assert.That(second, Is.SameAs(first),
                "RawValueParser.SplitTopLevelCommas must cache by raw string and return the SAME List<string> instance on a repeated parse — callers iterate the cached list and the contract documented in the file header is identity-stable.");
        }

        [Test]
        public void Space_cache_hit_returns_same_instance_TG3() {
            const string key = "TG3_cache_hit_space_unique_marker_10px 20px";
            var first = RawValueParser.SplitTopLevelSpaces(key);
            var second = RawValueParser.SplitTopLevelSpaces(key);
            Assert.That(second, Is.SameAs(first),
                "RawValueParser.SplitTopLevelSpaces must cache by raw string and return the SAME List<string> instance on a repeated parse.");
        }

        [Test]
        public void Comma_cached_list_is_reference_identical_across_call_pair_TG3() {
            // "Do-not-mutate" contract pin: the returned list is the cached
            // instance. We assert via reference equality on a SECOND call.
            // This is the same test as the cache-hit pin above, framed as the
            // mutation-contract regression — kept distinct so the test name
            // surfaces which invariant a future regression broke.
            const string key = "TG3_no_mutate_unique_marker_a, b, c";
            var a = RawValueParser.SplitTopLevelCommas(key);
            var b = RawValueParser.SplitTopLevelCommas(key);
            Assert.That(ReferenceEquals(a, b), Is.True,
                "Cached list MUST be the same object on every call for `" + key + "` — callers iterate/index this list and the file header documents the do-not-mutate contract.");
            Assert.That(a.Count, Is.EqualTo(3));
        }

        // ---------------- Cache cap behaviour ----------------

        [Test]
        public void Comma_cache_cap_clears_on_overflow_keeps_dict_bounded_TG3() {
            // The cache has a documented soft cap of 512 entries. Parsing 513+
            // distinct strings should NOT grow the cache unboundedly — on the
            // clear-on-overflow path the count drops back to the size of the
            // last batch inserted after a clear. We can't read the dictionary
            // directly, but we CAN observe the cache-clear via identity: a
            // string parsed BEFORE the overflow should have its cached list
            // dropped, so re-parsing it returns a FRESH (non-reference-equal)
            // list instance.
            //
            // Use a unique prefix so neighbour tests' cache entries don't
            // collide with our 0..N range.
            const string prefix = "TG3_cap_test_unique_prefix_";

            // Seed an entry whose identity we'll probe after overflow.
            string probe = prefix + "probe";
            var probeFirst = RawValueParser.SplitTopLevelCommas(probe);

            // Now push the cache past its cap with 600 distinct strings — far
            // enough beyond 512 to guarantee at least one clear has fired even
            // if other tests already filled some slots.
            for (int i = 0; i < 600; i++) {
                _ = RawValueParser.SplitTopLevelCommas(prefix + i + ", x");
            }

            // After the cap was breached, the cache was cleared at least
            // once. Re-parsing the probe string returns a fresh list — NOT
            // reference-equal to the pre-overflow instance.
            var probeAfter = RawValueParser.SplitTopLevelCommas(probe);
            Assert.That(ReferenceEquals(probeAfter, probeFirst), Is.False,
                "After parsing 600 distinct strings the soft cap should have triggered a clear, so the probe-string entry from before the burst is no longer cached and re-parsing yields a fresh List<string>.");

            // Sanity: the re-parsed list still has the correct content.
            Assert.That(probeAfter.Count, Is.EqualTo(1));
            Assert.That(probeAfter[0], Is.EqualTo(probe));
        }

        [Test]
        public void Space_cache_cap_clears_on_overflow_keeps_dict_bounded_TG3() {
            const string prefix = "TG3_space_cap_test_unique_prefix_";
            string probe = prefix + "probe";
            var probeFirst = RawValueParser.SplitTopLevelSpaces(probe);

            for (int i = 0; i < 600; i++) {
                _ = RawValueParser.SplitTopLevelSpaces(prefix + i + " x");
            }

            var probeAfter = RawValueParser.SplitTopLevelSpaces(probe);
            Assert.That(ReferenceEquals(probeAfter, probeFirst), Is.False,
                "Space-cache should clear on overflow at the same 512-entry soft cap as the comma cache.");
            Assert.That(probeAfter.Count, Is.EqualTo(1));
            Assert.That(probeAfter[0], Is.EqualTo(probe));
        }

        // ---------------- Empty / null guards ----------------

        [Test]
        public void Null_or_empty_input_returns_empty_singleton_for_both_splits_TG3() {
            var emptyComma1 = RawValueParser.SplitTopLevelCommas("");
            var emptyComma2 = RawValueParser.SplitTopLevelCommas(null);
            var emptySpace1 = RawValueParser.SplitTopLevelSpaces("");
            var emptySpace2 = RawValueParser.SplitTopLevelSpaces(null);

            Assert.That(emptyComma1.Count, Is.EqualTo(0));
            Assert.That(emptyComma2.Count, Is.EqualTo(0));
            Assert.That(emptySpace1.Count, Is.EqualTo(0));
            Assert.That(emptySpace2.Count, Is.EqualTo(0));

            // Both splitters share the same `EmptyList` singleton sentinel,
            // so the four returns are reference-equal — pin that too.
            Assert.That(ReferenceEquals(emptyComma1, emptyComma2), Is.True);
            Assert.That(ReferenceEquals(emptySpace1, emptySpace2), Is.True);
            Assert.That(ReferenceEquals(emptyComma1, emptySpace1), Is.True,
                "The empty-input fast path on both splitters returns the same shared EmptyList sentinel.");
        }
    }
}
