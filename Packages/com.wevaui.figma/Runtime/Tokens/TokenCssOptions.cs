using System.Collections.Generic;

namespace Weva.Figma.Tokens
{
    public sealed class TokenCssOptions
    {
        /// <summary>Indentation for declarations inside a rule block.</summary>
        public string Indent = "  ";

        /// <summary>Optional prefix: "" → <c>--name</c>; "fig" → <c>--fig-name</c>.</summary>
        public string CustomPropertyPrefix = "";

        /// <summary>Attribute used for explicit theme selection: <c>[data-theme="…"]</c>.</summary>
        public string ThemeAttribute = "data-theme";

        /// <summary>Emit a <c>[data-theme="mode"]</c> block per mode of every multi-mode collection.</summary>
        public bool EmitThemeSelectors = true;

        /// <summary>
        /// When a single multi-mode collection has modes named like "light"/"dark",
        /// also emit <c>@media (prefers-color-scheme: …)</c> so the design follows
        /// the OS theme automatically (explicit <c>[data-theme]</c> still overrides).
        /// </summary>
        public bool EmitPrefersColorScheme = true;

        /// <summary>Unit appended to FLOAT tokens whose scope doesn't imply one. UI tokens are usually px.</summary>
        public string DefaultFloatUnit = "px";

        /// <summary>BOOLEAN variables have no CSS value; skip them (and warn) rather than emit 0/1.</summary>
        public bool SkipBooleans = true;

        /// <summary>Optional per-variable-name override of the FLOAT unit (sanitized name → unit, e.g. "z-index" → "").</summary>
        public Dictionary<string, string> FloatUnitOverrides;
    }

    public sealed class TokenCssResult
    {
        public string Css;
        public readonly List<string> Warnings = new List<string>();
        public int EmittedCount;
        public int SkippedCount;
    }
}
