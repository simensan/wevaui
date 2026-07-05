using System.Collections.Generic;
using System.Text;

namespace Weva.Designer
{
    /// <summary>
    /// Named design tokens — the single source of truth for colors, spacing, radii and
    /// the type scale. The editor offers tokens first in every picker so authors choose
    /// "Brand/Primary" or "Spacing M", not "#3A7BD5"/"16px"; the compiler emits them as
    /// CSS custom properties on <c>:root</c> and node styles reference them with
    /// <c>var(--…)</c>. Swapping a token restyles every element that uses it.
    ///
    /// Categories map to CSS-var prefixes: color, space, radius, font, shadow.
    /// </summary>
    public sealed class DesignTokens
    {
        /// <summary>Color name → CSS color (e.g. "brand/primary" → "#3a7bd5").</summary>
        public readonly Dictionary<string, string> Colors = new Dictionary<string, string>();
        /// <summary>Spacing name → px (gap / padding scale).</summary>
        public readonly Dictionary<string, double> Spacing = new Dictionary<string, double>();
        /// <summary>Corner-radius name → px.</summary>
        public readonly Dictionary<string, double> Radii = new Dictionary<string, double>();
        /// <summary>Type-scale name → px font-size.</summary>
        public readonly Dictionary<string, double> FontSizes = new Dictionary<string, double>();
        /// <summary>Shadow name → CSS box-shadow value.</summary>
        public readonly Dictionary<string, string> Shadows = new Dictionary<string, string>();
        /// <summary>Gradient name → CSS gradient value (e.g. "brand" → "linear-gradient(...)").</summary>
        public readonly Dictionary<string, string> Gradients = new Dictionary<string, string>();

        public DesignTokens Color(string name, string css) { Colors[name] = css; return this; }
        public DesignTokens Space(string name, double px) { Spacing[name] = px; return this; }
        public DesignTokens Radius(string name, double px) { Radii[name] = px; return this; }
        public DesignTokens Font(string name, double px) { FontSizes[name] = px; return this; }
        public DesignTokens Shadow(string name, string css) { Shadows[name] = css; return this; }
        public DesignTokens Gradient(string name, string css) { Gradients[name] = css; return this; }

        /// <summary>
        /// Resolve a color value that may be a token reference. <c>{token-name}</c>
        /// resolves to <c>var(--color-…)</c>; anything else passes through as raw CSS.
        /// Unknown tokens fall back to a visible magenta so the author notices (no
        /// silent downgrade — house rule). Returns null for null/empty input.
        /// </summary>
        public string ResolveColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (IsTokenRef(value))
            {
                string name = value.Substring(1, value.Length - 2);
                string varName = CssVarName("color", name);
                return Colors.ContainsKey(name) ? "var(--" + varName + ")" : "var(--" + varName + ", magenta)";
            }
            return value;
        }

        /// <summary>
        /// Resolve a <c>background</c> paint that may be a colour OR a gradient token.
        /// A <c>{token}</c> resolves to <c>var(--color-…)</c> if it's a colour token or
        /// <c>var(--gradient-…)</c> if it's a gradient token; an unknown token falls back to
        /// visible magenta. Raw values (a literal colour or a raw <c>linear-gradient(...)</c>)
        /// pass through. Returns null for null/empty input.
        /// </summary>
        public string ResolvePaint(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (IsTokenRef(value))
            {
                string name = value.Substring(1, value.Length - 2);
                if (Gradients.ContainsKey(name)) return "var(--" + CssVarName("gradient", name) + ")";
                string varName = CssVarName("color", name);
                return Colors.ContainsKey(name) ? "var(--" + varName + ")" : "var(--" + varName + ", magenta)";
            }
            return value;
        }

        /// <summary>Resolve a shadow value (raw CSS or <c>{token}</c>) to a box-shadow string.</summary>
        public string ResolveShadow(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (IsTokenRef(value))
            {
                string name = value.Substring(1, value.Length - 2);
                return "var(--" + CssVarName("shadow", name) + ")";
            }
            return value;
        }

        /// <summary>Resolve a <see cref="Dim"/> token reference to <c>var(--category-name)</c>.</summary>
        public string ResolveDimToken(string category, string tokenName)
            => "var(--" + CssVarName(category, tokenName) + ")";

        /// <summary>Emit the <c>:root { … }</c> custom-property block, or "" if no tokens.</summary>
        public string EmitRoot()
        {
            var sb = new StringBuilder();
            sb.Append(":root {\n");
            int count = 0;
            count += EmitColorVars(sb, "color", Colors);
            count += EmitLengthVars(sb, "space", Spacing);
            count += EmitLengthVars(sb, "radius", Radii);
            count += EmitLengthVars(sb, "font", FontSizes);
            count += EmitColorVars(sb, "shadow", Shadows); // string-valued, same shape
            count += EmitColorVars(sb, "gradient", Gradients); // string-valued, same shape
            sb.Append("}\n");
            return count == 0 ? "" : sb.ToString();
        }

        static int EmitColorVars(StringBuilder sb, string category, Dictionary<string, string> table)
        {
            foreach (var kv in table)
                sb.Append("  --").Append(CssVarName(category, kv.Key)).Append(": ").Append(kv.Value).Append(";\n");
            return table.Count;
        }

        static int EmitLengthVars(StringBuilder sb, string category, Dictionary<string, double> table)
        {
            foreach (var kv in table)
                sb.Append("  --").Append(CssVarName(category, kv.Key)).Append(": ").Append(DesignCssText.Px(kv.Value)).Append(";\n");
            return table.Count;
        }

        static bool IsTokenRef(string value)
            => value.Length > 2 && value[0] == '{' && value[value.Length - 1] == '}';

        /// <summary>Make a CSS-custom-property-safe identifier from a category + token name.</summary>
        internal static string CssVarName(string category, string name)
        {
            var sb = new StringBuilder(category.Length + name.Length + 1);
            sb.Append(category).Append('-');
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                sb.Append(ok ? c : '-');
            }
            return sb.ToString();
        }
    }
}
