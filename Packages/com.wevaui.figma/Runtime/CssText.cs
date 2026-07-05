using System.Globalization;
using System.Text;

namespace Weva.Figma
{
    /// <summary>
    /// Helpers that format values into CSS text exactly the way the Weva
    /// subset expects. Centralised so every emitter (tokens, styles, layout)
    /// produces byte-identical, invariant-culture output.
    /// </summary>
    public static class CssText
    {
        /// <summary>Invariant number, no exponent, trailing zeros trimmed: 16, 16.5, 0.5.</summary>
        public static string Number(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>A pixel length, snapped to 0.01px to keep noisy Figma coordinates clean.</summary>
        public static string Px(double v) => Number(System.Math.Round(v, 2)) + "px";

        /// <summary>
        /// A Figma RGBA (channels in 0..1) as a CSS color. Opaque colors emit
        /// <c>rgb()</c>; otherwise <c>rgba()</c> — both are in the Weva subset.
        /// </summary>
        public static string Color(double r, double g, double b, double a)
        {
            int R = ToByte(r), G = ToByte(g), B = ToByte(b);
            if (a >= 0.9995)
                return $"rgb({R}, {G}, {B})";
            string alpha = a.ToString("0.####", CultureInfo.InvariantCulture);
            return $"rgba({R}, {G}, {B}, {alpha})";
        }

        static int ToByte(double channel01)
        {
            int v = (int)System.Math.Round(channel01 * 255.0);
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return v;
        }

        /// <summary>
        /// Turn a Figma name ("color/Brand/Primary") into a CSS custom-property
        /// name ("--color-brand-primary"). Lowercased; any run of non
        /// <c>[a-z0-9]</c> collapses to a single '-'; edges trimmed. An optional
        /// prefix becomes "--prefix-…".
        /// </summary>
        public static string CustomProperty(string name, string prefix = "")
        {
            string core = SanitizeIdent(name);
            if (core.Length == 0) core = "token";
            string p = SanitizeIdent(prefix);
            return p.Length > 0 ? "--" + p + "-" + core : "--" + core;
        }

        /// <summary>Lowercase, hyphenate, trim — the identifier core without the "--".</summary>
        public static string SanitizeIdent(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool lastDash = false;
            foreach (char ch in name)
            {
                char c = char.ToLowerInvariant(ch);
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (ok)
                {
                    sb.Append(c);
                    lastDash = false;
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
            return sb.ToString();
        }
    }
}
