using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Animation;
using Weva.Css.Values;

namespace Weva.Css.Animation {
    public static class AnimationShorthandParser {
        public static IReadOnlyList<AnimationSpec> Parse(string text) {
            var list = new List<AnimationSpec>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            string trimmed = text.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return list;

            foreach (var segment in SplitTopLevelByComma(trimmed)) {
                var spec = ParseOne(segment.Trim());
                if (spec.Name != null) list.Add(spec);
            }
            return list;
        }

        static AnimationSpec ParseOne(string segment) {
            string name = null;
            double? duration = null;
            double? delay = null;
            EasingFunction easing = null;
            double? iterations = null;
            PlaybackDirection direction = PlaybackDirection.Normal;
            FillMode fill = FillMode.None;
            bool paused = false;
            AnimationCompositionMode composition = AnimationCompositionMode.Replace;
            bool dirSet = false, fillSet = false, playSet = false, compositionSet = false;

            foreach (var tok in TokenizeRespectingParens(segment)) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(tok);
                if (TransitionShorthandParser.TryParseTime(tok, out double seconds)) {
                    if (duration == null) duration = seconds;
                    else if (delay == null) delay = seconds;
                    continue;
                }
                if (lower == "infinite") { iterations = double.PositiveInfinity; continue; }
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) {
                    if (iterations == null) { iterations = n; continue; }
                }
                if (TryParseEasing(tok, out var e)) { if (easing == null) { easing = e; continue; } }
                if (!dirSet && TryParseDirection(lower, out var d)) { direction = d; dirSet = true; continue; }
                if (!fillSet && TryParseFillMode(lower, out var fm)) { fill = fm; fillSet = true; continue; }
                // CSS Animations L2 §5 / §10: `animation-composition` token
                // (`replace` / `add` / `accumulate`) in the shorthand. Like
                // fill-mode / direction, recognised by name; first match wins
                // and any subsequent token of the same shape falls through to
                // animation-name. `add` / `replace` are ALSO not legal
                // animation-name identifiers under the spec (which would
                // require a custom-ident), so disambiguation by token
                // identity is safe here. (The lower-case `accumulate` is
                // similarly reserved.)
                if (!compositionSet && TryParseComposition(lower, out var cm)) { composition = cm; compositionSet = true; continue; }
                if (!playSet && (lower == "running" || lower == "paused")) { paused = lower == "paused"; playSet = true; continue; }
                if (lower == "none") {
                    // could be animation-name=none or fill-mode=none; if no name yet, treat as name=null
                    if (name == null) { name = null; continue; }
                }
                if (name == null) { name = tok; continue; }
            }

            return new AnimationSpec(
                name,
                duration ?? 0,
                delay ?? 0,
                easing ?? EaseEasing.Instance,
                iterations ?? 1,
                direction,
                fill,
                paused,
                composition);
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

        static bool TryParseDirection(string lower, out PlaybackDirection d) {
            switch (lower) {
                case "normal": d = PlaybackDirection.Normal; return true;
                case "reverse": d = PlaybackDirection.Reverse; return true;
                case "alternate": d = PlaybackDirection.Alternate; return true;
                case "alternate-reverse": d = PlaybackDirection.AlternateReverse; return true;
            }
            d = PlaybackDirection.Normal;
            return false;
        }

        static bool TryParseComposition(string lower, out AnimationCompositionMode c) {
            switch (lower) {
                case "replace": c = AnimationCompositionMode.Replace; return true;
                case "add": c = AnimationCompositionMode.Add; return true;
                case "accumulate": c = AnimationCompositionMode.Accumulate; return true;
            }
            c = AnimationCompositionMode.Replace;
            return false;
        }

        static bool TryParseFillMode(string lower, out FillMode f) {
            switch (lower) {
                case "none": f = FillMode.None; return true;
                case "forwards": f = FillMode.Forwards; return true;
                case "backwards": f = FillMode.Backwards; return true;
                case "both": f = FillMode.Both; return true;
            }
            f = FillMode.None;
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
            if (start <= s.Length) yield return s.Substring(start);
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
