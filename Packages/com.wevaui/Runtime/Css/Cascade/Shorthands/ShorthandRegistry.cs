using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    public static class ShorthandRegistry {
        static readonly Dictionary<string, IShorthandExpander> map = BuildMap();

        public static bool IsShorthand(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            return map.ContainsKey(name);
        }

        public static bool TryGet(string name, out IShorthandExpander expander) {
            if (string.IsNullOrEmpty(name)) { expander = null; return false; }
            return map.TryGetValue(name, out expander);
        }

        public static bool TryExpand(string name, string value, out IEnumerable<KeyValuePair<string, string>> longhands) {
            if (TryGet(name, out var ex)) {
                longhands = ex.Expand(value ?? "");
                return true;
            }
            longhands = null;
            return false;
        }

        public static IEnumerable<string> Names => map.Keys;

        static Dictionary<string, IShorthandExpander> BuildMap() {
            var d = new Dictionary<string, IShorthandExpander>();
            void Add(IShorthandExpander e) { d[e.ShorthandName] = e; }

            Add(MarginShorthandExpander.Margin());
            Add(MarginShorthandExpander.Padding());
            Add(MarginShorthandExpander.ScrollPadding());
            Add(MarginShorthandExpander.ScrollMargin());
            Add(new InsetShorthandExpander());
            Add(new LogicalBoxShorthandExpander("margin-inline", "margin", "inline", true));
            Add(new LogicalBoxShorthandExpander("margin-block", "margin", "block", true));
            Add(new LogicalBoxShorthandExpander("padding-inline", "padding", "inline", false));
            Add(new LogicalBoxShorthandExpander("padding-block", "padding", "block", false));
            Add(new LogicalBoxShorthandExpander("inset-inline", "inset", "inline", true));
            Add(new LogicalBoxShorthandExpander("inset-block", "inset", "block", true));

            Add(BorderShorthandExpander.Border());
            Add(BorderShorthandExpander.BorderTop());
            Add(BorderShorthandExpander.BorderRight());
            Add(BorderShorthandExpander.BorderBottom());
            Add(BorderShorthandExpander.BorderLeft());
            Add(BorderShorthandExpander.BorderWidth());
            Add(BorderShorthandExpander.BorderStyle());
            Add(BorderShorthandExpander.BorderColor());
            Add(new LogicalBorderShorthandExpander("border-inline", "inline", null));
            Add(new LogicalBorderShorthandExpander("border-inline-start", "inline", "start"));
            Add(new LogicalBorderShorthandExpander("border-inline-end", "inline", "end"));
            Add(LogicalBorderShorthandExpander.AxisWidth("border-inline-width", "inline"));
            Add(LogicalBorderShorthandExpander.AxisStyle("border-inline-style", "inline"));
            Add(LogicalBorderShorthandExpander.AxisColor("border-inline-color", "inline"));
            Add(new LogicalBorderShorthandExpander("border-block", "block", null));
            Add(new LogicalBorderShorthandExpander("border-block-start", "block", "start"));
            Add(new LogicalBorderShorthandExpander("border-block-end", "block", "end"));
            Add(LogicalBorderShorthandExpander.AxisWidth("border-block-width", "block"));
            Add(LogicalBorderShorthandExpander.AxisStyle("border-block-style", "block"));
            Add(LogicalBorderShorthandExpander.AxisColor("border-block-color", "block"));

            Add(new BorderRadiusShorthandExpander());

            Add(new BackgroundShorthandExpander());
            Add(new MaskShorthandExpander());
            Add(new BorderImageShorthandExpander());
            Add(new FontShorthandExpander());

            Add(new FlexShorthandExpander());
            Add(new FlexFlowShorthandExpander());
            Add(new GapShorthandExpander());
            Add(new ColumnsShorthandExpander());
            Add(new ColumnRuleShorthandExpander());

            Add(PlaceShorthandExpander.PlaceItems());
            Add(PlaceShorthandExpander.PlaceContent());
            Add(PlaceShorthandExpander.PlaceSelf());

            Add(new TransitionShorthandExpander());
            Add(new AnimationShorthandExpander());
            Add(new TextDecorationShorthandExpander());
            // CSS Text Decoration L4 — both prefixed and unprefixed forms
            // expand to the same `-webkit-text-stroke-{width,color}` longhands.
            Add(new TextStrokeShorthandExpander("-webkit-text-stroke"));
            Add(new TextStrokeShorthandExpander("text-stroke"));
            Add(new OverflowShorthandExpander());
            Add(new OverscrollBehaviorShorthandExpander());
            Add(new OutlineShorthandExpander());
            Add(new ListStyleShorthandExpander());

            // CSS Cascade L4 §3.2 — registered last so its enumeration of
            // `CssProperties` skips every shorthand already in this map.
            Add(new AllShorthandExpander());

            return d;
        }
    }
}
