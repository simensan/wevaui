using System;
using System.Collections.Generic;
using Weva.Css.Cascade.Shorthands;
using Weva.Css.Selectors;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;

namespace Weva.Css.Cascade {
    // CSS Conditional Rules: evaluates the author-visible subset of @supports.
    // The evaluator is intentionally tied to Weva's actual feature surface:
    // registered parse-only stubs such as clip-path/mask report unsupported.
    public static class SupportsEvaluator {
        public static bool Evaluate(string conditionText) {
            if (string.IsNullOrWhiteSpace(conditionText)) return false;
            var parser = new Parser(conditionText);
            if (!parser.ParseCondition(out bool result)) return false;
            parser.SkipWhitespace();
            return parser.IsEof && result;
        }

        static bool EvaluateDeclaration(string text) {
            int colon = FindTopLevelColon(text);
            if (colon <= 0) return false;

            string property = text.Substring(0, colon).Trim().ToLowerInvariant();
            string value = text.Substring(colon + 1).Trim();
            if (property.Length == 0 || value.Length == 0) return false;
            if (property.StartsWith("--", StringComparison.Ordinal)) return true;

            if (ShorthandRegistry.TryGet(property, out var expander)) {
                try {
                    bool emitted = false;
                    foreach (KeyValuePair<string, string> longhand in expander.Expand(value)) {
                        emitted = true;
                        if (!IsSupportedDeclaration(longhand.Key, longhand.Value)) return false;
                    }
                    return emitted;
                } catch {
                    return false;
                }
            }

            return IsSupportedDeclaration(property, value);
        }

        static bool IsSupportedDeclaration(string property, string value) {
            if (!CssProperties.TryGet(property, out _) || CssProperties.IsStubProperty(property)) return false;
            if (property == "clip-path") return ClipPathResolver.IsSupportedValue(value);
            if (property == "backdrop-filter") return SupportsFilterList(value);
            if (property == "mask-image") return SupportsMaskImage(value);
            if (property == "mask-mode") return SupportsOneOf(value, "alpha", "luminance", "match-source");
            if (property == "mask-repeat") return SupportsRepeat(value);
            if (property == "mask-composite") return SupportsOneOf(value, "add", "subtract", "intersect", "exclude");
            return true;
        }

        static bool SupportsFilterList(string value) {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return true;
            try {
                FilterParser.Parse(value, Weva.Css.Values.LengthContext.Default, Weva.Paint.LinearColor.Black);
                return true;
            } catch {
                return false;
            }
        }

        static bool SupportsMaskImage(string value) {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = RawValueParser.SplitTopLevelCommas(value);
            if (parts.Count == 0) return false;
            for (int i = 0; i < parts.Count; i++) {
                string part = parts[i].Trim();
                if (string.Equals(part, "none", StringComparison.OrdinalIgnoreCase)) continue;
                if (!RawValueParser.TryParseFunctionCall(part, out var name, out _)) return false;
                if (name != "url"
                    && name != "linear-gradient"
                    && name != "repeating-linear-gradient"
                    && name != "radial-gradient"
                    && name != "conic-gradient") {
                    return false;
                }
            }
            return true;
        }

