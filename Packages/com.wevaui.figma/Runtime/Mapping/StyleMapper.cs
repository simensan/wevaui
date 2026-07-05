using System.Collections.Generic;
using System.Globalization;
using Weva.Figma.Model;

namespace Weva.Figma.Mapping
{
    /// <summary>
    /// Maps a node's paint (fills, strokes, corners, effects, opacity, clip) and,
    /// for TEXT nodes, its typography, to CSS declarations within the Weva
    /// subset. Emits idiomatic shorthands (border, border-radius, box-shadow,
    /// background) — the engine's shorthand expanders handle them.
    ///
    /// v1 simplifications: only the topmost visible fill becomes the background
    /// (no multi-layer background stacks); gradient strokes fall back to none;
    /// blend modes and image scale-mode edge cases are approximated.
    /// </summary>
    public static class StyleMapper
    {
        public static void Apply(FigmaNode node, CssBlock b)
        {
            if (node.Opacity < 1 && node.Opacity >= 0)
                b.Set("opacity", CssText.Number(System.Math.Round(node.Opacity, 3)));

            if (node.IsText)
            {
                FigmaPaint c = FigmaNode.FirstVisible(node.Fills);
                if (c != null && c.IsSolid) b.Set("color", c.Color.ToCss(c.Opacity));
                ApplyText(node, b);
            }
            else
            {
                ApplyFill(node, b);
            }

            ApplyStroke(node, b);
            ApplyCorners(node, b);
            ApplyEffects(node, b);

            if (node.ClipsContent) b.Set("overflow", "hidden");
        }

        static void ApplyFill(FigmaNode node, CssBlock b)
        {
            FigmaPaint p = FigmaNode.FirstVisible(node.Fills);
            if (p == null) return;

            if (p.IsSolid)
            {
                b.Set("background-color", p.Color.ToCss(p.Opacity));
            }
            else if (p.IsLinearGradient)
            {
                b.Set("background-image", LinearGradient(p));
            }
            else if (p.IsRadialGradient)
            {
                b.Set("background-image", RadialGradient(p));
            }
            else if (p.IsImage)
            {
                b.Set("background-image", $"url(\"{RasterNaming.ImageFile(p.ImageRef)}\")");
                b.Set("background-size", ScaleModeSize(p.ScaleMode));
                b.Set("background-position", "center");
                b.Set("background-repeat", p.ScaleMode == "TILE" ? "repeat" : "no-repeat");
            }
        }

        static string ScaleModeSize(string scaleMode)
        {
            switch (scaleMode)
            {
                case "FIT": return "contain";
                case "STRETCH": return "100% 100%";
                case "TILE": return "auto";
                default: return "cover"; // FILL
            }
        }

        static string LinearGradient(FigmaPaint p)
        {
            double deg = 180; // default top → bottom
            if (p.GradientHandles != null && p.GradientHandles.Count >= 2)
            {
                Vec2 s = p.GradientHandles[0], e = p.GradientHandles[1];
                double dx = e.X - s.X, dy = e.Y - s.Y;
                // CSS angle: 0deg = up, 90deg = right, clockwise. Figma y is down.
                deg = System.Math.Atan2(dx, -dy) * 180.0 / System.Math.PI;
                deg = ((deg % 360) + 360) % 360;
            }
            return $"linear-gradient({CssText.Number(System.Math.Round(deg, 1))}deg, {Stops(p)})";
        }

        static string RadialGradient(FigmaPaint p)
            => $"radial-gradient({Stops(p)})";

        static string Stops(FigmaPaint p)
        {
            var parts = new List<string>();
            if (p.GradientStops != null)
                foreach (GradientStop s in p.GradientStops)
                    parts.Add(s.Color.ToCss() + " " + CssText.Number(System.Math.Round(s.Position * 100, 2)) + "%");
            return string.Join(", ", parts);
        }

        static void ApplyStroke(FigmaNode node, CssBlock b)
        {
            if (node.StrokeWeight <= 0) return;
            FigmaPaint s = FigmaNode.FirstVisible(node.Strokes);
            if (s == null || !s.IsSolid) return; // gradient/image strokes deferred to a raster pass
            string color = s.Color.ToCss(s.Opacity);

            Json.JsonValue isw = node.Raw["individualStrokeWeights"];
            if (isw.IsObject)
            {
                EmitSide(b, "top", isw["top"].AsDouble(), color);
                EmitSide(b, "right", isw["right"].AsDouble(), color);
                EmitSide(b, "bottom", isw["bottom"].AsDouble(), color);
                EmitSide(b, "left", isw["left"].AsDouble(), color);
            }
            else
            {
                b.Set("border", $"{CssText.Px(node.StrokeWeight)} solid {color}");
            }
        }

