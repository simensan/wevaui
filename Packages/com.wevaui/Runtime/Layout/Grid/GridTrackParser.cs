using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Layout.Grid {
    internal static class GridTrackParser {
        // PH4: hard clamp for repeat() counts — see the parse site.
        internal const int MaxRepeatCount = 10000;

        public sealed class ParseException : Exception {
            public ParseException(string message) : base(message) { }
        }

        public static GridTemplate Parse(string text, LengthContext lengthCtx) {
            if (string.IsNullOrEmpty(text)) return GridTemplate.Empty;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return GridTemplate.Empty;
            if (CssStringUtil.EqualsIgnoreCase(trimmed, "none") || CssStringUtil.EqualsIgnoreCase(trimmed, "auto")) return GridTemplate.Empty;

            var tokens = Tokenize(trimmed);
            var p = new Cursor(tokens);

            var tracks = new List<GridTrackSize>();
            var lineNames = new List<List<string>>();
            lineNames.Add(new List<string>());

            bool isAutoFill = false;
            bool isAutoFit = false;
            List<GridTrackSize> autoPattern = null;
            List<List<string>> autoPatternNames = null;

            while (!p.IsEnd) {
                if (p.PeekKind == TokenKind.LBracket) {
                    var names = ReadLineNameGroup(ref p);
                    lineNames[lineNames.Count - 1].AddRange(names);
                    continue;
                }
                if (p.PeekKind == TokenKind.Ident && string.Equals(p.PeekText, "repeat", StringComparison.OrdinalIgnoreCase)
                    && p.PeekNextKind == TokenKind.LParen) {
                    ReadRepeat(ref p, lengthCtx, tracks, lineNames,
                        ref isAutoFill, ref isAutoFit, ref autoPattern, ref autoPatternNames);
                    continue;
                }

                var size = ReadTrackSize(ref p, lengthCtx);
                tracks.Add(size);
                lineNames.Add(new List<string>());
            }

            var roLineNames = new List<IReadOnlyList<string>>(lineNames.Count);
            foreach (var l in lineNames) roLineNames.Add(l.ToArray());

            if (autoPattern != null) {
                var roAutoNames = new List<IReadOnlyList<string>>(autoPatternNames.Count);
                foreach (var l in autoPatternNames) roAutoNames.Add(l.ToArray());
                return new GridTemplate(tracks.ToArray(), roLineNames, isAutoFill, isAutoFit, autoPattern.ToArray(), roAutoNames);
            }
            return new GridTemplate(tracks.ToArray(), roLineNames);
        }

        static void ReadRepeat(ref Cursor p, LengthContext lengthCtx,
                               List<GridTrackSize> outTracks,
                               List<List<string>> outLineNames,
                               ref bool isAutoFill, ref bool isAutoFit,
                               ref List<GridTrackSize> autoPattern,
                               ref List<List<string>> autoPatternNames) {
            p.Expect(TokenKind.Ident);
            p.Expect(TokenKind.LParen);

            int count = -1;
            bool fill = false, fit = false;
            if (p.PeekKind == TokenKind.Ident) {
                string id = CssStringUtil.ToLowerInvariantOrSame(p.PeekText);
                if (id == "auto-fill") { fill = true; p.Advance(); }
                else if (id == "auto-fit") { fit = true; p.Advance(); }
            }
            if (!fill && !fit) {
                if (p.PeekKind != TokenKind.Number) throw new ParseException("repeat() expects an integer or auto-fill/auto-fit");
                // PH4: read as double before converting — (int)Math.Round on a
                // value beyond int range is undefined (wraps negative), and
                // an unclamped count let one declaration
                // (`repeat(2000000000, 1px [a] 2px)`) allocate billions of
                // track entries + a List<string> per line at layout time.
                // Chrome clamps track counts too (~1e5); 10,000 covers any
                // real grid while keeping worst-case allocation trivial.
                double rawCount = Math.Round(p.PeekNumber);
                if (rawCount < 1) throw new ParseException("repeat() count must be >= 1");
                count = rawCount > MaxRepeatCount ? MaxRepeatCount : (int)rawCount;
                p.Advance();
            }
            p.Expect(TokenKind.Comma);

            var patternTracks = new List<GridTrackSize>();
            var patternNames = new List<List<string>>();
            patternNames.Add(new List<string>());
            while (p.PeekKind != TokenKind.RParen) {
                if (p.IsEnd) throw new ParseException("Unterminated repeat()");
                if (p.PeekKind == TokenKind.LBracket) {
                    var names = ReadLineNameGroup(ref p);
                    patternNames[patternNames.Count - 1].AddRange(names);
                    continue;
                }
                var size = ReadTrackSize(ref p, lengthCtx);
                patternTracks.Add(size);
                patternNames.Add(new List<string>());
            }
            p.Expect(TokenKind.RParen);

            if (patternTracks.Count == 0) throw new ParseException("repeat() must contain at least one track");

            if (fill || fit) {
                isAutoFill = fill;
                isAutoFit = fit;
                autoPattern = new List<GridTrackSize>(patternTracks);
                autoPatternNames = new List<List<string>>();
                foreach (var l in patternNames) autoPatternNames.Add(new List<string>(l));
                return;
            }

            for (int rep = 0; rep < count; rep++) {
                outLineNames[outLineNames.Count - 1].AddRange(patternNames[0]);
                for (int t = 0; t < patternTracks.Count; t++) {
                    outTracks.Add(patternTracks[t]);
                    var endNames = new List<string>(patternNames[t + 1]);
                    outLineNames.Add(endNames);
                }
            }
        }

        static GridTrackSize ReadTrackSize(ref Cursor p, LengthContext lengthCtx) {
            if (p.PeekKind == TokenKind.Ident && string.Equals(p.PeekText, "minmax", StringComparison.OrdinalIgnoreCase)
                && p.PeekNextKind == TokenKind.LParen) {
                p.Advance();
                p.Expect(TokenKind.LParen);
                var min = ReadTrackSize(ref p, lengthCtx);
                p.Expect(TokenKind.Comma);
                var max = ReadTrackSize(ref p, lengthCtx);
                p.Expect(TokenKind.RParen);
                return GridTrackSize.Minmax(min, max);
            }
            // CSS Grid L1 §7.2.3: fit-content(<length-percentage>) — sugar for
            // minmax(auto, max-content) clamped to the argument. The argument
            // must be a length or percentage (no intrinsic keywords, no fr).
            if (p.PeekKind == TokenKind.Ident && string.Equals(p.PeekText, "fit-content", StringComparison.OrdinalIgnoreCase)
                && p.PeekNextKind == TokenKind.LParen) {
                p.Advance();
                p.Expect(TokenKind.LParen);
                GridTrackSize limit;
                if (p.PeekKind == TokenKind.Length) {
                    double px = p.PeekNumber;
                    string unit = p.PeekUnit;
                    p.Advance();
                    if (unit == "fr") throw new ParseException("fit-content() does not accept fr units");
                    limit = ResolveLengthAsTrack(px, unit, lengthCtx);
                } else if (p.PeekKind == TokenKind.Percent) {
                    double v = p.PeekNumber;
                    p.Advance();
                    limit = GridTrackSize.Percentage(v);
                } else if (p.PeekKind == TokenKind.Number) {
                    // Bare numbers (e.g. `0`) are treated as length in px.
                    double v = p.PeekNumber;
                    p.Advance();
                    limit = GridTrackSize.Length(v);
                } else {
                    throw new ParseException("fit-content() expects a <length-percentage> argument");
                }
                p.Expect(TokenKind.RParen);
                return GridTrackSize.FitContent(limit);
            }
            if (p.PeekKind == TokenKind.Ident) {
                string id = CssStringUtil.ToLowerInvariantOrSame(p.PeekText);
                p.Advance();
                switch (id) {
                    case "auto": return GridTrackSize.Auto;
                    case "min-content": return GridTrackSize.MinContent;
                    case "max-content": return GridTrackSize.MaxContent;
                }
                throw new ParseException("Unknown track keyword: " + id);
            }
            if (p.PeekKind == TokenKind.Length) {
                double px = p.PeekNumber;
                string unit = p.PeekUnit;
                p.Advance();
                if (unit == "fr") return GridTrackSize.Fr(px);
                return ResolveLengthAsTrack(px, unit, lengthCtx);
            }
            if (p.PeekKind == TokenKind.Percent) {
                double v = p.PeekNumber;
                p.Advance();
                return GridTrackSize.Percentage(v);
            }
            if (p.PeekKind == TokenKind.Number) {
                double v = p.PeekNumber;
                p.Advance();
                return GridTrackSize.Length(v);
            }
            throw new ParseException("Expected track size, got " + p.PeekKind);
        }

        static GridTrackSize ResolveLengthAsTrack(double value, string unit, LengthContext lengthCtx) {
            if (string.IsNullOrEmpty(unit) || unit == "px") return GridTrackSize.Length(value);
            if (CssLength.TryParseUnit(unit, out var u)) {
                var len = new CssLength(value, u);
                try {
                    return GridTrackSize.Length(len.ToPixels(lengthCtx));
                } catch {
                    return GridTrackSize.Length(value);
                }
            }
            throw new ParseException("Unknown length unit: " + unit);
        }

        static List<string> ReadLineNameGroup(ref Cursor p) {
            p.Expect(TokenKind.LBracket);
            var names = new List<string>();
            while (p.PeekKind != TokenKind.RBracket) {
                if (p.IsEnd) throw new ParseException("Unterminated line-name group");
                if (p.PeekKind != TokenKind.Ident) throw new ParseException("Line name must be an identifier");
                names.Add(p.PeekText);
                p.Advance();
            }
            p.Expect(TokenKind.RBracket);
            return names;
        }

        // Tokenizer

        enum TokenKind { Ident, Number, Length, Percent, LParen, RParen, LBracket, RBracket, Comma, End }

        struct Token {
            public TokenKind Kind;
            public string Text;
            public string Unit;
            public double Number;
        }

        struct Cursor {
            readonly List<Token> tokens;
            int pos;
            public Cursor(List<Token> ts) { tokens = ts; pos = 0; }
            public bool IsEnd => pos >= tokens.Count;
            public TokenKind PeekKind => IsEnd ? TokenKind.End : tokens[pos].Kind;
            public TokenKind PeekNextKind => pos + 1 >= tokens.Count ? TokenKind.End : tokens[pos + 1].Kind;
            public string PeekText => IsEnd ? "" : tokens[pos].Text;
            public string PeekUnit => IsEnd ? "" : tokens[pos].Unit;
            public double PeekNumber => IsEnd ? 0 : tokens[pos].Number;
            public void Advance() { pos++; }
            public void Expect(TokenKind k) {
                if (PeekKind != k) throw new ParseException("Expected " + k + " but got " + PeekKind);
                pos++;
            }
        }

        static List<Token> Tokenize(string s) {
            var list = new List<Token>();
            int i = 0;
            while (i < s.Length) {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(') { list.Add(new Token { Kind = TokenKind.LParen, Text = "(" }); i++; continue; }
                if (c == ')') { list.Add(new Token { Kind = TokenKind.RParen, Text = ")" }); i++; continue; }
                if (c == '[') { list.Add(new Token { Kind = TokenKind.LBracket, Text = "[" }); i++; continue; }
                if (c == ']') { list.Add(new Token { Kind = TokenKind.RBracket, Text = "]" }); i++; continue; }
                if (c == ',') { list.Add(new Token { Kind = TokenKind.Comma, Text = "," }); i++; continue; }

                if (c == '-' || c == '+' || char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))) {
                    int start = i;
                    if (c == '-' || c == '+') i++;
                    bool sawDigit = false;
                    bool sawDot = false;
                    while (i < s.Length && (char.IsDigit(s[i]) || (!sawDot && s[i] == '.'))) {
                        if (s[i] == '.') sawDot = true; else sawDigit = true;
                        i++;
                    }
                    if (!sawDigit) throw new ParseException("Bad number near pos " + start);
                    string numStr = s.Substring(start, i - start);
                    double val;
                    if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        throw new ParseException("Bad number: " + numStr);

                    int unitStart = i;
                    while (i < s.Length && (char.IsLetter(s[i]) || s[i] == '%')) i++;
                    string unit = s.Substring(unitStart, i - unitStart);

                    if (unit == "%") {
                        list.Add(new Token { Kind = TokenKind.Percent, Number = val, Text = numStr + "%" });
                    } else if (unit.Length > 0) {
                        list.Add(new Token { Kind = TokenKind.Length, Number = val, Unit = CssStringUtil.ToLowerInvariantOrSame(unit), Text = numStr + unit });
                    } else {
                        list.Add(new Token { Kind = TokenKind.Number, Number = val, Text = numStr });
                    }
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '-') {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-')) i++;
                    string id = s.Substring(start, i - start);
                    list.Add(new Token { Kind = TokenKind.Ident, Text = id });
                    continue;
                }

                throw new ParseException("Unexpected character '" + c + "' at " + i);
            }

            int paren = 0, brack = 0;
            foreach (var t in list) {
                if (t.Kind == TokenKind.LParen) paren++;
                else if (t.Kind == TokenKind.RParen) paren--;
                else if (t.Kind == TokenKind.LBracket) brack++;
                else if (t.Kind == TokenKind.RBracket) brack--;
                if (paren < 0 || brack < 0) throw new ParseException("Unbalanced bracket/paren");
            }
            if (paren != 0) throw new ParseException("Unmatched parenthesis");
            if (brack != 0) throw new ParseException("Unmatched bracket");
            return list;
        }
    }
}
