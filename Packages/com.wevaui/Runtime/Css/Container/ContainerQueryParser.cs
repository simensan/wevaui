using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Css.Values;

namespace Weva.Css.Container {
    public static class ContainerQueryParser {
        public static ContainerQueryParseResult Parse(string text) {
            if (text == null) text = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0) {
                return new ContainerQueryParseResult(null, new ContainerQueryList(new List<ContainerQuery>()));
            }
            var tokens = new CssTokenizer(text).Tokenize();
            var reader = new Reader(tokens);
            reader.SkipWs();

            string name = null;
            // An optional bare ident preceding the parenthesized condition is the
            // container name. 'not' is reserved as a logical prefix and never a name.
            if (!reader.AtEnd()) {
                var t = reader.Peek();
                if (t.Kind == CssTokenKind.Ident
                    && !string.Equals(t.Text, "not", System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(t.Text, "and", System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(t.Text, "or", System.StringComparison.OrdinalIgnoreCase)) {
                    name = t.Text;
                    reader.Advance();
                    reader.SkipWs();
                }
            }

            var items = new List<ContainerQuery>();
            // A bare container name without any subsequent condition is not
            // a complete @container query — per the CSS Containment Module
            // a name must be followed by a condition. Reject early so callers
            // get a parse error instead of a silently-matching empty list.
            if (name != null && reader.AtEnd()) {
                throw new ContainerQueryParseException(
                    "Container name '" + name + "' is missing a query condition",
                    reader.LastColumn);
            }
            while (!reader.AtEnd()) {
                var q = ParseQuery(reader);
                if (q != null) items.Add(q);
                reader.SkipWs();
                if (reader.AtEnd()) break;
                if (reader.Peek().Kind == CssTokenKind.Comma) {
                    reader.Advance();
                    reader.SkipWs();
                    continue;
                }
                var t = reader.Peek();
                throw new ContainerQueryParseException("Expected ',' or end of query, got '" + (t.Text ?? "") + "'", t.Column);
            }
            return new ContainerQueryParseResult(name, new ContainerQueryList(items));
        }

        public static ContainerQueryList ParseCondition(string text) {
            return Parse(text).Condition;
        }

        static ContainerQuery ParseQuery(Reader reader) {
            reader.SkipWs();
            if (reader.AtEnd()) return null;
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.Ident && string.Equals(t.Text, "not", System.StringComparison.OrdinalIgnoreCase)) {
                reader.Advance();
                reader.SkipWs();
                var child = ParseSingle(reader);
                return new ContainerQueryNotQuery(child);
            }
            return ParseSequence(reader);
        }

        static ContainerQuery ParseSequence(Reader reader) {
            reader.SkipWs();
            var first = ParseSingle(reader);
            string combinator = null;
            var seq = new List<ContainerQuery> { first };
            while (true) {
                reader.SkipWs();
                if (reader.AtEnd()) break;
                var t = reader.Peek();
                if (t.Kind != CssTokenKind.Ident) break;
                bool isAnd = string.Equals(t.Text, "and", System.StringComparison.OrdinalIgnoreCase);
                bool isOr = string.Equals(t.Text, "or", System.StringComparison.OrdinalIgnoreCase);
                if (!isAnd && !isOr) break;
                string here = isAnd ? "and" : "or";
                if (combinator != null && combinator != here) {
                    throw new ContainerQueryParseException("Cannot mix 'and' and 'or' without parentheses", t.Column);
                }
                combinator = here;
                reader.Advance();
                reader.SkipWs();
                seq.Add(ParseSingle(reader));
            }
            if (seq.Count == 1) return seq[0];
            return combinator == "or"
                ? (ContainerQuery)new ContainerQueryOrQuery(seq)
                : new ContainerQueryAndQuery(seq);
        }

        static ContainerQuery ParseSingle(Reader reader) {
            reader.SkipWs();
            if (reader.AtEnd()) {
                throw new ContainerQueryParseException("Unexpected end of container query", reader.LastColumn);
            }
            var t = reader.Peek();
            // CON-2: `style(...)` style query. The tokenizer emits a single
            // Function token for `style(` (the `(` is already consumed), so the
            // reader is positioned at the first token inside the parens.
            if (t.Kind == CssTokenKind.Function
                && string.Equals(t.Text, "style", System.StringComparison.OrdinalIgnoreCase)) {
                reader.Advance();
                return ParseStyleFunction(reader, t);
            }
            if (t.Kind == CssTokenKind.LParen) {
                return ParseParenthesized(reader);
            }
            throw new ContainerQueryParseException("Expected '(' but got '" + (t.Text ?? "") + "'", t.Column);
        }

