using System;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;

namespace Weva.Css.Container {
    public static class ContainerResolver {
        // Walks up the box tree from the supplied element's box (exclusive of the element
        // itself per CSS spec — an element does not query its own container) and returns
        // the ContainerContext of the nearest ancestor whose `container-type` is non-None.
        // If `name` is non-null, only ancestors whose `container-name` (whitespace-split
        // list) contains `name` qualify. Returns ContainerContext.None when no ancestor
        // matches; ContainerFeatureQuery treats that as "every feature evaluates false".
        //
        // Note: this reads box Width/Height after the most recent layout pass. Container
        // queries thus see the layout-after-previous-cascade size — see CascadeEngine
        // commentary on the chicken-and-egg between cascade and layout.
        public static ContainerContext Resolve(Element element, string name, Func<Element, Box> elementToBox) {
            if (element == null || elementToBox == null) return ContainerContext.None;
            var startBox = elementToBox(element);
            if (startBox == null) return ContainerContext.None;
            var box = startBox.Parent;
            while (box != null) {
                if (Matches(box, name, out var ctx)) return ctx;
                box = box.Parent;
            }
            return ContainerContext.None;
        }

        public static ContainerContext ResolveFromBox(Box startBox, string name) {
            if (startBox == null) return ContainerContext.None;
            var box = startBox.Parent;
            while (box != null) {
                if (Matches(box, name, out var ctx)) return ctx;
                box = box.Parent;
            }
            return ContainerContext.None;
        }

        // Returns true iff `box` is a qualifying container (non-None container-type)
        // for the given optional `name`. Used by the nested-@container chain walker
        // in CascadeEngine to test whether a candidate box is an anchor point.
        public static bool BoxQualifiesAsContainer(Box box, string name) {
            return Matches(box, name, out _);
        }

        // Extracts the ContainerContext from a box that is already known to be a
        // qualifying container. Used by the nested-@container chain walker after
        // BoxQualifiesAsContainer confirms the box is valid.
        public static ContainerContext ContextFromBox(Box box) {
            if (box == null) return ContainerContext.None;
            Matches(box, null, out var ctx);
            return ctx;
        }

        static bool Matches(Box box, string name, out ContainerContext ctx) {
            ctx = ContainerContext.None;
            var style = box.Style;
            if (style == null) return false;
            // Typed read against the per-style parsed cache — the keyword
            // grammar of container-type is parsed once per slot, not per
            // cascade-resolution pass. ParseContainerTypeParsed dispatches
            // on the cached CssKeyword/CssIdentifier without re-running
            // CssValue.TryParse. Falls back to the raw-string path when the
            // slot didn't parse (initial-value edge cases).
            var typeParsed = style.GetParsed(CssProperties.ContainerTypeId);
            var type = typeParsed != null
                ? ParseContainerTypeParsed(typeParsed)
                : ParseContainerType(style.Get(CssProperties.ContainerTypeId));
            if (type == ContainerType.None) return false;
            // container-name is a custom <custom-ident> list. We keep the raw
            // string read because NormalizeName walks the whole declared list
            // for membership tests below — a parsed CssIdentifier collapses
            // multi-name lists into a single token. Use the int-keyed
            // overload to skip the property-name → id lookup.
            string boxName = NormalizeName(style.Get(CssProperties.ContainerNameId));
            if (!string.IsNullOrEmpty(name)) {
                if (string.IsNullOrEmpty(boxName)) return false;
                if (!ContainsName(boxName, name)) return false;
            }
            double inline = box.Width;
            double block = box.Height;
            // CON-2: carry the container's element so style() queries can read
            // its computed custom properties (box.Style is a layout artifact and
            // may not reflect the cascaded custom-property values).
            ctx = type == ContainerType.Size
                ? ContainerContext.Size(inline, block, boxName, box.Element)
                : ContainerContext.InlineSize(inline, boxName, box.Element);
            return true;
        }

        public static ContainerType ParseContainerType(string text) {
            if (string.IsNullOrEmpty(text)) return ContainerType.None;
            string t = CssStringUtil.ToLowerInvariantOrSame(text.Trim());
            switch (t) {
                case "inline-size": return ContainerType.InlineSize;
                case "size": return ContainerType.Size;
                case "normal":
                case "none":
                case "":
                    return ContainerType.None;
            }
            return ContainerType.None;
        }

        // Typed dispatch against the per-style parsed CssValue. container-type
        // is a keyword grammar — CssParser produces CssKeyword for the
        // recognized tokens and CssIdentifier for unknown idents. Either way
        // the token is already lowercase (CssKeyword case-folds on
        // construction), so the comparison is O(1) byte equality with no
        // Trim/ToLowerInvariant allocation.
        static ContainerType ParseContainerTypeParsed(CssValue parsed) {
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            else return ContainerType.None;
            if (string.IsNullOrEmpty(name)) return ContainerType.None;
            if (CssStringUtil.EqualsIgnoreCase(name, "inline-size")) return ContainerType.InlineSize;
            if (CssStringUtil.EqualsIgnoreCase(name, "size")) return ContainerType.Size;
            // "normal" / "none" / unknown idents all collapse to None per spec.
            return ContainerType.None;
        }

        static string NormalizeName(string text) {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();
            if (t.Length == 0) return null;
            string lower = CssStringUtil.ToLowerInvariantOrSame(t);
            if (lower == "none" || lower == "initial" || lower == "unset") return null;
            return t;
        }

        static bool ContainsName(string declared, string wanted) {
            int i = 0;
            while (i < declared.Length) {
                while (i < declared.Length && char.IsWhiteSpace(declared[i])) i++;
                int start = i;
                while (i < declared.Length && !char.IsWhiteSpace(declared[i])) i++;
                if (i > start) {
                    int len = i - start;
                    if (len == wanted.Length && string.CompareOrdinal(declared, start, wanted, 0, len) == 0) {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
