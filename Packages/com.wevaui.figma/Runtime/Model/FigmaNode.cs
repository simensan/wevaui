using System.Collections.Generic;
using Weva.Figma.Json;

namespace Weva.Figma.Model
{
    /// <summary>
    /// A single Figma node, parsed from the REST file/nodes payload (or a plugin
    /// export with the same shape). One flat class with optional fields rather
    /// than a type hierarchy — it mirrors the loose JSON and keeps the mappers
    /// simple. <see cref="Raw"/> is kept as an escape hatch for fields we don't
    /// model yet.
    /// </summary>
    public sealed class FigmaNode
    {
        public string Id;
        public string Name;
        public string Type;          // FRAME, GROUP, COMPONENT, COMPONENT_SET, INSTANCE, TEXT, RECTANGLE, VECTOR, ELLIPSE, ...
        public bool Visible = true;
        public FigmaNode Parent;     // set during parse
        public List<FigmaNode> Children;

        public Rect Box;             // absoluteBoundingBox

        // --- Auto Layout (FRAME/COMPONENT/INSTANCE) ---
        public string LayoutMode;            // NONE, HORIZONTAL, VERTICAL
        public string PrimaryAxisAlign;      // MIN, CENTER, MAX, SPACE_BETWEEN
        public string CounterAxisAlign;      // MIN, CENTER, MAX, BASELINE
        public string LayoutWrap;            // NO_WRAP, WRAP
        public double ItemSpacing;
        public double CounterAxisSpacing;
        public double PaddingLeft, PaddingRight, PaddingTop, PaddingBottom;

        // --- This node as a child of an auto-layout parent ---
        public string LayoutSizingHorizontal; // FIXED, HUG, FILL
        public string LayoutSizingVertical;
        public string LayoutAlign;             // STRETCH, INHERIT (cross-axis)
        public double LayoutGrow;              // 0 or 1 (main-axis)
        public string LayoutPositioning;       // AUTO, ABSOLUTE

        // --- Constraints (child of a non-auto-layout frame) ---
        public string ConstraintHorizontal;   // LEFT, RIGHT, CENTER, LEFT_RIGHT (stretch), SCALE
        public string ConstraintVertical;     // TOP, BOTTOM, CENTER, TOP_BOTTOM (stretch), SCALE

        // --- Box decoration ---
        public double Opacity = 1;
        public bool ClipsContent;
        public string BlendMode;
        public double CornerRadius;
        public double[] RectangleCornerRadii;  // [tl, tr, br, bl] or null
        public List<FigmaPaint> Fills;
        public List<FigmaPaint> Strokes;
        public double StrokeWeight;
        public string StrokeAlign;             // INSIDE, OUTSIDE, CENTER
        public List<FigmaEffect> Effects;

        // --- Text ---
        public string Characters;
        public FigmaTextStyle TextStyle;

        public JsonValue Raw;

        public bool IsAutoLayout => LayoutMode == "HORIZONTAL" || LayoutMode == "VERTICAL";
        public bool IsText => Type == "TEXT";
        public bool IsVector => Type == "VECTOR" || Type == "BOOLEAN_OPERATION"
                                || Type == "STAR" || Type == "REGULAR_POLYGON" || Type == "LINE";
        public bool HasChildren => Children != null && Children.Count > 0;

        public static FigmaNode Parse(string json) => Parse(JsonParser.Parse(json));

        public static FigmaNode Parse(JsonValue v) => Parse(v, null);

        static FigmaNode Parse(JsonValue v, FigmaNode parent)
        {
            var n = new FigmaNode
            {
                Raw = v,
                Parent = parent,
                Id = v["id"].AsString(null),
                Name = v["name"].AsString(""),
                Type = v["type"].AsString(null),
                Visible = v.Has("visible") ? v["visible"].AsBool(true) : true,
                Box = Rect.From(v["absoluteBoundingBox"]),

                LayoutMode = v["layoutMode"].AsString(null),
                PrimaryAxisAlign = v["primaryAxisAlignItems"].AsString(null),
                CounterAxisAlign = v["counterAxisAlignItems"].AsString(null),
                LayoutWrap = v["layoutWrap"].AsString(null),
                ItemSpacing = v["itemSpacing"].AsDouble(),
                CounterAxisSpacing = v["counterAxisSpacing"].AsDouble(),
                PaddingLeft = v["paddingLeft"].AsDouble(),
                PaddingRight = v["paddingRight"].AsDouble(),
                PaddingTop = v["paddingTop"].AsDouble(),
                PaddingBottom = v["paddingBottom"].AsDouble(),

                LayoutSizingHorizontal = v["layoutSizingHorizontal"].AsString(null),
                LayoutSizingVertical = v["layoutSizingVertical"].AsString(null),
                LayoutAlign = v["layoutAlign"].AsString(null),
                LayoutGrow = v["layoutGrow"].AsDouble(),
                LayoutPositioning = v["layoutPositioning"].AsString(null),

                Opacity = v.Has("opacity") ? v["opacity"].AsDouble(1) : 1,
                ClipsContent = v["clipsContent"].AsBool(false),
                BlendMode = v["blendMode"].AsString(null),
                CornerRadius = v["cornerRadius"].AsDouble(),
                StrokeWeight = v["strokeWeight"].AsDouble(),
                StrokeAlign = v["strokeAlign"].AsString(null),

                Characters = v["characters"].AsString(null),
                TextStyle = FigmaTextStyle.From(v["style"]),
            };

            if (v.Has("constraints"))
            {
                n.ConstraintHorizontal = v["constraints"]["horizontal"].AsString(null);
                n.ConstraintVertical = v["constraints"]["vertical"].AsString(null);
            }

            if (v["rectangleCornerRadii"].IsArray && v["rectangleCornerRadii"].Count == 4)
            {
                JsonValue r = v["rectangleCornerRadii"];
                n.RectangleCornerRadii = new[] { r[0].AsDouble(), r[1].AsDouble(), r[2].AsDouble(), r[3].AsDouble() };
            }

            n.Fills = FigmaPaint.ListFrom(v["fills"]);
            n.Strokes = FigmaPaint.ListFrom(v["strokes"]);
            n.Effects = FigmaEffect.ListFrom(v["effects"]);

            if (v["children"].IsArray)
            {
                n.Children = new List<FigmaNode>();
                foreach (JsonValue child in v["children"].Items)
                    n.Children.Add(Parse(child, n));
            }

            return n;
        }

        /// <summary>The first visible fill/stroke paint, or null.</summary>
        public static FigmaPaint FirstVisible(List<FigmaPaint> paints)
        {
            if (paints == null) return null;
            // Figma paints render bottom-to-top; the topmost visible one wins for a single value.
            for (int i = paints.Count - 1; i >= 0; i--)
                if (paints[i].Visible && paints[i].Opacity > 0)
                    return paints[i];
            return null;
        }
    }
}
