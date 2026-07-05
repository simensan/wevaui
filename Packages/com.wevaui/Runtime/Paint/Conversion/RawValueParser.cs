using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class RawValueParser {
        // Both Split* functions are pure on their `s` input — same string in,
        // same tokens out, including the Substring allocations. They're called
        // per text-shadow/transform/gradient/border-image per cache miss; with
        // a static text-shadow on N elements the same raw string is split N
        // times per frame. Cache by raw string.
        //
        // **Lifetime contract:** the returned List<string> is OWNED BY THE
        // CACHE — callers must treat it as read-only (iterate, index). Adding
        // or removing items would corrupt subsequent callers' results.
        // Existing usage (TextShadowResolver, TransformResolver, etc.) only
        // foreach/indexes the list, so the invariant holds.
        //
        // Capacity cap: bounded by a soft maximum. Clear on overflow rather
        // than evict — simpler and the working set is typically <100.
        const int MaxCacheEntries = 512;
        static readonly Dictionary<string, List<string>> commaCache = new Dictionary<string, List<string>>(32);
        static readonly Dictionary<string, List<string>> spaceCache = new Dictionary<string, List<string>>(32);

        // Splits a raw CSS value string at top-level (depth-0) commas, respecting parentheses.
        // Each resulting segment has its outer whitespace trimmed.
        // RETURN VALUE IS CACHED — do not mutate.
        public static List<string> SplitTopLevelCommas(string s) {
            if (string.IsNullOrEmpty(s)) return EmptyList;
            if (commaCache.TryGetValue(s, out var cached)) return cached;
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) {
                    list.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            list.Add(s.Substring(start).Trim());
            if (commaCache.Count >= MaxCacheEntries) commaCache.Clear();
            commaCache[s] = list;
            return list;
        }

        // RETURN VALUE IS CACHED — do not mutate.
        public static List<string> SplitTopLevelSpaces(string s) {
            if (string.IsNullOrEmpty(s)) return EmptyList;
            if (spaceCache.TryGetValue(s, out var cached)) return cached;
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && (c == ' ' || c == '\t' || c == '\n' || c == '\r')) {
                    if (i > start) list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length) list.Add(s.Substring(start));
            list.RemoveAll(string.IsNullOrEmpty);
            if (spaceCache.Count >= MaxCacheEntries) spaceCache.Clear();
            spaceCache[s] = list;
            return list;
        }

        static readonly List<string> EmptyList = new List<string>(0);

        // Parses "f(args)" returning name (lowercase) and the inner argument string.
        public static bool TryParseFunctionCall(string s, out string name, out string inner) {
            name = null;
            inner = null;
            if (string.IsNullOrEmpty(s)) return false;
            int paren = s.IndexOf('(');
            if (paren <= 0 || s[s.Length - 1] != ')') return false;
            name = CssStringUtil.ToLowerInvariantOrSame(s.Substring(0, paren).Trim());
            inner = s.Substring(paren + 1, s.Length - paren - 2).Trim();
            return true;
        }

        public static bool TryParseAngleDegrees(string s, out double degrees) {
            degrees = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string t = CssStringUtil.ToLowerInvariantOrSame(s.Trim());
            if (t.EndsWith("turn")) return TryNumberAndScale(t, t.Length - 4, 360.0, out degrees);
            if (t.EndsWith("grad")) return TryNumberAndScale(t, t.Length - 4, 360.0 / 400.0, out degrees);
            if (t.EndsWith("deg")) return TryNumberAndScale(t, t.Length - 3, 1.0, out degrees);
            if (t.EndsWith("rad")) return TryNumberAndScale(t, t.Length - 3, 180.0 / System.Math.PI, out degrees);
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) {
                degrees = raw;
                return true;
            }
            return false;
        }

        public static bool TryParseNumber(string s, out double value) {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryParseNumberOrPercent(string s, out double value, out bool isPercent) {
            value = 0;
            isPercent = false;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.EndsWith("%")) {
                isPercent = true;
                if (double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
                return false;
            }
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static bool TryNumberAndScale(string t, int headLen, double scale, out double result) {
            result = 0;
            if (double.TryParse(t.AsSpan(0, headLen), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) {
                result = v * scale;
                return true;
            }
            return false;
        }

        public static bool LooksLikeColor(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            string t = CssStringUtil.ToLowerInvariantOrSame(s.Trim());
            if (t.StartsWith("#") || t.StartsWith("rgb(") || t.StartsWith("rgba(") || t.StartsWith("hsl(") || t.StartsWith("hsla(")) return true;
            if (t.StartsWith("hwb(") || t.StartsWith("oklab(") || t.StartsWith("oklch(") || t.StartsWith("color-mix(")) return true;
            if (t == "transparent" || CssStringUtil.IsCurrentColor(t)) return true;
            return CssColor.TryFromName(t, out _);
        }
    }
}
