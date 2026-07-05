using System;
using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade {
    // CSS Properties and Values API Level 1 — typed custom property registry.
    //
    // A stylesheet can declare a typed custom property via:
    //
    //   @property --my-prop {
    //     syntax: "<length>";
    //     initial-value: 0px;
    //     inherits: false;
    //   }
    //
    // This registry stores one PropertyDescriptor per custom-property name.
    // The CascadeEngine consults it when processing custom properties to:
    //   1. Override the default "inherits: true" behaviour.
    //   2. Supply the typed initial-value when no rule sets the property.
    //   3. Validate authored values against the declared syntax and fall back
    //      to the initial-value when the authored value doesn't match.
    //
    // Instances are per-CascadeEngine (not global) so stylesheet-isolated
    // tests don't cross-contaminate each other.
    public sealed class AtPropertyRegistry {
        readonly Dictionary<string, PropertyDescriptor> descriptors = new Dictionary<string, PropertyDescriptor>(StringComparer.Ordinal);

        // Register a descriptor. Later registrations for the same name win
        // (last-write per cascade source-order, consistent with how the
        // cascade treats duplicate at-rules).
        public void Register(PropertyDescriptor descriptor) {
            if (descriptor == null) return;
            if (string.IsNullOrEmpty(descriptor.Name)) return;
            descriptors[descriptor.Name] = descriptor;
        }

        // Returns true when a descriptor exists for the given custom-property name.
        public bool TryGet(string name, out PropertyDescriptor descriptor) {
            if (name == null) { descriptor = null; return false; }
            return descriptors.TryGetValue(name, out descriptor);
        }

        // Returns true when the custom property is declared with `inherits: false`.
        // Unregistered properties inherit by default (CSS Custom Properties L1 §2).
        public bool IsNonInheriting(string name) {
            if (name == null) return false;
            if (descriptors.TryGetValue(name, out var d)) return !d.Inherits;
            return false;
        }

        // Returns the typed initial-value for the named property, or null if
        // unregistered (callers treat null as "no initial-value constraint").
        public string GetInitialValue(string name) {
            if (name == null) return null;
            if (descriptors.TryGetValue(name, out var d)) return d.InitialValue;
            return null;
        }

        // Validates `value` against the descriptor's syntax string.
        // Returns true when valid (or when the descriptor uses "*" universal syntax).
        // Returns false when the value violates the typed syntax — the cascade
        // must then substitute the descriptor's initial-value instead.
        public bool ValidateValue(string name, string value) {
            if (!descriptors.TryGetValue(name, out var descriptor)) return true; // unregistered: always valid
            return PropertyDescriptor.Validate(descriptor.Syntax, value);
        }

        public int Count => descriptors.Count;

        public IEnumerable<PropertyDescriptor> All => descriptors.Values;
    }

    // Descriptor for a single `@property` declaration.
    public sealed class PropertyDescriptor {
        public string Name { get; }
        public string Syntax { get; }
        public string InitialValue { get; }
        public bool Inherits { get; }

        public PropertyDescriptor(string name, string syntax, string initialValue, bool inherits) {
            Name = name;
            Syntax = syntax;
            InitialValue = initialValue;
            Inherits = inherits;
        }

        // Attempts to build a PropertyDescriptor from the three raw descriptor strings.
        // Returns null when any required piece is missing/invalid (causes at-rule discard).
        public static PropertyDescriptor TryCreate(string name, string syntax, string initialValue, string inheritsText) {
            // All three descriptors are required.
            if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal)) return null;
            if (string.IsNullOrEmpty(syntax)) return null;
            if (initialValue == null) return null; // missing descriptor (empty string is valid for universal syntax)
            if (string.IsNullOrEmpty(inheritsText)) return null;

            // Parse `inherits` keyword.
            bool inherits;
            string lower = inheritsText.Trim().ToLowerInvariant();
            if (lower == "true") inherits = true;
            else if (lower == "false") inherits = false;
            else return null; // invalid keyword — discard

            // Strip outer quotes from syntax if present (authors may write
            // both  syntax: <length>  and  syntax: "<length>").
            string synTrimmed = syntax.Trim();
            if (synTrimmed.Length >= 2 && synTrimmed[0] == '"' && synTrimmed[synTrimmed.Length - 1] == '"') {
                synTrimmed = synTrimmed.Substring(1, synTrimmed.Length - 2).Trim();
            }
            if (string.IsNullOrEmpty(synTrimmed)) return null;

            // CSS Properties & Values L1 §3.4: initial-value must not contain
            // any variable references (var() or env()).  A rule with such a value
            // is invalid and must be silently discarded.
            if (ContainsVariableReference(initialValue)) return null;

            // Validate initial-value against syntax.
            // Universal syntax ("*") accepts any token sequence including empty.
            // For typed syntax, require the value to parse without error.
            if (synTrimmed != "*") {
                if (!Validate(synTrimmed, initialValue)) return null;
            }

            return new PropertyDescriptor(name, synTrimmed, initialValue, inherits);
        }

        // CSS Properties & Values L1 §3.4 — returns true when the string
        // contains a var() or env() function call, indicating a variable
        // reference.  Such values are forbidden in @property initial-value.
        static bool ContainsVariableReference(string value) {
            if (string.IsNullOrEmpty(value)) return false;
            string lower = value.ToLowerInvariant();
            return lower.Contains("var(") || lower.Contains("env(");
        }

        // CSS Properties & Values L1 — validate a value string against a
        // single syntax component. v1 covers the spec-required primitives.
        // Supports `|`-separated alternatives (e.g. `"<length> | <percentage>"`).
        // Does NOT yet handle `+` (space-separated list) or `#` (comma-separated
        // list) multipliers — that is a v2 enhancement.
        public static bool Validate(string syntax, string value) {
            if (syntax == null || syntax == "*") return true;
            if (value == null) value = "";
            string valueTrimmed = value.Trim();

            // Split on top-level `|` to get alternatives.
            string[] alternatives = SplitAlternatives(syntax);
            foreach (string alt in alternatives) {
                string altTrimmed = alt.Trim();
                if (MatchesSingle(altTrimmed, valueTrimmed)) return true;
            }
            return false;
        }

        static string[] SplitAlternatives(string syntax) {
            // Simple split on `|` that avoids splitting inside `<...>`.
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < syntax.Length; i++) {
                if (syntax[i] == '|') {
                    parts.Add(syntax.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(syntax.Substring(start));
            return parts.ToArray();
        }

        static bool MatchesSingle(string altSyntax, string value) {
            // Strip list multipliers from syntax component for v1 matching.
            string syn = altSyntax.TrimEnd('+', '#');
            switch (syn) {
                case "*":
                    return true;
                case "<length>":
                    return IsLength(value);
                case "<number>":
                    return IsNumber(value);
                case "<integer>":
                    return IsInteger(value);
                case "<percentage>":
                    return IsPercentage(value);
                case "<color>":
                    return IsColor(value);
                case "<angle>":
                    return IsAngle(value);
                case "<time>":
                    return IsTime(value);
                case "<resolution>":
                    return IsResolution(value);
                case "<url>":
                    return value.StartsWith("url(", StringComparison.OrdinalIgnoreCase);
                case "<image>":
                    return value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("linear-gradient(", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("radial-gradient(", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("conic-gradient(", StringComparison.OrdinalIgnoreCase);
                case "<string>":
                    return value.Length >= 2 && (value[0] == '"' || value[0] == '\'');
                case "<custom-ident>":
                    return IsIdent(value);
                case "<transform-function>":
                case "<transform-list>":
                    return value.Contains("("); // rough check for function syntax
                default:
                    // Unknown syntax component — accept any value (permissive fallback).
                    return true;
            }
        }

        // -- Value validators --

        static bool IsLength(string v) {
            if (v == "0") return true;
            if (string.IsNullOrEmpty(v)) return false;
            // Accept calc() as a length.
            if (v.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("min(", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("max(", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase)) return true;
            // Accept env() / var() references.
            if (v.StartsWith("env(", StringComparison.OrdinalIgnoreCase) ||
                v.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) return true;
            // Must end with a length unit.
            string[] units = { "px", "em", "rem", "vh", "vw", "vmin", "vmax", "vb", "vi", "%", "pt", "pc", "cm", "mm", "in", "q", "ex", "ch", "cap", "ic", "lh", "rlh", "svh", "lvh", "dvh", "cqw", "cqh" };
            foreach (var u in units) {
                if (v.EndsWith(u, StringComparison.OrdinalIgnoreCase)) {
                    string prefix = v.Substring(0, v.Length - u.Length);
                    if (double.TryParse(prefix, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
                }
            }
            return false;
        }

        static bool IsNumber(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            if (v.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)) return true;
            return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        static bool IsInteger(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            return int.TryParse(v, out _);
        }

        static bool IsPercentage(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            if (!v.EndsWith("%")) return false;
            return double.TryParse(v.Substring(0, v.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        static bool IsColor(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            // Named colors, hex, rgb(), rgba(), hsl(), hwb(), oklch(), etc.
            if (v.StartsWith("#")) return v.Length == 4 || v.Length == 5 || v.Length == 7 || v.Length == 9;
            if (v.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("hsl(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("hsla(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("hwb(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("color(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("oklch(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("oklab(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("lch(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("lab(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "transparent" || v == "currentcolor" || v == "currentColor") return true;
            // Named colors — delegate to the engine's named color table.
            return CssNamedColors.TryGet(v, out _, out _, out _, out _);
        }

        static bool IsAngle(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            string[] units = { "deg", "rad", "grad", "turn" };
            foreach (var u in units) {
                if (v.EndsWith(u, StringComparison.OrdinalIgnoreCase)) {
                    string prefix = v.Substring(0, v.Length - u.Length);
                    if (double.TryParse(prefix, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
                }
            }
            return false;
        }

        static bool IsTime(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            if (v.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
                return double.TryParse(v.Substring(0, v.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            }
            if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
                return double.TryParse(v.Substring(0, v.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            }
            return false;
        }

        static bool IsResolution(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            string[] units = { "dpi", "dpcm", "dppx", "x" };
            foreach (var u in units) {
                if (v.EndsWith(u, StringComparison.OrdinalIgnoreCase)) {
                    string prefix = v.Substring(0, v.Length - u.Length);
                    if (double.TryParse(prefix, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return true;
                }
            }
            return false;
        }

        static bool IsIdent(string v) {
            if (string.IsNullOrEmpty(v)) return false;
            // Simple check: starts with letter/underscore/hyphen, rest is alphanumeric/-/_
            char c0 = v[0];
            if (c0 == '-' && v.Length > 1) c0 = v[1]; // leading hyphen OK
            if (!char.IsLetter(c0) && c0 != '_') return false;
            for (int i = 1; i < v.Length; i++) {
                char c = v[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            }
            return true;
        }
    }
}
