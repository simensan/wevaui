using Weva.Figma.Model;

namespace Weva.Figma.Mapping
{
    /// <summary>
    /// Maps a node's geometry to CSS layout: Figma Auto Layout → flexbox,
    /// non-auto-layout frames → an absolute-positioning context, and Figma
    /// sizing (Fixed/Hug/Fill) → fixed sizes / <c>flex-grow</c> / <c>align-self</c>.
    ///
    /// v1 simplifications (tracked in PLAN.md): constraint mapping pins one or
    /// two edges but doesn't reproduce SCALE/CENTER exactly; Figma stroke-align
    /// is assumed INSIDE (matches the emitted <c>box-sizing: border-box</c>).
    /// </summary>
    public static class LayoutMapper
    {
        public static void Apply(FigmaNode node, CssBlock b)
        {
            FigmaNode parent = node.Parent;
            bool isRoot = parent == null;
            bool parentAuto = parent != null && parent.IsAutoLayout;
            bool isAbsolute = !isRoot && (!parent.IsAutoLayout || node.LayoutPositioning == "ABSOLUTE");

            if (node.IsAutoLayout)
            {
                b.Set("display", "flex");
                b.Set("flex-direction", node.LayoutMode == "VERTICAL" ? "column" : "row");
                if (node.LayoutWrap == "WRAP") b.Set("flex-wrap", "wrap");

                string justify = MapPrimary(node.PrimaryAxisAlign);
                if (justify != null) b.Set("justify-content", justify);
                b.Set("align-items", MapCounter(node.CounterAxisAlign));

                if (node.ItemSpacing != 0 && node.PrimaryAxisAlign != "SPACE_BETWEEN")
                    b.Set("gap", CssText.Px(node.ItemSpacing));

                ApplyPadding(node, b);
            }

            string position = ResolvePosition(node, isAbsolute);
            if (position != null) b.Set("position", position);

            if (isAbsolute) ApplyAbsoluteOffsets(node, parent, b);
            else ApplyFlowSizing(node, parent, parentAuto, isRoot, b);
        }

        static string ResolvePosition(FigmaNode node, bool isAbsolute)
        {
            if (isAbsolute) return "absolute";
            if (!node.HasChildren) return null;
            // A non-auto-layout frame positions all children absolutely; an
            // auto-layout frame still needs a containing block if any child opts
            // out with layoutPositioning:ABSOLUTE.
            if (!node.IsAutoLayout) return "relative";
            foreach (FigmaNode c in node.Children)
                if (c.LayoutPositioning == "ABSOLUTE") return "relative";
            return null;
        }

        static void ApplyPadding(FigmaNode n, CssBlock b)
        {
            double t = n.PaddingTop, r = n.PaddingRight, bo = n.PaddingBottom, l = n.PaddingLeft;
            if (t == 0 && r == 0 && bo == 0 && l == 0) return;
            if (t == r && r == bo && bo == l) b.Set("padding", CssText.Px(t));
            else if (t == bo && l == r) b.Set("padding", CssText.Px(t) + " " + CssText.Px(r));
            else b.Set("padding", $"{CssText.Px(t)} {CssText.Px(r)} {CssText.Px(bo)} {CssText.Px(l)}");
        }

        static void ApplyAbsoluteOffsets(FigmaNode node, FigmaNode parent, CssBlock b)
        {
            Rect p = parent.Box;
            double left = node.Box.X - p.X;
            double top = node.Box.Y - p.Y;
            double right = (p.X + p.Width) - (node.Box.X + node.Box.Width);
            double bottom = (p.Y + p.Height) - (node.Box.Y + node.Box.Height);

            string ch = node.ConstraintHorizontal;
            bool stretchH = ch == "LEFT_RIGHT" || ch == "SCALE";
            if (ch == "RIGHT") b.Set("right", CssText.Px(right));
            else if (stretchH) { b.Set("left", CssText.Px(left)); b.Set("right", CssText.Px(right)); }
            else b.Set("left", CssText.Px(left)); // LEFT, CENTER, default

            string cv = node.ConstraintVertical;
            bool stretchV = cv == "TOP_BOTTOM" || cv == "SCALE";
            if (cv == "BOTTOM") b.Set("bottom", CssText.Px(bottom));
            else if (stretchV) { b.Set("top", CssText.Px(top)); b.Set("bottom", CssText.Px(bottom)); }
            else b.Set("top", CssText.Px(top)); // TOP, CENTER, default

            if (!stretchH && node.Box.Width > 0) b.Set("width", CssText.Px(node.Box.Width));
            if (!stretchV && node.Box.Height > 0) b.Set("height", CssText.Px(node.Box.Height));
        }

        static void ApplyFlowSizing(FigmaNode node, FigmaNode parent, bool parentAuto, bool isRoot, CssBlock b)
        {
            if (isRoot)
            {
                if (node.Box.Width > 0) b.Set("width", CssText.Px(node.Box.Width));
                if (node.Box.Height > 0) b.Set("height", CssText.Px(node.Box.Height));
                return;
            }
            if (!parentAuto) return; // shouldn't happen (such a child is absolute), guard anyway

            ApplyAxisSizing(node, b, horizontal: true, parent.LayoutMode);
            ApplyAxisSizing(node, b, horizontal: false, parent.LayoutMode);
        }

        static void ApplyAxisSizing(FigmaNode node, CssBlock b, bool horizontal, string parentDir)
        {
            bool isMain = (horizontal && parentDir == "HORIZONTAL") || (!horizontal && parentDir == "VERTICAL");
            string sizing = horizontal ? node.LayoutSizingHorizontal : node.LayoutSizingVertical;
            double size = horizontal ? node.Box.Width : node.Box.Height;
            string sizeProp = horizontal ? "width" : "height";
            string minProp = horizontal ? "min-width" : "min-height";

            switch (sizing)
            {
                case "FILL":
                    if (isMain) Fill(b, minProp);
                    else b.Set("align-self", "stretch");
                    break;
                case "HUG":
                    break; // auto
                case "FIXED":
                    if (size > 0) b.Set(sizeProp, CssText.Px(size));
                    break;
                default:
                    // Older payloads without layoutSizing*: fall back to grow/align.
                    if (isMain)
                    {
                        if (node.LayoutGrow >= 1) Fill(b, minProp);
                        else if (size > 0) b.Set(sizeProp, CssText.Px(size));
                    }
                    else
                    {
                        if (node.LayoutAlign == "STRETCH") b.Set("align-self", "stretch");
                        else if (size > 0) b.Set(sizeProp, CssText.Px(size));
                    }
                    break;
            }
        }

        static void Fill(CssBlock b, string minProp)
        {
            b.Set("flex-grow", "1");
            b.Set("flex-shrink", "1");
            b.Set("flex-basis", "0%");
            b.Set(minProp, "0"); // defeat the flex min-content floor so it can fill (and shrink) freely
        }

        static string MapPrimary(string align)
        {
            switch (align)
            {
                case "CENTER": return "center";
                case "MAX": return "flex-end";
                case "SPACE_BETWEEN": return "space-between";
                default: return null; // MIN / null = flex-start (initial)
            }
        }

        static string MapCounter(string align)
        {
            switch (align)
            {
                case "CENTER": return "center";
                case "MAX": return "flex-end";
                case "BASELINE": return "baseline";
                default: return "flex-start"; // MIN / null — emit explicitly to override CSS's stretch default
            }
        }
    }
}
