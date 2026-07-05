using System.Collections.Generic;
using System.Text;

namespace Weva.Parsing {
    public sealed class HtmlTokenizer {
        readonly string src;
        int pos;
        int line = 1;
        int col = 1;
        int tokenLine, tokenCol;
        readonly List<HtmlToken> tokens = new();
        readonly StringBuilder buf = new();

        public HtmlTokenizer(string source) {
            src = source ?? "";
        }

        public List<HtmlToken> Tokenize() {
            while (!AtEnd()) {
                if (Peek() == '<') {
                    FlushText();
                    ConsumeTag();
                } else {
                    ConsumeText();
                }
            }
            FlushText();
            tokens.Add(new HtmlToken { Kind = HtmlTokenKind.Eof, Line = line, Column = col });
            return tokens;
        }

        void ConsumeText() {
            if (buf.Length == 0) MarkTokenStart();
            if (Peek() == '&') {
                if (TryConsumeEntity(out var resolved)) {
                    buf.Append(resolved);
                    return;
                }
            }
            buf.Append(Peek());
            Advance();
        }

        void FlushText() {
            if (buf.Length == 0) return;
            tokens.Add(new HtmlToken {
                Kind = HtmlTokenKind.Text,
                Text = buf.ToString(),
                Line = tokenLine,
                Column = tokenCol
            });
            buf.Clear();
        }

        void ConsumeTag() {
            MarkTokenStart();
            Expect('<');
            if (Peek() == '!') {
                Advance();
                ConsumeMarkupDeclaration();
                return;
            }
            if (Peek() == '/') {
                Advance();
                ConsumeEndTag();
                return;
            }
            ConsumeStartTag();
        }

        void ConsumeStartTag() {
            if (!IsTagNameStart(Peek())) throw Error("Expected tag name after '<'");
            var name = ReadTagName();
            var attrs = new List<HtmlAttribute>();
            bool selfClosing = false;
            while (!AtEnd()) {
                SkipWhitespace();
                if (AtEnd()) break;
                char c = Peek();
                if (c == '>') break;
                if (c == '/') {
                    Advance();
                    SkipWhitespace();
                    if (Peek() != '>') throw Error("Expected '>' after '/'");
                    selfClosing = true;
                    break;
                }
                attrs.Add(ReadAttribute());
            }
            if (AtEnd() || Peek() != '>') throw Error("Unterminated start tag");
            Advance();
            tokens.Add(new HtmlToken {
                Kind = HtmlTokenKind.StartTag,
                Name = name,
                Attributes = attrs,
                SelfClosing = selfClosing,
                Line = tokenLine,
                Column = tokenCol
            });
        }

        void ConsumeEndTag() {
            if (!IsTagNameStart(Peek())) throw Error("Expected tag name after '</'");
            var name = ReadTagName();
            SkipWhitespace();
            if (AtEnd() || Peek() != '>') throw Error("Unterminated end tag");
            Advance();
            tokens.Add(new HtmlToken {
                Kind = HtmlTokenKind.EndTag,
                Name = name,
                Line = tokenLine,
                Column = tokenCol
            });
        }

        HtmlAttribute ReadAttribute() {
            var nameBuf = new StringBuilder();
            while (!AtEnd() && IsAttributeNameChar(Peek())) {
                nameBuf.Append(AsciiToLower(Peek()));
                Advance();
            }
            if (nameBuf.Length == 0) throw Error("Expected attribute name");
            string name = nameBuf.ToString();
            SkipWhitespace();
            if (Peek() != '=') return new HtmlAttribute(name, "");
            Advance();
            SkipWhitespace();
            return new HtmlAttribute(name, ReadAttributeValue());
        }

        string ReadAttributeValue() {
            char c = Peek();
            if (c == '"' || c == '\'') {
                char quote = c;
                Advance();
                var sb = new StringBuilder();
                while (!AtEnd() && Peek() != quote) {
                    if (Peek() == '&' && TryConsumeEntity(out var resolved)) {
                        sb.Append(resolved);
                        continue;
                    }
                    sb.Append(Peek());
                    Advance();
                }
                if (AtEnd()) throw Error("Unterminated attribute value");
                Advance();
                return sb.ToString();
            }
            var ub = new StringBuilder();
            while (!AtEnd()) {
                char ch = Peek();
                if (char.IsWhiteSpace(ch) || ch == '>' || ch == '/') break;
                if (ch == '&' && TryConsumeEntity(out var resolved)) {
                    ub.Append(resolved);
                    continue;
                }
                ub.Append(ch);
                Advance();
            }
            return ub.ToString();
        }

        void ConsumeMarkupDeclaration() {
            if (StartsWith("--")) {
                Advance(); Advance();
                ConsumeComment();
                return;
            }
            if (StartsWithIgnoreCase("DOCTYPE")) {
                for (int i = 0; i < 7; i++) Advance();
                ConsumeDoctype();
                return;
            }
            throw Error("Unknown markup declaration");
        }

