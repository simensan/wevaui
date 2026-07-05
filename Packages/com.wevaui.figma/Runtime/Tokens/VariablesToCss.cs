using System.Collections.Generic;
using System.Text;
using Weva.Figma.Json;

namespace Weva.Figma.Tokens
{
    /// <summary>
    /// Emits a CSS stylesheet of custom properties from a Figma Variables
    /// document. Layout of the output:
    ///
    /// <list type="number">
    ///   <item><c>:root { … }</c> — every variable at its collection's default mode.</item>
    ///   <item><c>@media (prefers-color-scheme: …)</c> — for a single themed
    ///         collection whose modes are named light/dark (OS-driven).</item>
    ///   <item><c>[data-theme="mode"] { … }</c> — explicit per-mode overrides
    ///         (highest precedence; emitted last).</item>
    /// </list>
    ///
    /// Output is deterministic: collections are visited in (name, id) order,
    /// variables in their collection's authored <c>variableIds</c> order, modes
    /// in authored order.
    /// </summary>
    public static class VariablesToCss
    {
        static readonly HashSet<string> UnitlessScopes = new HashSet<string>
        {
            "OPACITY", "FONT_WEIGHT", "LINE_HEIGHT",
        };

        // Scopes whose number is a length.
        static readonly HashSet<string> PixelScopes = new HashSet<string>
        {
            "WIDTH_HEIGHT", "GAP", "CORNER_RADIUS", "STROKE_FLOAT",
            "FONT_SIZE", "LETTER_SPACING", "PARAGRAPH_SPACING", "PARAGRAPH_INDENT",
        };

        public static TokenCssResult Build(string json, TokenCssOptions options = null)
            => Build(FigmaVariablesDocument.Parse(json), options);

        public static TokenCssResult Build(FigmaVariablesDocument doc, TokenCssOptions options = null)
        {
            options = options ?? new TokenCssOptions();
            var result = new TokenCssResult();
            var sb = new StringBuilder();

            List<FigmaVariableCollection> collections = OrderedCollections(doc);

            // 1. :root — default mode of every variable.
            sb.Append(":root {\n");
            foreach (FigmaVariableCollection coll in collections)
                foreach (FigmaVariable v in OrderedVariables(doc, coll))
                    AppendDecl(sb, doc, coll, v, coll.DefaultModeId, options, result);
            sb.Append("}\n");

            var themed = collections.FindAll(c => c.IsMultiMode);
            bool disambiguate = themed.Count > 1;

            // 2. prefers-color-scheme (single themed collection only).
            if (options.EmitPrefersColorScheme && themed.Count == 1)
            {
                FigmaVariableCollection coll = themed[0];
                foreach (FigmaVariableMode mode in coll.Modes)
                {
                    if (mode.ModeId == coll.DefaultModeId) continue; // already in :root
                    string scheme = DetectScheme(mode.Name);
                    if (scheme == null) continue;
                    sb.Append('\n');
                    sb.Append($"@media (prefers-color-scheme: {scheme}) {{\n");
                    sb.Append("  :root {\n");
                    foreach (FigmaVariable v in OrderedVariables(doc, coll))
                        AppendDecl(sb, doc, coll, v, mode.ModeId, options, result, extraIndent: "  ");
                    sb.Append("  }\n");
                    sb.Append("}\n");
                }
            }

            // 3. explicit [data-theme] blocks (win over :root and @media on source order).
            if (options.EmitThemeSelectors)
            {
                foreach (FigmaVariableCollection coll in themed)
                {
                    foreach (FigmaVariableMode mode in coll.Modes)
                    {
                        string theme = disambiguate
                            ? CssText.SanitizeIdent(coll.Name) + "-" + CssText.SanitizeIdent(mode.Name)
                            : CssText.SanitizeIdent(mode.Name);
                        sb.Append('\n');
                        sb.Append($"[{options.ThemeAttribute}=\"{theme}\"] {{\n");
                        foreach (FigmaVariable v in OrderedVariables(doc, coll))
                            AppendDecl(sb, doc, coll, v, mode.ModeId, options, result);
                        sb.Append("}\n");
                    }
                }
            }

            if (disambiguate)
                result.Warnings.Add($"{themed.Count} multi-mode collections present; [data-theme] values are prefixed with the collection name to avoid collisions.");

            result.Css = sb.ToString();
            return result;
        }

