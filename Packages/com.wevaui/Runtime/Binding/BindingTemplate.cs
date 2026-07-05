using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Binding {
    public sealed class BindingTemplate {
        public readonly struct Segment {
            public readonly string Literal;
            public readonly BindingPath Path;
            public readonly bool IsBinding;

            Segment(string literal, BindingPath path, bool isBinding) {
                Literal = literal;
                Path = path;
                IsBinding = isBinding;
            }

            public static Segment AsLiteral(string s) => new Segment(s, default, false);
            public static Segment AsBinding(BindingPath p) => new Segment(null, p, true);
        }

        readonly Segment[] segments;
        readonly string source;
        readonly bool hasAnyBinding;

        public IReadOnlyList<Segment> Segments => segments;
        public bool HasBinding => hasAnyBinding;
        public string Source => source;

        BindingTemplate(Segment[] segs, string source, bool hasAnyBinding) {
            this.segments = segs;
            this.source = source;
            this.hasAnyBinding = hasAnyBinding;
        }

        public static BindingTemplate Parse(string raw) {
            return Parse(raw, 0, 0);
        }

        public static BindingTemplate Parse(string raw, int startLine, int startColumn) {
            if (raw == null) raw = "";
            var list = new List<Segment>();
            var literal = new StringBuilder();
            bool anyBinding = false;
            int i = 0;
            int line = startLine > 0 ? startLine : 1;
            int col = startColumn > 0 ? startColumn : 1;

            while (i < raw.Length) {
                char c = raw[i];

                // Escape: \{{ → literal {{ ;  \}} → literal }}
                if (c == '\\' && i + 2 < raw.Length) {
                    if (raw[i + 1] == '{' && raw[i + 2] == '{') {
                        literal.Append("{{");
                        Advance(raw, ref i, ref line, ref col, 3);
                        continue;
                    }
                    if (raw[i + 1] == '}' && raw[i + 2] == '}') {
                        literal.Append("}}");
                        Advance(raw, ref i, ref line, ref col, 3);
                        continue;
                    }
                }

                if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '{') {
                    if (literal.Length > 0) {
                        list.Add(Segment.AsLiteral(literal.ToString()));
                        literal.Length = 0;
                    }
                    int markerLine = line;
                    int markerCol = col;
                    Advance(raw, ref i, ref line, ref col, 2);
                    int contentStart = i;
                    int contentLine = line;
                    int contentCol = col;
                    int closeAt = -1;
                    while (i < raw.Length) {
                        if (i + 1 < raw.Length && raw[i] == '}' && raw[i + 1] == '}') {
                            closeAt = i;
                            break;
                        }
                        Advance(raw, ref i, ref line, ref col, 1);
                    }
                    if (closeAt < 0) {
                        throw new BindingException(
                            "Unmatched '{{' in binding template.", markerLine, markerCol);
                    }
                    var inner = raw.Substring(contentStart, closeAt - contentStart);
                    var trimmed = inner.Trim();
                    if (trimmed.Length == 0) {
                        throw new BindingException(
                            "Empty binding expression '{{ }}'.", markerLine, markerCol);
                    }
                    BindingPath path;
                    try {
                        path = BindingPath.Parse(trimmed);
                    } catch (BindingException ex) {
                        throw new BindingException(ex.Message, contentLine, contentCol, ex);
                    }
                    list.Add(Segment.AsBinding(path));
                    anyBinding = true;
                    Advance(raw, ref i, ref line, ref col, 2);
                    continue;
                }

                if (c == '}' && i + 1 < raw.Length && raw[i + 1] == '}') {
                    throw new BindingException(
                        "Unmatched '}}' in binding template.", line, col);
                }

                literal.Append(c);
                Advance(raw, ref i, ref line, ref col, 1);
            }

            if (literal.Length > 0) {
                list.Add(Segment.AsLiteral(literal.ToString()));
            }

            return new BindingTemplate(list.ToArray(), raw, anyBinding);
        }

        static void Advance(string s, ref int i, ref int line, ref int col, int n) {
            for (int k = 0; k < n && i < s.Length; k++) {
                if (s[i] == '\n') { line++; col = 1; } else { col++; }
                i++;
            }
        }

        // Reused render buffer. Binding updates run on the main thread; the
        // take-then-restore dance keeps a re-entrant Render (a resolved
        // property getter that itself renders a template) correct by giving
        // the nested call a fresh builder instead of corrupting the outer one.
        [ThreadStatic] static StringBuilder scratchCache;

        public string Render(object context) {
            return Render(context, null);
        }

        // Render with an unchanged-result fast path: when the rendered output
        // is character-equal to `ifUnchanged`, returns the SAME `ifUnchanged`
        // reference and allocates nothing. This is the per-frame binding poll
        // — on idle frames every template renders into the reused buffer,
        // matches the value already in the DOM, and produces zero garbage.
        public string Render(object context, string ifUnchanged) {
            if (segments.Length == 0) return string.Empty;
            if (segments.Length == 1 && !segments[0].IsBinding) return segments[0].Literal;

            var sb = scratchCache;
            scratchCache = null;
            if (sb == null) sb = new StringBuilder(64); else sb.Clear();

            for (int i = 0; i < segments.Length; i++) {
                var seg = segments[i];
                if (!seg.IsBinding) {
                    sb.Append(seg.Literal);
                } else {
                    AppendValue(sb, context, seg.Path);
                }
            }

            string result = ifUnchanged != null && BuilderEquals(sb, ifUnchanged)
                ? ifUnchanged
                : sb.ToString();
            scratchCache = sb;
            return result;
        }

        static bool BuilderEquals(StringBuilder sb, string s) {
            if (sb.Length != s.Length) return false;
            for (int i = 0; i < s.Length; i++) {
                if (sb[i] != s[i]) return false;
            }
            return true;
        }

        static void AppendValue(StringBuilder sb, object context, BindingPath path) {
            if (context == null) return;
            if (!BindingResolver.TryResolve(context, path, out var value)) return;
            if (value == null) return;
            // Common value types append without an intermediate string.
            // Integers format manually so the output stays invariant-culture
            // (StringBuilder.Append(int) uses the current culture's negative
            // sign). Everything else falls back to FormatValue semantics.
            switch (value) {
                case string s: sb.Append(s); break;
                case int i: AppendInvariant(sb, i); break;
                case long l: AppendInvariant(sb, l); break;
                case bool b: sb.Append(b ? "True" : "False"); break;
                case char c: sb.Append(c); break;
                case short s16: AppendInvariant(sb, s16); break;
                case byte u8: AppendInvariant(sb, u8); break;
                case sbyte s8: AppendInvariant(sb, s8); break;
                case ushort u16: AppendInvariant(sb, u16); break;
                case uint u32: AppendInvariant(sb, u32); break;
                case ulong u64: AppendDigits(sb, u64); break;
                default: sb.Append(FormatValue(value)); break;
            }
        }

        static void AppendInvariant(StringBuilder sb, long value) {
            if (value < 0) {
                sb.Append('-');
                // Negate via unsigned arithmetic so long.MinValue doesn't overflow.
                AppendDigits(sb, unchecked((ulong)(-(value + 1))) + 1);
            } else {
                AppendDigits(sb, (ulong)value);
            }
        }

        static void AppendDigits(StringBuilder sb, ulong value) {
            // 20 chars covers ulong.MaxValue.
            Span<char> buf = stackalloc char[20];
            int pos = buf.Length;
            do {
                buf[--pos] = (char)('0' + (int)(value % 10));
                value /= 10;
            } while (value != 0);
            for (int i = pos; i < buf.Length; i++) sb.Append(buf[i]);
        }

        static string FormatValue(object value) {
            switch (value) {
                case string s: return s;
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }
    }
}
