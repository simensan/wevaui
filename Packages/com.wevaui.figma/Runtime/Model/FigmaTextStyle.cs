using Weva.Figma.Json;

namespace Weva.Figma.Model
{
    /// <summary>The typographic style on a TEXT node (Figma <c>style</c> block).</summary>
    public sealed class FigmaTextStyle
    {
        public string FontFamily;
        public double FontSize = 16;
        public double FontWeight = 400;
        public double LineHeightPx;
        public double LineHeightPercentFontSize;
        public string LineHeightUnit;       // PIXELS, FONT_SIZE_%, INTRINSIC_%
        public double LetterSpacing;
        public string TextAlignHorizontal;  // LEFT, CENTER, RIGHT, JUSTIFIED
        public string TextCase;             // ORIGINAL, UPPER, LOWER, TITLE
        public string TextDecoration;       // NONE, UNDERLINE, STRIKETHROUGH
        public bool Italic;

        public static FigmaTextStyle From(JsonValue v)
        {
            if (!v.IsObject) return null;
            return new FigmaTextStyle
            {
                FontFamily = v["fontFamily"].AsString(null),
                FontSize = v.Has("fontSize") ? v["fontSize"].AsDouble(16) : 16,
                FontWeight = v.Has("fontWeight") ? v["fontWeight"].AsDouble(400) : 400,
                LineHeightPx = v["lineHeightPx"].AsDouble(),
                LineHeightPercentFontSize = v["lineHeightPercentFontSize"].AsDouble(),
                LineHeightUnit = v["lineHeightUnit"].AsString(null),
                LetterSpacing = v["letterSpacing"].AsDouble(),
                TextAlignHorizontal = v["textAlignHorizontal"].AsString(null),
                TextCase = v["textCase"].AsString(null),
                TextDecoration = v["textDecoration"].AsString(null),
                Italic = v["italic"].AsBool(false),
            };
        }
    }
}
