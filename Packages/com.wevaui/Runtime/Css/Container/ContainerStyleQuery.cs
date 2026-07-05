using System;
using Weva.Css.Values;

namespace Weva.Css.Container {
    // CSS Containment L3 §3.4 — style query: `style(--prop: value)` or the
    // boolean existence form `style(--prop)`. Evaluates a custom property on the
    // resolved container element.
    //
    // v1 scope: only custom properties (`--*`) are supported. Standard-property
    // style features (e.g. `style(display: flex)`) and the range/relational
    // forms are not yet handled — the parser only admits a leading `--ident`, so
    // anything else throws and the rule is dropped per the EC11 convention.
    //
    // v1 resolution: the container is the same one the size-side resolver finds
    // (nearest `container-type` ancestor). The spec lets style queries resolve
    // against *any* ancestor regardless of container-type; widening the resolver
    // to that is a follow-on. In practice authors put style queries on elements
    // that are already containers, so this covers the common case.
    public sealed class ContainerStyleQuery : ContainerQuery {
        // Custom property name, case-preserved (custom properties are
        // case-sensitive, unlike feature names).
        public string Property { get; }
        // Declared value text, or null for the existence form `style(--prop)`.
        public string ValueText { get; }

        public ContainerStyleQuery(string property, string valueText) {
            Property = property ?? "";
            ValueText = valueText == null ? null : valueText.Trim();
        }

        public override bool Evaluate(ContainerContext ctx) {
            // No resolved container (Type == None) or no resolver wired → the
            // feature is indeterminate, which the spec treats as false.
            if (ctx.ContainerElement == null || ctx.ComputedCustomProperty == null) return false;

            string actual = ctx.ComputedCustomProperty(ctx.ContainerElement, Property);

            if (ValueText == null) {
                // Existence form `style(--prop)`: matches when the property
                // resolves to a non-empty value (i.e. not the guaranteed-invalid
                // / unset value).
                return !string.IsNullOrEmpty(actual) && !IsGuaranteedInvalid(actual);
            }

            if (actual == null) return false;
            // Compare computed value text. Custom-property values are compared
            // as component values; a trimmed ordinal string compare is the v1
            // approximation (sufficient for keyword-style tokens like `dark`).
            return string.Equals(actual.Trim(), ValueText, StringComparison.Ordinal);
        }

        static bool IsGuaranteedInvalid(string v) {
            string t = v.Trim();
            return t.Length == 0
                || CssStringUtil.EqualsIgnoreCase(t, "initial")
                || CssStringUtil.EqualsIgnoreCase(t, "unset");
        }
    }
}