        static void AppendDecl(StringBuilder sb, FigmaVariablesDocument doc, FigmaVariableCollection coll,
            FigmaVariable v, string modeId, TokenCssOptions options, TokenCssResult result, string extraIndent = "")
        {
            string value = ResolveValue(doc, coll, v, modeId, options, result);
            if (value == null) return;
            string prop = CssText.CustomProperty(v.Name, options.CustomPropertyPrefix);
            sb.Append(extraIndent).Append(options.Indent).Append(prop).Append(": ").Append(value).Append(";\n");
            result.EmittedCount++;
        }

        static string ResolveValue(FigmaVariablesDocument doc, FigmaVariableCollection coll,
            FigmaVariable v, string modeId, TokenCssOptions options, TokenCssResult result)
        {
            JsonValue raw;
            if (!v.ValuesByMode.TryGetValue(modeId, out raw) || raw.IsNull)
            {
                // Fall back to the collection's default mode if this mode is absent.
                if (modeId != coll.DefaultModeId && v.ValuesByMode.TryGetValue(coll.DefaultModeId, out raw) && !raw.IsNull)
                {
                    // ok, use default
                }
                else
                {
                    result.SkippedCount++;
                    result.Warnings.Add($"'{v.Name}' has no value for mode '{modeId}'.");
                    return null;
                }
            }

            // Alias → var(--other).
            if (raw.IsObject && raw["type"].AsString() == "VARIABLE_ALIAS")
            {
                string targetId = raw["id"].AsString(null);
                if (targetId != null && doc.Variables.TryGetValue(targetId, out FigmaVariable target))
                    return "var(" + CssText.CustomProperty(target.Name, options.CustomPropertyPrefix) + ")";
                result.SkippedCount++;
                result.Warnings.Add($"'{v.Name}' aliases unknown variable '{targetId}'.");
                return null;
            }

            switch (v.Type)
            {
                case FigmaVariableType.Color:
                    return CssText.Color(
                        raw["r"].AsDouble(), raw["g"].AsDouble(), raw["b"].AsDouble(),
                        raw.Has("a") ? raw["a"].AsDouble(1) : 1);

                case FigmaVariableType.Float:
                    return CssText.Number(raw.AsDouble()) + UnitForFloat(v, options);

                case FigmaVariableType.String:
                    return raw.AsString("");

                case FigmaVariableType.Bool:
                    if (options.SkipBooleans)
                    {
                        result.SkippedCount++;
                        result.Warnings.Add($"'{v.Name}' is a BOOLEAN; skipped (no CSS representation).");
                        return null;
                    }
                    return raw.AsBool() ? "1" : "0";

                default:
                    result.SkippedCount++;
                    result.Warnings.Add($"'{v.Name}' has unsupported type; skipped.");
                    return null;
            }
        }

        static string UnitForFloat(FigmaVariable v, TokenCssOptions options)
        {
            if (options.FloatUnitOverrides != null
                && options.FloatUnitOverrides.TryGetValue(CssText.SanitizeIdent(v.Name), out string unit))
                return unit;

            foreach (string s in v.Scopes)
                if (UnitlessScopes.Contains(s)) return "";
            foreach (string s in v.Scopes)
                if (PixelScopes.Contains(s)) return "px";
            return options.DefaultFloatUnit ?? "";
        }

        static string DetectScheme(string modeName)
        {
            if (string.IsNullOrEmpty(modeName)) return null;
            string n = modeName.ToLowerInvariant();
            if (n.Contains("dark")) return "dark";
            if (n.Contains("light")) return "light";
            return null;
        }

        static List<FigmaVariableCollection> OrderedCollections(FigmaVariablesDocument doc)
        {
            var list = new List<FigmaVariableCollection>(doc.Collections.Values);
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Name ?? "", b.Name ?? "");
                return c != 0 ? c : string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
            });
            return list;
        }

        static List<FigmaVariable> OrderedVariables(FigmaVariablesDocument doc, FigmaVariableCollection coll)
        {
            var list = new List<FigmaVariable>();
            var seen = new HashSet<string>();
            // Authored order first.
            foreach (string id in coll.VariableIds)
                if (doc.Variables.TryGetValue(id, out FigmaVariable v) && seen.Add(id))
                    list.Add(v);
            // Any variables that name this collection but weren't listed, sorted by name.
            var extra = new List<FigmaVariable>();
            foreach (FigmaVariable v in doc.Variables.Values)
                if (v.CollectionId == coll.Id && !seen.Contains(v.Id))
                    extra.Add(v);
            extra.Sort((a, b) => string.CompareOrdinal(a.Name ?? "", b.Name ?? ""));
            list.AddRange(extra);
            return list;
        }
    }
}