        void ConsumeComment() {
            var sb = new StringBuilder();
            while (!AtEnd()) {
                if (Peek() == '-' && pos + 2 < src.Length && src[pos + 1] == '-' && src[pos + 2] == '>') {
                    Advance(); Advance(); Advance();
                    tokens.Add(new HtmlToken {
                        Kind = HtmlTokenKind.Comment,
                        Text = sb.ToString(),
                        Line = tokenLine,
                        Column = tokenCol
                    });
                    return;
                }
                sb.Append(Peek());
                Advance();
            }
            throw Error("Unterminated comment");
        }

        void ConsumeDoctype() {
            while (!AtEnd() && Peek() != '>') Advance();
            if (AtEnd()) throw Error("Unterminated DOCTYPE");
            Advance();
            tokens.Add(new HtmlToken { Kind = HtmlTokenKind.DocType, Line = tokenLine, Column = tokenCol });
        }

        string ReadTagName() {
            var sb = new StringBuilder();
            while (!AtEnd() && IsTagNameChar(Peek())) {
                sb.Append(AsciiToLower(Peek()));
                Advance();
            }
            return sb.ToString();
        }

        // ASCII-only lowercase for tag/attribute names. HTML names are ASCII per
        // the spec; folding inline during the append elides the second pass
        // that `sb.ToString().ToLowerInvariant()` would have made over the
        // already-allocated tag name string.
        static char AsciiToLower(char c) {
            return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }

        bool TryConsumeEntity(out string value) {
            value = null;
            int savedPos = pos, savedLine = line, savedCol = col;
            if (Peek() != '&') return false;
            Advance();
            if (AtEnd()) { Restore(savedPos, savedLine, savedCol); return false; }

            if (Peek() == '#') {
                Advance();
                bool hex = false;
                if (!AtEnd() && (Peek() == 'x' || Peek() == 'X')) { hex = true; Advance(); }
                int code = 0;
                int digits = 0;
                while (!AtEnd()) {
                    char ch = Peek();
                    int v = -1;
                    if (ch >= '0' && ch <= '9') v = ch - '0';
                    else if (hex && ch >= 'a' && ch <= 'f') v = 10 + (ch - 'a');
                    else if (hex && ch >= 'A' && ch <= 'F') v = 10 + (ch - 'A');
                    if (v < 0 || (!hex && v > 9)) break;
                    code = code * (hex ? 16 : 10) + v;
                    Advance();
                    digits++;
                }
                if (digits == 0 || AtEnd() || Peek() != ';') {
                    Restore(savedPos, savedLine, savedCol);
                    return false;
                }
                Advance();
                if (code < 0 || code > 0x10FFFF) { Restore(savedPos, savedLine, savedCol); return false; }
                value = char.ConvertFromUtf32(code);
                return true;
            }

            var sb = new StringBuilder();
            while (!AtEnd() && char.IsLetterOrDigit(Peek())) {
                sb.Append(Peek());
                Advance();
            }
            if (sb.Length == 0 || AtEnd() || Peek() != ';') {
                Restore(savedPos, savedLine, savedCol);
                return false;
            }
            Advance();
            if (HtmlEntities.Lookup(sb.ToString(), out var resolved)) {
                value = resolved;
                return true;
            }
            Restore(savedPos, savedLine, savedCol);
            return false;
        }

        void Restore(int p, int l, int c) { pos = p; line = l; col = c; }

        bool StartsWith(string s) {
            if (pos + s.Length > src.Length) return false;
            for (int i = 0; i < s.Length; i++) if (src[pos + i] != s[i]) return false;
            return true;
        }

        bool StartsWithIgnoreCase(string s) {
            if (pos + s.Length > src.Length) return false;
            for (int i = 0; i < s.Length; i++) {
                if (char.ToLowerInvariant(src[pos + i]) != char.ToLowerInvariant(s[i])) return false;
            }
            return true;
        }

        void Expect(char c) {
            if (Peek() != c) throw Error($"Expected '{c}'");
            Advance();
        }

        void SkipWhitespace() {
            while (!AtEnd() && char.IsWhiteSpace(Peek())) Advance();
        }

        bool AtEnd() => pos >= src.Length;
        char Peek() => pos < src.Length ? src[pos] : '\0';

        void Advance() {
            if (pos < src.Length) {
                if (src[pos] == '\n') { line++; col = 1; } else { col++; }
                pos++;
            }
        }

        void MarkTokenStart() { tokenLine = line; tokenCol = col; }

        static bool IsTagNameStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        static bool IsTagNameChar(char c) => IsTagNameStart(c) || (c >= '0' && c <= '9') || c == '-' || c == '_';
        static bool IsAttributeNameChar(char c) => !char.IsWhiteSpace(c) && c != '/' && c != '>' && c != '=' && c != '"' && c != '\'';

        HtmlParseException Error(string msg) => new HtmlParseException(msg, line, col);
    }
}