        static ContainerQuery ParseStyleFunction(Reader reader, CssToken funcTok) {
            reader.SkipWs();
            if (reader.AtEnd()) {
                throw new ContainerQueryParseException("Unterminated style() query", funcTok.Column);
            }
            var nameTok = reader.Peek();
            // v1 admits only custom-property style features. A standard-property
            // feature (e.g. `style(display: flex)`) or a bare keyword is rejected
            // so the rule drops per EC11 rather than silently mis-matching.
            if (nameTok.Kind != CssTokenKind.Ident
                || nameTok.Text == null
                || !nameTok.Text.StartsWith("--", System.StringComparison.Ordinal)) {
                throw new ContainerQueryParseException(
                    "style() supports only custom properties (--*) in v1", nameTok.Column);
            }
            // Custom property names are case-sensitive — keep the raw text.
            string property = nameTok.Text;
            reader.Advance();
            reader.SkipWs();

            string valueText = null;
            if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Colon) {
                reader.Advance();
                reader.SkipWs();
                valueText = ReadValueUntilRParen(reader);
            }

            reader.SkipWs();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                int col = reader.AtEnd() ? funcTok.Column : reader.Peek().Column;
                throw new ContainerQueryParseException("Expected ')' to close style()", col);
            }
            reader.Advance();
            return new ContainerStyleQuery(property, valueText);
        }

        static ContainerQuery ParseParenthesized(Reader reader) {
            var lparen = reader.Peek();
            reader.Advance();
            reader.SkipWs();

            if (reader.AtEnd()) {
                throw new ContainerQueryParseException("Unterminated query", lparen.Column);
            }

            // Nested boolean group: '(' followed by 'not' or another '(' is a sub-condition.
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.LParen
                || (t.Kind == CssTokenKind.Ident && string.Equals(t.Text, "not", System.StringComparison.OrdinalIgnoreCase))) {
                var inner = ParseQuery(reader);
                // Allow further and/or chaining inside the parens.
                reader.SkipWs();
                if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Ident) {
                    var combo = reader.Peek();
                    if (string.Equals(combo.Text, "and", System.StringComparison.OrdinalIgnoreCase)
                        || string.Equals(combo.Text, "or", System.StringComparison.OrdinalIgnoreCase)) {
                        // Reconstruct sequence by reusing ParseSequence-like loop here.
                        bool isOr = string.Equals(combo.Text, "or", System.StringComparison.OrdinalIgnoreCase);
                        var children = new List<ContainerQuery> { inner };
                        while (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Ident) {
                            var c = reader.Peek();
                            bool curOr = string.Equals(c.Text, "or", System.StringComparison.OrdinalIgnoreCase);
                            bool curAnd = string.Equals(c.Text, "and", System.StringComparison.OrdinalIgnoreCase);
                            if (!curAnd && !curOr) break;
                            if (curOr != isOr) {
                                throw new ContainerQueryParseException("Cannot mix 'and' and 'or' without parentheses", c.Column);
                            }
                            reader.Advance();
                            reader.SkipWs();
                            children.Add(ParseSingle(reader));
                            reader.SkipWs();
                        }
                        inner = isOr
                            ? (ContainerQuery)new ContainerQueryOrQuery(children)
                            : new ContainerQueryAndQuery(children);
                    }
                }
                reader.SkipWs();
                if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                    int col = reader.AtEnd() ? lparen.Column : reader.Peek().Column;
                    throw new ContainerQueryParseException("Expected ')' to close group", col);
                }
                reader.Advance();
                return inner;
            }

            return ParseFeatureBody(reader, lparen);
        }

        static ContainerQuery ParseFeatureBody(Reader reader, CssToken lparen) {
            var nameTok = reader.Peek();
            if (nameTok.Kind != CssTokenKind.Ident) {
                throw new ContainerQueryParseException("Expected feature name", nameTok.Column);
            }
            string featureName = CssStringUtil.ToLowerInvariantOrSame(nameTok.Text);
            reader.Advance();
            reader.SkipWs();

            string valueText = null;
            ContainerFeatureRange range = ContainerFeatureRange.Equals;
            if (featureName.StartsWith("min-")) range = ContainerFeatureRange.Min;
            else if (featureName.StartsWith("max-")) range = ContainerFeatureRange.Max;

            if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Colon) {
                // Legacy form: `(feature: value)`.
                reader.Advance();
                reader.SkipWs();
                valueText = ReadValueUntilRParen(reader);
            } else if (!reader.AtEnd()
                       && reader.Peek().Kind == CssTokenKind.Delim
                       && IsRangeDelimChar(reader.Peek().Text)) {
                // CON-1: range form `(feature >= 600px)` / `(feature > 600px)` /
                // `(feature <= 600px)` / `(feature < 600px)` / `(feature = 600px)`.
                // CSS Containment L3 §3.3 / CSS Media Queries L5 — translates to
                // the corresponding ContainerFeatureRange. v1 simplification:
                // exclusive `>` and `<` collapse to `Min`/`Max` (inclusive); the
                // ½-pixel boundary difference is below layout resolution.
                char op1 = reader.Peek().Text[0];
                reader.Advance();
                bool hasEq = false;
                if (!reader.AtEnd() && reader.Peek().Kind == CssTokenKind.Delim && reader.Peek().Text == "=") {
                    hasEq = true;
                    reader.Advance();
                }
                reader.SkipWs();
                valueText = ReadValueUntilRParen(reader);
                range = op1 switch {
                    '>' => ContainerFeatureRange.Min, // `>=` and `>` both → Min
                    '<' => ContainerFeatureRange.Max, // `<=` and `<` both → Max
                    '=' => ContainerFeatureRange.Equals,
                    _ => ContainerFeatureRange.Equals,
                };
                _ = hasEq; // currently unused — kept for future strict/inclusive distinction.
            }
            reader.SkipWs();
            if (reader.AtEnd() || reader.Peek().Kind != CssTokenKind.RParen) {
                int col = reader.AtEnd() ? lparen.Column : reader.Peek().Column;
                throw new ContainerQueryParseException("Expected ')' to close feature", col);
            }
            reader.Advance();

            return new ContainerFeatureQuery(featureName, valueText, range);
        }

        static bool IsRangeDelimChar(string s) {
            if (string.IsNullOrEmpty(s) || s.Length != 1) return false;
            char c = s[0];
            return c == '>' || c == '<' || c == '=';
        }

        static string ReadValueUntilRParen(Reader reader) {
            var sb = new StringBuilder();
            int depth = 0;
            while (!reader.AtEnd()) {
                var t = reader.Peek();
                if (depth == 0 && t.Kind == CssTokenKind.RParen) break;
                if (t.Kind == CssTokenKind.LParen || t.Kind == CssTokenKind.Function) depth++;
                else if (t.Kind == CssTokenKind.RParen) depth--;
                AppendToken(sb, t);
                reader.Advance();
            }
            return sb.ToString().Trim();
        }

        static void AppendToken(StringBuilder sb, CssToken t) {
            switch (t.Kind) {
                case CssTokenKind.Whitespace:
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    return;
                case CssTokenKind.Number:
                    sb.Append(t.Text ?? t.Number.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case CssTokenKind.Dimension:
                    sb.Append(t.Text ?? (t.Number.ToString("R", CultureInfo.InvariantCulture) + (t.Unit ?? "")));
                    return;
                case CssTokenKind.Percentage:
                    sb.Append(t.Text ?? (t.Number.ToString("R", CultureInfo.InvariantCulture) + "%"));
                    return;
                case CssTokenKind.Hash:
                    sb.Append('#');
                    sb.Append(t.Text);
                    return;
                case CssTokenKind.AtKeyword:
                    sb.Append('@');
                    sb.Append(t.Text);
                    return;
                case CssTokenKind.String:
                    sb.Append('"');
                    sb.Append(t.Text);
                    sb.Append('"');
                    return;
                case CssTokenKind.Url:
                    sb.Append("url(");
                    sb.Append(t.Text);
                    sb.Append(')');
                    return;
                case CssTokenKind.Function:
                    sb.Append(t.Text);
                    sb.Append('(');
                    return;
                case CssTokenKind.Comma:
                case CssTokenKind.Colon:
                case CssTokenKind.Semicolon:
                case CssTokenKind.LBrace:
                case CssTokenKind.RBrace:
                case CssTokenKind.LParen:
                case CssTokenKind.RParen:
                case CssTokenKind.LBracket:
                case CssTokenKind.RBracket:
                case CssTokenKind.Delim:
                case CssTokenKind.Ident:
                    sb.Append(t.Text);
                    return;
            }
        }

        sealed class Reader {
            readonly List<CssToken> tokens;
            int idx;

            public Reader(List<CssToken> toks) {
                tokens = toks;
                idx = 0;
            }

            public bool AtEnd() {
                return idx >= tokens.Count || tokens[idx].Kind == CssTokenKind.Eof;
            }

            public CssToken Peek() => tokens[idx];

            public void Advance() { if (idx < tokens.Count) idx++; }

            public void SkipWs() {
                while (idx < tokens.Count && tokens[idx].Kind == CssTokenKind.Whitespace) idx++;
            }

            public int LastColumn {
                get {
                    if (tokens.Count == 0) return 1;
                    return tokens[tokens.Count - 1].Column;
                }
            }
        }
    }
}
