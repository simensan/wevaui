namespace Weva.Css.Values {
    public abstract class CssValue {
        public abstract CssValueKind Kind { get; }
        public string Raw { get; internal set; }

        // Parse-result cache. CssValue instances are immutable after
        // construction (`Raw` has only an internal setter that's invoked from
        // each subtype's constructor, never afterwards), so the same parsed
        // tree can be safely shared across every caller that passes the same
        // raw string. Parsing is hot on the paint path — TextRunResolver and
        // friends call TryParse per text run, ~6 KB/frame in the profiler.
        // Caching by raw-string lookup makes the steady-state cost a single
        // dictionary probe per call.
        //
        // Negative-cache: failed parses also memoize (as `null` sentinels in
        // `parseCache` with a parallel `failedCache` set) so re-parsing
        // garbage like `font-size: calc(unsupported())` doesn't re-emit a
        // warning every frame.
        //
        // Size cap: bounded by a soft maximum to keep the cache from
        // unbounded growth in pathological hot-reload churn. On overflow we
        // clear the whole cache rather than evicting — simpler and the
        // working set is normally well under the cap.
        const int MaxCacheEntries = 4096;
        static readonly System.Collections.Generic.Dictionary<string, CssValue> parseCache
            = new System.Collections.Generic.Dictionary<string, CssValue>(64);
        static readonly System.Collections.Generic.HashSet<string> failedCache
            = new System.Collections.Generic.HashSet<string>();

        public static CssValue Parse(string text) {
            return CssValueParser.Parse(text);
        }

        public static bool TryParse(string text, out CssValue value) {
            return TryParse(text, null, out value);
        }

        // property-aware overload: when a parse fails, the diagnostic carries
        // the property name so authors can pinpoint the offending declaration
        // without grepping. Existing callers can keep using the 2-arg overload
        // and will get a warning attributed to property "?" — strictly worse
        // than passing the name, but better than silent skip.
        public static bool TryParse(string text, string property, out CssValue value) {
            return TryParseInternal(text, property, warnOnFailure: true, out value);
        }

        // Silent variant for internal cache fills (ComputedStyle.GetParsed) and
        // other call sites that have a downstream raw-string fallback for the
        // values CssValueParser doesn't fully support yet (e.g. angle dimensions
        // inside rotate() / hue-rotate() — TransformResolver / FilterResolver
        // both keep a string path on the GetParsed-returned-null branch). The
        // logged warning was firing once per parse-failure-per-frame on
        // animated transforms because the per-frame angle value
        // ("337.94295deg", "339.381444deg", …) was a new string every frame,
        // so failedCache memoization didn't dedupe it.
        public static bool TryParseSilent(string text, out CssValue value) {
            return TryParseInternal(text, null, warnOnFailure: false, out value);
        }

        // Diagnostic counters for the process-static parseCache. Exposed via
        // Diagnostics so a profile / test harness can read the hit/miss
        // ratio without having to instrument the call sites individually.
        // Bumped on every TryParse path; reset on ClearCacheCounters().
        public static long ParseCacheHits;
        public static long ParseCacheMisses;
        public static long ParseCacheFailedHits;

        public static void ClearCacheCounters() {
            ParseCacheHits = 0;
            ParseCacheMisses = 0;
            ParseCacheFailedHits = 0;
        }

        // Drops every entry from the parse + failed-parse caches. Used by the
        // test harness to keep state from bleeding between cases — a test that
        // registers a custom property mid-suite would otherwise see another
        // test's stale entry for the same raw text.
        public static void ClearCachesForTests() {
            parseCache.Clear();
            failedCache.Clear();
            ClearCacheCounters();
        }

        // Drops only the negative (failed-parse) cache. Called from the
        // stylesheet hot-reload pipeline so an author fix to a malformed
        // declaration (e.g. `#ff` -> `#ff0000`) re-parses on the next
        // TryParse instead of silently returning the cached null. The
        // positive parseCache is preserved: raw-text -> CssValue is
        // deterministic, so successful entries remain valid across a
        // stylesheet swap and clearing them would force a full reparse
        // storm on the first frame after every save.
        public static void InvalidateNegativeCache() {
            failedCache.Clear();
            ParseCacheFailedHits = 0;
        }

        static bool TryParseInternal(string text, string property, bool warnOnFailure, out CssValue value) {
            if (text == null) { value = null; return false; }
            if (parseCache.TryGetValue(text, out var cached)) {
                ParseCacheHits++;
                value = cached;
                return true;
            }
            if (failedCache.Contains(text)) {
                ParseCacheFailedHits++;
                value = null;
                return false;
            }
            ParseCacheMisses++;
            try {
                value = CssValueParser.Parse(text);
                if (parseCache.Count + failedCache.Count >= MaxCacheEntries) {
                    parseCache.Clear();
                    failedCache.Clear();
                }
                // CssValuePool rents CssLength / CssNumber / CssPercentage
                // wrappers and Reset()s them between layout passes. Storing a
                // pool-rented reference in this process-lifetime cache would
                // leave parseCache[text] pointing at an instance whose value
                // got mutated on the next pass — the same "300px" key would
                // later return a CssLength carrying whatever number the pool
                // re-used the slot for. Clone any pool-mutable leaves into
                // fresh non-pooled copies so the cache's lifetime contract
                // (immutable for the key's lifetime) holds.
                value = CssValueStableCopy.Of(value);
                parseCache[text] = value;
                return true;
            } catch (CssValueParseException) {
                if (warnOnFailure) {
                    Weva.Diagnostics.UICssDiagnostics.Warn(
                        "CssValueParser",
                        "failed to parse value '" + text + "' for property '" + (property ?? "?") + "'");
                }
                if (parseCache.Count + failedCache.Count < MaxCacheEntries) {
                    failedCache.Add(text);
                }
                value = null;
                return false;
            } catch (CssParseException) {
                if (warnOnFailure) {
                    Weva.Diagnostics.UICssDiagnostics.Warn(
                        "CssValueParser",
                        "failed to parse value '" + text + "' for property '" + (property ?? "?") + "'");
                }
                if (parseCache.Count + failedCache.Count < MaxCacheEntries) {
                    failedCache.Add(text);
                }
                value = null;
                return false;
            }
        }

        public override string ToString() => Raw ?? "";
    }
}
