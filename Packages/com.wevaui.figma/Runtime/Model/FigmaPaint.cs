using System.Collections.Generic;
using Weva.Figma.Json;

namespace Weva.Figma.Model
{
    public sealed class GradientStop
    {
        public double Position;
        public Rgba Color;
    }

    /// <summary>A Figma paint (fill or stroke): solid, gradient, or image.</summary>
    public sealed class FigmaPaint
    {
        public string Type;          // SOLID, GRADIENT_LINEAR, GRADIENT_RADIAL, GRADIENT_ANGULAR, GRADIENT_DIAMOND, IMAGE
        public bool Visible = true;
        public double Opacity = 1;

        public Rgba Color;                       // SOLID
        public List<GradientStop> GradientStops; // GRADIENT_*
        public List<Vec2> GradientHandles;       // GRADIENT_* (start, end, width)
        public string ImageRef;                  // IMAGE
        public string ScaleMode;                 // IMAGE: FILL/FIT/TILE/STRETCH

        public bool IsSolid => Type == "SOLID";
        public bool IsGradient => Type != null && Type.StartsWith("GRADIENT_");
        public bool IsLinearGradient => Type == "GRADIENT_LINEAR";
        public bool IsRadialGradient => Type == "GRADIENT_RADIAL";
        public bool IsImage => Type == "IMAGE";

        public static FigmaPaint From(JsonValue v)
        {
            var p = new FigmaPaint
            {
                Type = v["type"].AsString(),
                Visible = v.Has("visible") ? v["visible"].AsBool(true) : true,
                Opacity = v.Has("opacity") ? v["opacity"].AsDouble(1) : 1,
                ImageRef = v["imageRef"].AsString(null),
                ScaleMode = v["scaleMode"].AsString(null),
            };

            if (p.IsSolid)
                p.Color = Rgba.From(v["color"]);

            if (p.IsGradient)
            {
                p.GradientStops = new List<GradientStop>();
                foreach (JsonValue s in v["gradientStops"].Items)
                    p.GradientStops.Add(new GradientStop
                    {
                        Position = s["position"].AsDouble(),
                        Color = Rgba.From(s["color"]),
                    });

                p.GradientHandles = new List<Vec2>();
                foreach (JsonValue h in v["gradientHandlePositions"].Items)
                    p.GradientHandles.Add(Vec2.From(h));
            }

            return p;
        }

        public static List<FigmaPaint> ListFrom(JsonValue array)
        {
            if (!array.IsArray || array.Count == 0) return null;
            var list = new List<FigmaPaint>(array.Count);
            foreach (JsonValue v in array.Items)
                list.Add(From(v));
            return list;
        }
    }
}
