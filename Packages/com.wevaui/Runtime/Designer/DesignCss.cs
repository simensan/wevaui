using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Designer
{
    /// <summary>
    /// An ordered set of CSS declarations for one rule. Insertion order is preserved
    /// so compiler output is deterministic and diff-friendly. (Mirrors the figma
    /// package's CssBlock; kept independent because core can't depend upward on the
    /// figma package — the two converge in M8.)
    /// </summary>
    public sealed class CssDecls
    {
        readonly List<KeyValuePair<string, string>> _decls = new List<KeyValuePair<string, string>>();

        public bool IsEmpty => _decls.Count == 0;
        public IReadOnlyList<KeyValuePair<string, string>> Declarations => _decls;

        /// <summary>Append a declaration. No-op if <paramref name="value"/> is null/empty.</summary>
        public void Set(string property, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            _decls.Add(new KeyValuePair<string, string>(property, value));
        }

        public bool Has(string property)
        {
            foreach (var d in _decls) if (d.Key == property) return true;
            return false;
        }

        public void RenderInto(StringBuilder sb, string indent)
        {
            foreach (var d in _decls)
                sb.Append(indent).Append(d.Key).Append(": ").Append(d.Value).Append(";\n");
        }
    }

    /// <summary>Small text helpers for the design compiler (px formatting, escaping).</summary>
    internal static class DesignCssText
    {
        /// <summary>Format a px length without a trailing ".0" and with invariant culture.</summary>
        public static string Px(double v)
        {
            string n = v == (long)v
                ? ((long)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.####", CultureInfo.InvariantCulture);
            return n + "px";
        }

        public static string Num(double v) =>
            v == (long)v
                ? ((long)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.####", CultureInfo.InvariantCulture);

        public static string EscapeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Quote a URL for use inside CSS <c>url("…")</c>, escaping backslashes and
        /// double-quotes so a crafted path can't break out of the value.
        /// </summary>
        public static string Url(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string EscapeAttr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