        static void EmitSide(CssBlock b, string side, double w, string color)
        {
            if (w > 0) b.Set("border-" + side, $"{CssText.Px(w)} solid {color}");
        }

        static void ApplyCorners(FigmaNode node, CssBlock b)
        {
            // An ellipse is a box with fully-rounded corners; border-radius:50%
            // yields an ellipse even when width != height.
            if (node.Type == "ELLIPSE")
            {
                b.Set("border-radius", "50%");
                return;
            }

            double[] r = node.RectangleCornerRadii;
            if (r != null)
            {
                if (r[0] == r[1] && r[1] == r[2] && r[2] == r[3])
                {
                    if (r[0] > 0) b.Set("border-radius", CssText.Px(r[0]));
                }
                else
                {
                    b.Set("border-radius", $"{CssText.Px(r[0])} {CssText.Px(r[1])} {CssText.Px(r[2])} {CssText.Px(r[3])}");
                }
            }
            else if (node.CornerRadius > 0)
            {
                b.Set("border-radius", CssText.Px(node.CornerRadius));
            }
        }

        static void ApplyEffects(FigmaNode node, CssBlock b)
        {
            if (node.Effects == null) return;
            var shadows = new List<string>();
            foreach (FigmaEffect e in node.Effects)
            {
                if (!e.Visible) continue;
                if (e.IsDropShadow || e.IsInnerShadow)
                {
                    string s = $"{CssText.Px(e.Offset.X)} {CssText.Px(e.Offset.Y)} {CssText.Px(e.Radius)}";
                    if (e.Spread != 0) s += " " + CssText.Px(e.Spread);
                    s += " " + e.Color.ToCss();
                    if (e.IsInnerShadow) s = "inset " + s;
                    shadows.Add(s);
                }
                else if (e.IsLayerBlur)
                {
                    b.Set("filter", $"blur({CssText.Px(e.Radius)})");
                }
                else if (e.IsBackgroundBlur)
                {
                    b.Set("backdrop-filter", $"blur({CssText.Px(e.Radius)})");
                }
            }
            if (shadows.Count > 0) b.Set("box-shadow", string.Join(", ", shadows));
        }

        static void ApplyText(FigmaNode node, CssBlock b)
        {
            FigmaTextStyle t = node.TextStyle;
            if (t == null) return;

            if (!string.IsNullOrEmpty(t.FontFamily)) b.Set("font-family", QuoteFamily(t.FontFamily));
            b.Set("font-size", CssText.Px(t.FontSize));
            if (System.Math.Abs(t.FontWeight - 400) > 0.5) b.Set("font-weight", CssText.Number(t.FontWeight));
            if (t.Italic) b.Set("font-style", "italic");

            if (t.LineHeightUnit == "PIXELS" && t.LineHeightPx > 0)
                b.Set("line-height", CssText.Px(t.LineHeightPx));
            else if (t.LineHeightUnit == "FONT_SIZE_%" && t.LineHeightPercentFontSize > 0)
                b.Set("line-height", CssText.Number(System.Math.Round(t.LineHeightPercentFontSize, 2)) + "%");

            if (t.LetterSpacing != 0) b.Set("letter-spacing", CssText.Px(t.LetterSpacing));

            b.Set("text-align", MapTextAlign(t.TextAlignHorizontal));
            b.Set("text-transform", MapTextCase(t.TextCase));
            b.Set("text-decoration", MapTextDecoration(t.TextDecoration));

            if (node.Characters != null && node.Characters.IndexOf('\n') >= 0)
                b.Set("white-space", "pre-wrap");
        }

        static string QuoteFamily(string family)
            => family.IndexOf(' ') >= 0 ? "\"" + family + "\"" : family;

        static string MapTextAlign(string a)
        {
            switch (a)
            {
                case "CENTER": return "center";
                case "RIGHT": return "right";
                case "JUSTIFIED": return "justify";
                default: return null; // LEFT / null
            }
        }

        static string MapTextCase(string c)
        {
            switch (c)
            {
                case "UPPER": return "uppercase";
                case "LOWER": return "lowercase";
                case "TITLE": return "capitalize";
                default: return null;
            }
        }

        static string MapTextDecoration(string d)
        {
            switch (d)
            {
                case "UNDERLINE": return "underline";
                case "STRIKETHROUGH": return "line-through";
                default: return null;
            }
        }
    }
}
