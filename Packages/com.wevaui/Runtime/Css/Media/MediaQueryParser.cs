using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Css.Values;

namespace Weva.Css.Media {
    public static class MediaQueryParser {
        public static MediaQueryList Parse(string text) {
            if (text == null) text = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0) {
                return new MediaQueryList(new List<MediaQuery>());
            }
            var tokens = new CssTokenizer(text).Tokenize();
            var reader = new Reader(tokens);
            var items = new List<MediaQuery>();
            reader.SkipWs();
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
                throw new MediaQueryParseException("Expected ',' or end of query, got '" + (t.Text ?? "") + "'", t.Column);
            }
            return new MediaQueryList(items);
        }

        static MediaQuery ParseQuery(Reader reader) {
            reader.SkipWs();
            if (reader.AtEnd()) return null;
            var t = reader.Peek();

            if (t.Kind == CssTokenKind.Ident && string.Equals(t.Text, "not", System.StringComparison.OrdinalIgnoreCase)) {
                reader.Advance();
                reader.SkipWs();
                var child = ParseTypeOrFeatureSequence(reader);
                return new MediaNotQuery(child);
            }
            if (t.Kind == CssTokenKind.Ident && string.Equals(t.Text, "only", System.StringComparison.OrdinalIgnoreCase)) {
                // 'only' is a hint for legacy browsers; treat as a pass-through prefix.
                reader.Advance();
                reader.SkipWs();
            }
            return ParseTypeOrFeatureSequence(reader);
        }

        static MediaQuery ParseTypeOrFeatureSequence(Reader reader) {
            reader.SkipWs();
            var first = ParseSingle(reader);
            var seq = new List<MediaQuery> { first };
            while (true) {
                reader.SkipWs();
                if (reader.AtEnd()) break;
                var t = reader.Peek();
                if (t.Kind != CssTokenKind.Ident) break;
                if (!string.Equals(t.Text, "and", System.StringComparison.OrdinalIgnoreCase)) break;
                reader.Advance();
                reader.SkipWs();
                seq.Add(ParseSingle(reader));
            }
            if (seq.Count == 1) return seq[0];
            return new MediaAndQuery(seq);
        }

        static MediaQuery ParseSingle(Reader reader) {
            reader.SkipWs();
            if (reader.AtEnd()) {
                throw new MediaQueryParseException("Unexpected end of media query", reader.LastColumn);
            }
            var t = reader.Peek();
            if (t.Kind == CssTokenKind.LParen) {
                return ParseFeature(reader);
            }
            if (t.Kind == CssTokenKind.Ident) {
                string name = CssStringUtil.ToLowerInvariantOrSame(t.Text);
                reader.Advance();
                switch (name) {
                    case "all": return new MediaTypeQuery(MediaType.All);
                    case "screen": return new MediaTypeQuery(MediaType.Screen);
                    case "print": return new MediaTypeQuery(MediaType.Print);
                }
                throw new MediaQueryParseException("Unknown media type '" + t.Text + "'", t.Column);
            }
            throw new MediaQueryParseException("Expected media type or '(' but got '" + (t.Text ?? "") + "'", t.Column);
        }

        static MediaQuery ParseFeature(Reader reader) {
            var lparen = reader.Peek();
            if (lparen.Kind != CssTokenKind.LParen) {
                throw new MediaQueryParseException("Expected '('", lparen.Column);
            }
            reader.Advance();
            reader.SkipWs();

            if (reader.AtEnd()) {
                throw new MediaQueryParseException("Unterminated feature query", lparen.Column);
            }

            var nameTok = reader.Peek();
            if (nameTok.Kind != CssTokenKind.Ident) {
                throw new MediaQueryParseException("Expected feature name", nameTok.Column);
            }
            string featureName = CssStringUtil.ToLowerInvariantOrSame(nameTok.Text);
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
                int col = reader.AtEnd() ? lparen.Column : reader.Peek().Column;
                throw new MediaQueryParseException("Expected ')' to close feature", col);
            }
            reader.Advance();

            MediaFeatureRange range = MediaFeatureRange.Equals;
            if (featureName.StartsWith("min-")) range = MediaFeatureRange.Min;
            else if (featureName.StartsWith("max-")) range = MediaFeatureRange.Max;

            return new MediaFeatureQuery(featureName, valueText, range);
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
