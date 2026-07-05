using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Designer.Serialization
{
    internal enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A tiny, dependency-free JSON DOM used to persist <see cref="DesignDocument"/>.
    /// Hand-rolled (matching the codebase's hand-rolled HTML/CSS parsers) so it works
    /// identically under Unity and the headless test runner — no System.Text.Json or
    /// UnityEngine.JsonUtility. Tolerant on read (unknown keys are simply ignored by
    /// callers), deterministic on write (insertion-ordered, 2-space indented).
    /// </summary>
    internal sealed class JsonVal
    {
        public JsonKind Kind;
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonVal> Items;
        public Dictionary<string, JsonVal> Members;

        public static JsonVal NewObject() => new JsonVal { Kind = JsonKind.Object, Members = new Dictionary<string, JsonVal>() };
        public static JsonVal NewArray() => new JsonVal { Kind = JsonKind.Array, Items = new List<JsonVal>() };
        public static JsonVal Of(string s) => s == null ? new JsonVal { Kind = JsonKind.Null } : new JsonVal { Kind = JsonKind.String, Str = s };
        public static JsonVal Of(double n) => new JsonVal { Kind = JsonKind.Number, Number = n };
        public static JsonVal Of(bool b) => new JsonVal { Kind = JsonKind.Bool, Bool = b };

        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        public void Set(string key, JsonVal v) { Members[key] = v; }
        public void Add(JsonVal v) { Items.Add(v); }

        /// <summary>Member lookup; returns null if absent or not an object.</summary>
        public JsonVal Get(string key)
        {
            if (Members != null && Members.TryGetValue(key, out var v)) return v;
            return null;
        }

        public string AsString(string fallback = null) => Kind == JsonKind.String ? Str : fallback;
        public double AsDouble(double fallback = 0) => Kind == JsonKind.Number ? Number : fallback;
        public int AsInt(int fallback = 0) => Kind == JsonKind.Number ? (int)Number : fallback;
        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? Bool : fallback;

        public string GetString(string key, string fallback = null) => Get(key)?.AsString(fallback) ?? fallback;
        public double GetDouble(string key, double fallback = 0) { var v = Get(key); return v != null ? v.AsDouble(fallback) : fallback; }
        public bool GetBool(string key, bool fallback = false) { var v = Get(key); return v != null ? v.AsBool(fallback) : fallback; }
    }

    internal static class Json
    {
        // --- Writing ---

        public static string Write(JsonVal v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, JsonVal v, int indent)
        {
            if (v == null) { sb.Append("null"); return; }
            switch (v.Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(v.Bool ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(FormatNumber(v.Number)); break;
                case JsonKind.String: WriteString(sb, v.Str); break;
                case JsonKind.Array: WriteArray(sb, v, indent); break;
                case JsonKind.Object: WriteObject(sb, v, indent); break;
            }
        }

        static void WriteArray(StringBuilder sb, JsonVal v, int indent)
        {
            if (v.Items.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            string pad = new string(' ', (indent + 1) * 2);
            for (int i = 0; i < v.Items.Count; i++)
            {
                sb.Append(pad);
                WriteValue(sb, v.Items[i], indent + 1);
                if (i < v.Items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(new string(' ', indent * 2)).Append(']');
        }

        static void WriteObject(StringBuilder sb, JsonVal v, int indent)
        {
            if (v.Members.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            string pad = new string(' ', (indent + 1) * 2);
            int i = 0, n = v.Members.Count;
            foreach (var kv in v.Members)
            {
                sb.Append(pad);
                WriteString(sb, kv.Key);
                sb.Append(": ");
                WriteValue(sb, kv.Value, indent + 1);
                if (++i < n) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(new string(' ', indent * 2)).Append('}');
        }

        static string FormatNumber(double d)
        {
            if (d == (long)d) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // --- Parsing ---

        public static JsonVal Parse(string s)
        {
            int i = 0;
            JsonVal v = ParseValue(s, ref i);
            SkipWs(s, ref i);
            return v;
        }

        static JsonVal ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new JsonException("unexpected end of input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return JsonVal.Of(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return JsonVal.Of(true);
                case 'f': Expect(s, ref i, "false"); return JsonVal.Of(false);
                case 'n': Expect(s, ref i, "null"); return new JsonVal { Kind = JsonKind.Null };
                default: return ParseNumber(s, ref i);
            }
        }

        static JsonVal ParseObject(string s, ref int i)
        {
            var obj = JsonVal.NewObject();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new JsonException("expected object key");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new JsonException("expected ':'");
                i++;
                obj.Members[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new JsonException("expected ',' or '}'");
            }
            return obj;
        }

        static JsonVal ParseArray(string s, ref int i)
        {
            var arr = JsonVal.NewArray();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            while (true)
            {
                arr.Items.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new JsonException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new JsonException("expected ',' or ']'");
            }
            return arr;
        }

        static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new JsonException("bad \\u escape");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw new JsonException("bad escape \\" + e);
                    }
                }
                else sb.Append(c);
            }
            throw new JsonException("unterminated string");
        }

        static JsonVal ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            string num = s.Substring(start, i - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new JsonException("bad number '" + num + "'");
            return JsonVal.Of(d);
        }

        static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || s.Substring(i, literal.Length) != literal)
                throw new JsonException("expected '" + literal + "'");
            i += literal.Length;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }
    }

    internal sealed class JsonException : System.Exception
    {
        public JsonException(string message) : base(message) { }
    }
}
