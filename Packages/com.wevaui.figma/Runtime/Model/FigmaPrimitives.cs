using Weva.Figma.Json;

namespace Weva.Figma.Model
{
    /// <summary>An RGBA color with channels in 0..1 (Figma's native range).</summary>
    public struct Rgba
    {
        public double R, G, B, A;

        public Rgba(double r, double g, double b, double a) { R = r; G = g; B = b; A = a; }

        public static Rgba From(JsonValue v, double fallbackAlpha = 1)
            => new Rgba(
                v["r"].AsDouble(),
                v["g"].AsDouble(),
                v["b"].AsDouble(),
                v.Has("a") ? v["a"].AsDouble(fallbackAlpha) : fallbackAlpha);

        /// <summary>This color as CSS text, multiplying alpha by an extra paint opacity.</summary>
        public string ToCss(double extraAlpha = 1) => CssText.Color(R, G, B, A * extraAlpha);
    }

    public struct Vec2
    {
        public double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }
        public static Vec2 From(JsonValue v) => new Vec2(v["x"].AsDouble(), v["y"].AsDouble());
    }

    public struct Rect
    {
        public double X, Y, Width, Height;
        public static Rect From(JsonValue v) => new Rect
        {
            X = v["x"].AsDouble(),
            Y = v["y"].AsDouble(),
            Width = v["width"].AsDouble(),
            Height = v["height"].AsDouble(),
        };
        public bool HasSize => Width > 0 || Height > 0;
    }
}
