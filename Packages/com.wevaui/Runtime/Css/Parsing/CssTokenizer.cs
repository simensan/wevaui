using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Css {
    public sealed class CssTokenizer {
        readonly string src;
        readonly bool throwOnError;
        int pos;
        int line = 1;
        int col = 1;
        int tokenLine, tokenCol;
        readonly List<CssToken> tokens = new();

        public CssTokenizer(string source, bool throwOnError = true) {
            src = source ?? "";
            this.throwOnError = throwOnError;
        }

        public List<CssToken> Tokenize() {
            while (!AtEnd()) {
                MarkTokenStart();
                char c = Peek();

                if (c == '/' && PeekAt(1) == '*') {
                    SkipComment();
                    continue;
                }

                if (c == '<' && PeekAt(1) == '!' && PeekAt(2) == '-' && PeekAt(3) == '-') {
                    Advance(); Advance(); Advance(); Advance();
                    continue;
                }

                if (c == '-' && PeekAt(1) == '-' && PeekAt(2) == '>') {
                    Advance(); Advance(); Advance();
                    continue;
                }

                if (IsWhitespace(c)) {
                    while (!AtEnd() && IsWhitespace(Peek())) Advance();
                    Emit(CssTokenKind.Whitespace, " ");
                    continue;
                }

                if (c == '"' || c == '\'') {
                    ConsumeString(c);
                    continue;
                }

                if (c == '#') {
                    Advance();
                    if (!AtEnd() && IsNameChar(Peek())) {
                        var sb = new StringBuilder();
                        while (!AtEnd() && IsNameChar(Peek())) {
                            sb.Append(Peek());
                            Advance();
                        }
                        Emit(CssTokenKind.Hash, sb.ToString());
                    } else {
                        Emit(CssTokenKind.Delim, "#");
                    }
                    continue;
                }

                if (c == '@') {
                    Advance();
                    if (!AtEnd() && IsIdentStart(Peek(), PeekAt(1), PeekAt(2))) {
                        var name = ReadIdent();
                        Emit(CssTokenKind.AtKeyword, name);
                    } else {
                        Emit(CssTokenKind.Delim, "@");
                    }
                    continue;
                }

                if (IsDigit(c) || ((c == '.' || c == '+' || c == '-') && IsNumberStart())) {
                    ConsumeNumeric();
                    continue;
                }

                if (IsIdentStart(c, PeekAt(1), PeekAt(2))) {
                    ConsumeIdentLike();
                    continue;
                }

                switch (c) {
                    case ',': Advance(); Emit(CssTokenKind.Comma, ","); continue;
                    case ':': Advance(); Emit(CssTokenKind.Colon, ":"); continue;
                    case ';': Advance(); Emit(CssTokenKind.Semicolon, ";"); continue;
                    case '{': Advance(); Emit(CssTokenKind.LBrace, "{"); continue;
                    case '}': Advance(); Emit(CssTokenKind.RBrace, "}"); continue;
                    case '(': Advance(); Emit(CssTokenKind.LParen, "("); continue;
                    case ')': Advance(); Emit(CssTokenKind.RParen, ")"); continue;
                    case '[': Advance(); Emit(CssTokenKind.LBracket, "["); continue;
                    case ']': Advance(); Emit(CssTokenKind.RBracket, "]"); continue;
                }

                Advance();
                Emit(CssTokenKind.Delim, c.ToString());
            }

            tokens.Add(new CssToken { Kind = CssTokenKind.Eof, Line = line, Column = col });
            return tokens;
        }

        void SkipComment() {
            Advance(); Advance();
            while (!AtEnd()) {
                if (Peek() == '*' && PeekAt(1) == '/') {
                    Advance(); Advance();
                    return;
                }
                Advance();
            }
            if (throwOnError) throw Error("Unterminated comment");
        }

        void ConsumeString(char quote) {
            Advance();
            var sb = new StringBuilder();
            while (!AtEnd()) {
                char c = Peek();
                if (c == quote) {
                    Advance();
                    Emit(CssTokenKind.String, sb.ToString());
                    return;
                }
                if (c == '\n') {
                    if (throwOnError) throw Error("Unterminated string");
                    // PH3 (CSS Syntax §4.3.5): unescaped newline = parse
                    // error → <bad-string-token>, newline NOT consumed (it
                    // terminates the declaration on the next pass). Pre-fix
                    // this threw even in lenient mode and one unterminated
                    // string blanked the entire document.
                    Emit(CssTokenKind.BadString, sb.ToString());
                    return;
                }
                if (c == '\\') {
                    Advance();
                    if (AtEnd()) break;
                    char e = Peek();
                    if (e == '\n') {
                        Advance();
                        continue;
                    }
                    if (IsHexDigit(e)) {
                        int code = 0;
                        int digits = 0;
                        while (!AtEnd() && digits < 6 && IsHexDigit(Peek())) {
                            code = code * 16 + HexValue(Peek());
                            Advance();
                            digits++;
                        }
                        if (!AtEnd() && IsWhitespace(Peek())) Advance();
                        // CSS Syntax §4.3.7: codepoints in the surrogate range
                        // (U+D800–U+DFFF) and >U+10FFFF are invalid as escape
                        // results; both must produce U+FFFD. Without the
                        // surrogate guard, `char.ConvertFromUtf32` throws
                        // ArgumentOutOfRangeException and aborts parsing of
                        // the entire stylesheet.
                        if (code <= 0 || code > 0x10FFFF
                            || (code >= 0xD800 && code <= 0xDFFF)) {
                            sb.Append('�');
                        } else {
                            sb.Append(char.ConvertFromUtf32(code));
                        }
                        continue;
                    }
                    sb.Append(MapEscape(e));
                    Advance();
                    continue;
                }
                sb.Append(c);
                Advance();
            }
            if (throwOnError) throw Error("Unterminated string");
            // PH3 (CSS Syntax §4.3.5): EOF in a string is a parse error but
            // still returns the <string-token> with what was consumed.
            Emit(CssTokenKind.String, sb.ToString());
        }

        static char MapEscape(char c) {
            switch (c) {
                case 'n': return '\n';
                case 'r': return '\r';
                case 't': return '\t';
                case 'f': return '\f';
                default: return c;
            }
        }

        void ConsumeNumeric() {
            double value = ReadNumber(out string raw);
            if (!AtEnd() && Peek() == '%') {
                Advance();
                tokens.Add(new CssToken {
                    Kind = CssTokenKind.Percentage,
                    Text = raw + "%",
                    Number = value,
                    Unit = "%",
                    Line = tokenLine,
                    Column = tokenCol
                });
                return;
            }
            if (!AtEnd() && IsIdentStart(Peek(), PeekAt(1), PeekAt(2))) {
                string unit = ReadIdent();
                tokens.Add(new CssToken {
                    Kind = CssTokenKind.Dimension,
                    Text = raw + unit,
                    Number = value,
                    Unit = unit,
                    Line = tokenLine,
                    Column = tokenCol
                });
                return;
            }
            tokens.Add(new CssToken {
                Kind = CssTokenKind.Number,
                Text = raw,
                Number = value,
                Line = tokenLine,
                Column = tokenCol
            });
        }

        double ReadNumber(out string raw) {
            var sb = new StringBuilder();
            if (!AtEnd() && (Peek() == '+' || Peek() == '-')) {
                sb.Append(Peek());
                Advance();
            }
            while (!AtEnd() && IsDigit(Peek())) {
                sb.Append(Peek());
                Advance();
            }
            if (!AtEnd() && Peek() == '.' && IsDigit(PeekAt(1))) {
                sb.Append(Peek());
                Advance();
                while (!AtEnd() && IsDigit(Peek())) {
                    sb.Append(Peek());
                    Advance();
                }
            } else if (!AtEnd() && Peek() == '.') {
                sb.Append(Peek());
                Advance();
            }
            if (!AtEnd() && (Peek() == 'e' || Peek() == 'E')) {
                int saved = pos, savedLine = line, savedCol = col;
                var ebuf = new StringBuilder();
                ebuf.Append(Peek());
                Advance();
                if (!AtEnd() && (Peek() == '+' || Peek() == '-')) {
                    ebuf.Append(Peek());
                    Advance();
                }
                if (!AtEnd() && IsDigit(Peek())) {
                    while (!AtEnd() && IsDigit(Peek())) {
                        ebuf.Append(Peek());
                        Advance();
                    }
                    sb.Append(ebuf);
                } else {
                    pos = saved; line = savedLine; col = savedCol;
                }
            }
            raw = sb.ToString();
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
            return v;
        }

        void ConsumeIdentLike() {
            string name = ReadIdent();
            if (!AtEnd() && Peek() == '(') {
                Advance();
                if (name.Equals("url", System.StringComparison.OrdinalIgnoreCase)) {
                    ConsumeUrl(name);
                    return;
                }
                Emit(CssTokenKind.Function, name);
                return;
            }
            Emit(CssTokenKind.Ident, name);
        }

        void ConsumeUrl(string fnName) {
            int savedPos = pos, savedLine = line, savedCol = col;
            while (!AtEnd() && IsWhitespace(Peek())) Advance();
            if (!AtEnd() && (Peek() == '"' || Peek() == '\'')) {
                pos = savedPos; line = savedLine; col = savedCol;
                Emit(CssTokenKind.Function, fnName);
                return;
            }
            var sb = new StringBuilder();
            while (!AtEnd() && Peek() != ')') {
                char c = Peek();
                if (IsWhitespace(c)) {
                    int wsPos = pos, wsLine = line, wsCol = col;
                    while (!AtEnd() && IsWhitespace(Peek())) Advance();
                    if (AtEnd() || Peek() != ')') {
                        if (throwOnError) throw Error("Invalid url(): whitespace before bad chars");
                        ConsumeBadUrlRemnants();
                        Emit(CssTokenKind.BadUrl, sb.ToString());
                        return;
                    }
                    break;
                }
                if (c == '"' || c == '\'' || c == '(') {
                    if (throwOnError) throw Error("Invalid url() body");
                    ConsumeBadUrlRemnants();
                    Emit(CssTokenKind.BadUrl, sb.ToString());
                    return;
                }
                if (c == '\\') {
                    Advance();
                    if (AtEnd()) {
                        if (throwOnError) throw Error("Bad escape in url()");
                        Emit(CssTokenKind.BadUrl, sb.ToString());
                        return;
                    }
                    sb.Append(Peek());
                    Advance();
                    continue;
                }
                sb.Append(c);
                Advance();
            }
            if (AtEnd() || Peek() != ')') {
                if (throwOnError) throw Error("Unterminated url()");
                // PH3 (CSS Syntax §4.3.6): EOF in url() is a parse error but
                // still returns the <url-token> with what was consumed.
                Emit(CssTokenKind.Url, sb.ToString());
                return;
            }
            Advance();
            tokens.Add(new CssToken {
                Kind = CssTokenKind.Url,
                Text = sb.ToString(),
                Line = tokenLine,
                Column = tokenCol
            });
        }

        // PH3 (CSS Syntax §4.3.14): after a url() goes bad, consume up to and
        // including the closing ')' (honouring escapes) so tokenizing resumes
        // at a sane boundary.
        void ConsumeBadUrlRemnants() {
            while (!AtEnd()) {
                char c = Peek();
                if (c == ')') { Advance(); return; }
                if (c == '\\') {
                    Advance();
                    if (!AtEnd()) Advance();
                    continue;
                }
                Advance();
            }
        }

        string ReadIdent() {
            var sb = new StringBuilder();
            if (!AtEnd() && Peek() == '-') {
                sb.Append('-');
                Advance();
                if (!AtEnd() && Peek() == '-') {
                    sb.Append('-');
                    Advance();
                }
            }
            while (!AtEnd() && IsNameChar(Peek())) {
                sb.Append(Peek());
                Advance();
            }
            return sb.ToString();
        }

        bool IsNumberStart() {
            char c = Peek();
            if (IsDigit(c)) return true;
            if (c == '.' && IsDigit(PeekAt(1))) return true;
            if (c == '+' || c == '-') {
                if (IsDigit(PeekAt(1))) return true;
                if (PeekAt(1) == '.' && IsDigit(PeekAt(2))) return true;
            }
            return false;
        }

        static bool IsIdentStart(char a, char b, char c) {
            if (IsLetter(a) || a == '_') return true;
            if (a == '-') {
                if (IsLetter(b) || b == '_' || b == '-') return true;
            }
            return false;
        }

        static bool IsNameChar(char c) {
            return IsLetter(c) || IsDigit(c) || c == '-' || c == '_';
        }

        // CSS Syntax L3 §4.2: "name-start code point: A letter, a non-ASCII
        // code point, or U+005F LOW LINE (_)." and "non-ASCII code point:
        // A code point with a value equal to or greater than U+0080
        // <control>." Without the >= 0x80 branch, identifiers containing
        // any non-ASCII character (e.g. `.café`, `--日本語`, emoji class
        // names) tokenise as a truncated ASCII prefix followed by Delim
        // tokens for each non-ASCII char, and the surrounding rule
        // silently fails. SelectorParser already accepts c >= 0x80 as
        // ident-start, so this aligns the source tokenizer with selector
        // tokenization.
        static bool IsLetter(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c >= 0x80;

        static bool IsDigit(char c) => c >= '0' && c <= '9';

        static bool IsHexDigit(char c) =>
            IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        static int HexValue(char c) {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            return 10 + (c - 'A');
        }

        static bool IsWhitespace(char c) =>
            c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        void Emit(CssTokenKind kind, string text) {
            tokens.Add(new CssToken {
                Kind = kind,
                Text = text,
                Line = tokenLine,
                Column = tokenCol
            });
        }

        bool AtEnd() => pos >= src.Length;
        char Peek() => pos < src.Length ? src[pos] : '\0';
        char PeekAt(int offset) => pos + offset < src.Length ? src[pos + offset] : '\0';

        void Advance() {
            if (pos < src.Length) {
                if (src[pos] == '\n') { line++; col = 1; } else { col++; }
                pos++;
            }
        }

        void MarkTokenStart() { tokenLine = line; tokenCol = col; }

        CssParseException Error(string msg) => new CssParseException(msg, line, col);
    }
}
