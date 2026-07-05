using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Animation;
using Weva.Css.Values;

namespace Weva.Css.Animation {
    public static class TransitionShorthandParser {
        public static IReadOnlyList<TransitionSpec> Parse(string text) {
            var list = new List<TransitionSpec>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            string trimmed = text.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return list;

            foreach (var segment in SplitTopLevelByComma(trimmed)) {
                var spec = ParseOne(segment.Trim());
                list.Add(spec);
            }
            return list;
        }

        static TransitionSpec ParseOne(string segment) {
            string property = null;
            double? duration = null;
            double? delay = null;
            EasingFunction easing = null;

            foreach (var tok in TokenizeRespectingParens(segment)) {
                if (TryParseTime(tok, out double seconds)) {
                    if (duration == null) duration = seconds;
                    else if (delay == null) delay = seconds;
                    continue;
                }
                if (TryParseEasing(tok, out var e)) {
                    if (easing == null) { easing = e; continue; }
                }
                if (property == null) {
                    property = tok;
                    continue;
                }
            }

            return new TransitionSpec(
                property ?? "all",
                duration ?? 0,
                delay ?? 0,
                easing ?? EaseEasing.Instance);
        }

        public static bool TryParseTime(string text, out double seconds) {
            seconds = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
                if (double.TryParse(s.AsSpan(0, s.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) {
                    seconds = ms / 1000.0;
                    return true;
                }
                return false;
            }
            if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
                if (double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)) {
                    seconds = sec;
                    return true;
                }
            }
            return false;
        }

        static bool TryParseEasing(string text, out EasingFunction easing) {
            easing = null;
            if (string.IsNullOrEmpty(text)) return false;
            string lower = CssStringUtil.ToLowerInvariantOrSame(text.Trim());
            switch (lower) {
                case "linear":
                case "ease":
                case "ease-in":
                case "ease-out":
                case "ease-in-out":
                case "step-start":
                case "step-end":
                    try { easing = EasingParser.Parse(lower); return true; }
                    catch (FormatException) { return false; }
            }
            if (lower.StartsWith("cubic-bezier(", StringComparison.Ordinal) ||
                lower.StartsWith("steps(", StringComparison.Ordinal) ||
                lower.StartsWith("linear(", StringComparison.Ordinal)) {
                try { easing = EasingParser.Parse(lower); return true; }
                catch (FormatException) { return false; }
            }
            return false;
        }

        static IEnumerable<string> SplitTopLevelByComma(string s) {
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (start < s.Length) yield return s.Substring(start);
            else if (start == s.Length) yield return "";
        }

        static IEnumerable<string> TokenizeRespectingParens(string s) {
            int depth = 0;
            int start = -1;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth == 0 && char.IsWhiteSpace(c)) {
                    if (start >= 0) {
                        yield return s.Substring(start, i - start);
                        start = -1;
                    }
                } else {
                    if (start < 0) start = i;
                }
            }
            if (start >= 0) yield return s.Substring(start);
        }
    }
}
