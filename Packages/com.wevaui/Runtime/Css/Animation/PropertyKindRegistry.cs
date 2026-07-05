using System.Collections.Generic;

namespace Weva.Css.Animation {
    public static class PropertyKindRegistry {
        static readonly Dictionary<string, PropertyKind> map = Build();

        public static PropertyKind Of(string property) {
            if (property == null) return PropertyKind.Discrete;
            return map.TryGetValue(property, out var k) ? k : PropertyKind.Discrete;
        }

        public static bool IsAnimatable(string property) {
            return property != null && map.ContainsKey(property);
        }

        static Dictionary<string, PropertyKind> Build() {
            var d = new Dictionary<string, PropertyKind>();
            void Length(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Length; }
            void Color(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Color; }
            void Number(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Number; }
            void Integer(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Integer; }
            void Discrete(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Discrete; }
            void Transform(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Transform; }
            void Filter(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Filter; }
            void Gradient(params string[] names) { foreach (var n in names) d[n] = PropertyKind.Gradient; }

            Length(
                "width", "height",
                "min-width", "min-height", "max-width", "max-height",
                "top", "right", "bottom", "left",
                "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
                "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
                "border-width",
                "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
                "border-radius",
                "border-top-left-radius", "border-top-right-radius",
                "border-bottom-right-radius", "border-bottom-left-radius",
                "font-size", "line-height", "letter-spacing", "word-spacing",
                "row-gap", "column-gap", "gap",
                "flex-basis",
                "outline-width", "outline-offset",
                "scroll-margin", "scroll-margin-top", "scroll-margin-right",
                "scroll-margin-bottom", "scroll-margin-left",
                "scroll-padding", "scroll-padding-top", "scroll-padding-right",
                "scroll-padding-bottom", "scroll-padding-left",
                "vertical-align"
            );

            Color(
                "color",
                "background-color",
                "border-color",
                "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
                "text-decoration-color",
                "outline-color"
            );

            Number(
                "opacity",
                "flex-grow", "flex-shrink"
            );

            // CSS Transitions L1 §2.3 — integer-typed animatable values:
            // interpolate as real number then round to nearest integer.
            // z-index: CSS Position L3 §6 (initial auto; integer when set).
            // order: CSS Flexbox L1 §8 (initial 0; integer).
            Integer(
                "z-index",
                "order"
            );

            Transform("transform");
            Filter("filter", "backdrop-filter");

            // CSS Transforms L2 §13 — individual transform properties
            // interpolate per-component (each axis lerps independently).
            // Distinct from the `transform` shorthand which uses
            // matrix-decomposition for mismatched function-list shapes.
            d["translate"] = PropertyKind.Translate;
            d["rotate"] = PropertyKind.Rotate;
            d["scale"] = PropertyKind.Scale;

            // H18b: multi-component animatable properties. Each has a
            // dedicated interpolator in ValueInterpolator that handles
            // per-component / per-shadow / per-shape lerp rules. Falling
            // back through the Discrete bucket would lose the visible
            // smooth interpolation these properties get in browsers.
            d["background-position"] = PropertyKind.BackgroundPosition;
            d["background-size"] = PropertyKind.BackgroundSize;
            d["box-shadow"] = PropertyKind.BoxShadow;
            d["text-shadow"] = PropertyKind.TextShadow;
            d["clip-path"] = PropertyKind.ClipPath;

            // A9: CSS Images L3 §3.5 + CSS Transitions L1 §2.3 — gradient-valued
            // properties are animatable when both endpoints share the same gradient
            // type, angle/direction, and stop count. Mismatched shapes or non-gradient
            // values (none, url()) fall back to discrete in the interpolator.
            // `border-image-source`, `mask-image`, and `list-style-image` round-trip
            // through the same gradient parser (BackgroundResolver.TryParseGradient)
            // and so share the same interpolation kind.
            Gradient("background-image", "border-image-source", "mask-image", "list-style-image");

            Discrete(
                "display", "visibility",
                "font-weight", "font-style", "font-variant",
                "text-align", "text-transform", "text-decoration",
                "white-space",
                "position", "overflow", "overflow-x", "overflow-y",
                "border-style",
                "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
                "flex-direction", "flex-wrap",
                "justify-content", "align-items", "align-self", "align-content",
                "box-sizing", "cursor"
            );

            return d;
        }
    }
}
