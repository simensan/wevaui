using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;

namespace Weva.Figma.Json
{
    public sealed class JsonParseException : Exception
    {
        public int Index { get; }

        public JsonParseException(string message, int index)
            : base($"JSON parse error: {message} (at index {index})")
        {
            Index = index;
        }
    }

    /// <summary>
    /// A focused, allocation-light recursive-descent JSON reader covering the
    /// full JSON grammar (RFC 8259): objects, arrays, strings with escapes and
    /// <c>\uXXXX</c> (including surrogate pairs), numbers with fractions and
    /// exponents, and the <c>true</c>/<c>false</c>/<c>null</c> literals.
    ///
    /// Hand-rolled and dependency-free on purpose — it keeps the Figma bridge a
    /// zero-dependency package, matching the engine's hand-rolled HTML/CSS
    /// parser doctrine. Numbers parse under <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    public static class JsonParser
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int pos = 0;
            SkipWhitespace(text, ref pos);
            JsonValue value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new JsonParseException("unexpected trailing content", pos);
            return value;
        }

        public static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (Exception)
            {
                value = JsonValue.Null;
                return false;
            }
        }

        static JsonValue ParseValue(string s, ref int pos)
        {
            if (pos >= s.Length) throw new JsonParseException("unexpected end of input", pos);
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return JsonValue.NewString(ParseString(s, ref pos));
                case 't': ExpectLiteral(s, ref pos, "true"); return JsonValue.NewBool(true);
                case 'f': ExpectLiteral(s, ref pos, "false"); return JsonValue.NewBool(false);
                case 'n': ExpectLiteral(s, ref pos, "null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref pos);
                    throw new JsonParseException($"unexpected character '{c}'", pos);
            }
        }

        static JsonValue ParseObject(string s, ref int pos)
        {
            pos++; // consume '{'
            var map = new Dictionary<string, JsonValue>();
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == '}') { pos++; return JsonValue.NewObject(map); }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != '"')
                    throw new JsonParseException("expected object key string", pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (Peek(s, pos) != ':')
                    throw new JsonParseException("expected ':' after object key", pos);
                pos++; // consume ':'
                SkipWhitespace(s, ref pos);
                map[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                char d = Peek(s, pos);
                if (d == ',') { pos++; continue; }
                if (d == '}') { pos++; break; }
                throw new JsonParseException("expected ',' or '}' in object", pos);
            }
            return JsonValue.NewObject(map);
        }

        static JsonValue ParseArray(string s, ref int pos)
        {
            pos++; // consume '['
            var list = new List<JsonValue>();
            SkipWhitespace(s, ref pos);
            if (Peek(s, pos) == ']') { pos++; return JsonValue.NewArray(list); }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                list.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                char d = Peek(s, pos);
                if (d == ',') { pos++; continue; }
                if (d == ']') { pos++; break; }
                throw new JsonParseException("expected ',' or ']' in array", pos);
            }
            return JsonValue.NewArray(list);
        }

        static string ParseString(string s, ref int pos)
        {
            pos++; // consume opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new JsonParseException("unterminated string", pos);
                char c = s[pos++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (pos >= s.Length) throw new JsonParseException("unterminated escape", pos);
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u': sb.Append(ParseUnicodeEscape(s, ref pos)); break;
                        default: throw new JsonParseException($"invalid escape '\\{e}'", pos - 1);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static char ParseUnicodeEscape(string s, ref int pos)
        {
            if (pos + 4 > s.Length) throw new JsonParseException("truncated \\u escape", pos);
            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                code = (code << 4) | HexDigit(s[pos], pos);
                pos++;
            }
            return (char)code;
        }

        static int HexDigit(char c, int pos)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new JsonParseException($"invalid hex digit '{c}'", pos);
        }

        static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (Peek(s, pos) == '-') pos++;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            if (pos < s.Length && s[pos] == '.')
            {
                pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
            {
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            string slice = s.Substring(start, pos - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                throw new JsonParseException($"invalid number '{slice}'", start);
            return JsonValue.NewNumber(n);
        }

        static void ExpectLiteral(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                throw new JsonParseException($"expected '{literal}'", pos);
            pos += literal.Length;
        }

        static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }

        static char Peek(string s, int pos) => pos < s.Length ? s[pos] : '\0';
    }
}
