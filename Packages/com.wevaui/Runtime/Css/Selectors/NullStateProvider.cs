using Weva.Dom;

namespace Weva.Css.Selectors {
    // State provider for headless / build-time cascade passes that have no live
    // interaction state (no hover, focus, active). The interactive flags default
    // to None. Attribute-driven pseudo-classes (:disabled, :checked, :root,
    // :placeholder-shown) ARE evaluated from the element's DOM state so that
    // the cascade resolves UA defaults (e.g. `input[disabled] { opacity: 0.5 }`)
    // and custom-property inheritance from :root even without a running dispatcher.
    public sealed class NullStateProvider : IElementStateProvider {
        public static readonly NullStateProvider Instance = new();

        NullStateProvider() { }

        public ElementState GetState(Element e) {
            if (e == null) return ElementState.None;
            ElementState s = ElementState.None;
            // :root — matches the first element child of the Document.
            if (e.OwnerDocument != null) {
                var children = e.OwnerDocument.Children;
                for (int i = 0; i < children.Count; i++) {
                    if (children[i] is Element root) {
                        if (ReferenceEquals(e, root)) s |= ElementState.Root;
                        break;
                    }
                }
            }
            // :disabled — mirrors SelectorMatcher.IsDisabled / FocusManager.IsDisabled
            if (IsDisabledViaAttribute(e)) s |= ElementState.Disabled;
            // :checked — checkbox/radio with [checked] attribute
            if (IsChecked(e)) s |= ElementState.Checked;
            // :placeholder-shown — input/textarea with placeholder and no value
            if (IsPlaceholderShown(e)) s |= ElementState.PlaceholderShown;
            return s;
        }

        static bool IsDisabledViaAttribute(Element e) {
            // Form controls disabled by the `disabled` attribute.
            if (e.HasAttribute("disabled")) return true;
            // A fieldset's [disabled] propagates to its descendant form controls,
            // but that field-group check is handled by SelectorMatcher.IsDisabled
            // for the live path. For the headless build-time path, per-element
            // attribute check is sufficient.
            return false;
        }

        static bool IsChecked(Element e) {
            if (e.TagName != "input") return false;
            var type = e.GetAttribute("type");
            if (type != "checkbox" && type != "radio") return false;
            return e.HasAttribute("checked");
        }

        static bool IsPlaceholderShown(Element e) {
            if (e.TagName != "input" && e.TagName != "textarea") return false;
            if (!e.HasAttribute("placeholder")) return false;
            var v = e.GetAttribute("value");
            return string.IsNullOrEmpty(v);
        }
    }
}