        static bool SupportsOneOf(string value, params string[] allowed) {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = RawValueParser.SplitTopLevelCommas(value);
            for (int i = 0; i < parts.Count; i++) {
                string v = parts[i].Trim();
                bool ok = false;
                for (int j = 0; j < allowed.Length; j++) {
                    if (string.Equals(v, allowed[j], StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                }
                if (!ok) return false;
            }
            return true;
        }

        static bool SupportsRepeat(string value) {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var layers = RawValueParser.SplitTopLevelCommas(value);
            for (int i = 0; i < layers.Count; i++) {
                var words = layers[i].Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0 || words.Length > 2) return false;
                for (int j = 0; j < words.Length; j++) {
                    string w = words[j].ToLowerInvariant();
                    if (w != "repeat" && w != "no-repeat" && w != "space" && w != "round" && w != "repeat-x" && w != "repeat-y") return false;
                }
            }
            return true;
        }

        static bool EvaluateSelector(string selectorText) {
            if (string.IsNullOrWhiteSpace(selectorText)) return false;
            try {
                SelectorParser.Parse(selectorText);
                return true;
            } catch {
                return false;
            }
        }

        static int FindTopLevelColon(string text) {
            int paren = 0;
            int bracket = 0;
            bool inString = false;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (inString) {
                    if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                    if (c == quote) inString = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                if (c == '(') paren++;
                else if (c == ')' && paren > 0) paren--;
                else if (c == '[') bracket++;
                else if (c == ']' && bracket > 0) bracket--;
                else if (c == ':' && paren == 0 && bracket == 0) return i;
            }
            return -1;
        }

        sealed class Parser {
            readonly string text;
            int pos;

            public Parser(string text) {
                this.text = text ?? "";
            }

            public bool IsEof => pos >= text.Length;

            public void SkipWhitespace() {
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            }

            public bool ParseCondition(out bool result) {
                return ParseOr(out result);
            }

            bool ParseOr(out bool result) {
                if (!ParseAnd(out result)) return false;
                while (true) {
                    int mark = pos;
                    if (!ConsumeKeyword("or")) { pos = mark; return true; }
                    if (!ParseAnd(out bool rhs)) return false;
                    result = result || rhs;
                }
            }

            bool ParseAnd(out bool result) {
                if (!ParseNot(out result)) return false;
                while (true) {
                    int mark = pos;
                    if (!ConsumeKeyword("and")) { pos = mark; return true; }
                    if (!ParseNot(out bool rhs)) return false;
                    result = result && rhs;
                }
            }

            bool ParseNot(out bool result) {
                if (ConsumeKeyword("not")) {
                    if (!ParseNot(out bool inner)) { result = false; return false; }
                    result = !inner;
                    return true;
                }
                return ParsePrimary(out result);
            }

            bool ParsePrimary(out bool result) {
                SkipWhitespace();
                result = false;
                if (IsEof) return false;

                if (StartsWithFunction("selector")) {
                    string args = ReadFunctionArguments("selector");
                    if (args == null) return false;
                    result = EvaluateSelector(args);
                    return true;
                }

                if (text[pos] != '(') return false;

                int close = FindMatchingParen(pos);
                if (close < 0) return false;

                string inner = text.Substring(pos + 1, close - pos - 1).Trim();
                pos = close + 1;
                if (FindTopLevelColon(inner) >= 0) {
                    result = EvaluateDeclaration(inner);
                    return true;
                }

                var nested = new Parser(inner);
                if (!nested.ParseCondition(out result)) return false;
                nested.SkipWhitespace();
                return nested.IsEof;
            }

            bool ConsumeKeyword(string keyword) {
                SkipWhitespace();
                int len = keyword.Length;
                if (pos + len > text.Length) return false;
                if (!string.Equals(text.Substring(pos, len), keyword, StringComparison.OrdinalIgnoreCase)) return false;
                if (pos > 0 && IsIdentChar(text[pos - 1])) return false;
                if (pos + len < text.Length && IsIdentChar(text[pos + len])) return false;
                pos += len;
                return true;
            }

            bool StartsWithFunction(string name) {
                SkipWhitespace();
                int len = name.Length;
                if (pos + len + 1 > text.Length) return false;
                if (!string.Equals(text.Substring(pos, len), name, StringComparison.OrdinalIgnoreCase)) return false;
                return text[pos + len] == '(';
            }

            string ReadFunctionArguments(string name) {
                SkipWhitespace();
                int start = pos + name.Length;
                if (start >= text.Length || text[start] != '(') return null;
                int close = FindMatchingParen(start);
                if (close < 0) return null;
                string args = text.Substring(start + 1, close - start - 1).Trim();
                pos = close + 1;
                return args;
            }

            int FindMatchingParen(int openIndex) {
                int depth = 0;
                bool inString = false;
                char quote = '\0';
                for (int i = openIndex; i < text.Length; i++) {
                    char c = text[i];
                    if (inString) {
                        if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                        if (c == quote) inString = false;
                        continue;
                    }
                    if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                    if (c == '(') depth++;
                    else if (c == ')') {
                        depth--;
                        if (depth == 0) return i;
                    }
                }
                return -1;
            }

            static bool IsIdentChar(char c) {
                return char.IsLetterOrDigit(c) || c == '_' || c == '-';
            }
        }
    }
}
