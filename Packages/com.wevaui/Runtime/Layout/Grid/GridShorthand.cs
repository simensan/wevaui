using System;
using Weva.Css.Values;

namespace Weva.Layout.Grid {
    internal static class GridShorthand {
        // Splits "<rows> / <columns>" into two parts. Caller recurses with each.
        // Top-level slash split (skip slashes inside parens / brackets / strings).
        public static (string rows, string columns) SplitTemplate(string text) {
            if (string.IsNullOrEmpty(text)) return (null, null);
            int depthParen = 0, depthBracket = 0;
            char inString = '\0';
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (inString != '\0') {
                    if (c == inString) inString = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') { inString = c; continue; }
                if (c == '(') depthParen++;
                else if (c == ')') depthParen--;
                else if (c == '[') depthBracket++;
                else if (c == ']') depthBracket--;
                else if (c == '/' && depthParen == 0 && depthBracket == 0) {
                    string rows = text.Substring(0, i).Trim();
                    string cols = text.Substring(i + 1).Trim();
                    return (rows, cols);
                }
            }
            return (text.Trim(), null);
        }

        public static (string row, string column) SplitGap(string gap) {
            if (string.IsNullOrEmpty(gap)) return (null, null);
            string trimmed = gap.Trim();
            int depthParen = 0;
            for (int i = 0; i < trimmed.Length; i++) {
                char c = trimmed[i];
                if (c == '(') depthParen++;
                else if (c == ')') depthParen--;
                else if (depthParen == 0 && (c == ' ' || c == '\t')) {
                    string a = trimmed.Substring(0, i).Trim();
                    string b = trimmed.Substring(i + 1).Trim();
                    if (b.Length == 0) return (a, null);
                    return (a, b);
                }
            }
            return (trimmed, null);
        }

        public static (string a, string b) SplitPlaceShorthand(string text) {
            if (string.IsNullOrEmpty(text)) return (null, null);
            string trimmed = text.Trim();
            int depthParen = 0;
            for (int i = 0; i < trimmed.Length; i++) {
                char c = trimmed[i];
                if (c == '(') depthParen++;
                else if (c == ')') depthParen--;
                else if (depthParen == 0 && (c == ' ' || c == '\t')) {
                    string a = trimmed.Substring(0, i).Trim();
                    string b = trimmed.Substring(i + 1).Trim();
                    if (b.Length == 0) return (a, a);
                    return (a, b);
                }
            }
            return (trimmed, trimmed);
        }

        public static GridAutoFlow ParseAutoFlow(string raw, GridAutoFlow fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            string lower = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            bool dense = false;
            bool col = false;
            foreach (var tok in lower.Split(' ', '\t')) {
                if (tok == "row") col = false;
                else if (tok == "column") col = true;
                else if (tok == "dense") dense = true;
            }
            if (col) return dense ? GridAutoFlow.ColumnDense : GridAutoFlow.Column;
            return dense ? GridAutoFlow.RowDense : GridAutoFlow.Row;
        }
    }
}
