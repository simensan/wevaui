using System.Collections.Generic;
using Weva.Figma.Json;

namespace Weva.Figma.Model
{
    /// <summary>A Figma effect: drop/inner shadow, layer blur, background blur.</summary>
    public sealed class FigmaEffect
    {
        public string Type;     // DROP_SHADOW, INNER_SHADOW, LAYER_BLUR, BACKGROUND_BLUR
        public bool Visible = true;
        public double Radius;
        public double Spread;
        public Rgba Color;
        public Vec2 Offset;

        public bool IsDropShadow => Type == "DROP_SHADOW";
        public bool IsInnerShadow => Type == "INNER_SHADOW";
        public bool IsLayerBlur => Type == "LAYER_BLUR";
        public bool IsBackgroundBlur => Type == "BACKGROUND_BLUR";

        public static FigmaEffect From(JsonValue v) => new FigmaEffect
        {
            Type = v["type"].AsString(),
            Visible = v.Has("visible") ? v["visible"].AsBool(true) : true,
            Radius = v["radius"].AsDouble(),
            Spread = v["spread"].AsDouble(),
            Color = Rgba.From(v["color"]),
            Offset = Vec2.From(v["offset"]),
        };

        public static List<FigmaEffect> ListFrom(JsonValue array)
        {
            if (!array.IsArray || array.Count == 0) return null;
            var list = new List<FigmaEffect>(array.Count);
            foreach (JsonValue v in array.Items)
                list.Add(From(v));
            return list;
        }
    }
}
