using System.Text;

namespace Weva.Designer
{
    /// <summary>The output of compiling a <see cref="DesignDocument"/>.</summary>
    public sealed class DesignCompileResult
    {
        public string Html;
        public string Css;

        public override string ToString() => "/* css */\n" + Css + "\n<!-- html -->\n" + Html;
    }

    /// <summary>
    /// Lowers a <see cref="DesignDocument"/> (the authoring IR) to Weva HTML/CSS.
    /// The output is intentionally clean and scoped: every node gets one generated
    /// class (<c>w0</c>, <c>w1</c>, …) so there is never a cascade/specificity
    /// conflict — the whole reason the editor can promise "no CSS surprises".
    ///
    /// The Fill/Hug/Fixed → flex lowering mirrors the (tested) figma LayoutMapper;
    /// the two compilers converge in M8. See WEVA_EDITOR_PLAN.md.
    /// </summary>
    public sealed class DesignCompiler
    {
        readonly StringBuilder _css = new StringBuilder();
        readonly StringBuilder _html = new StringBuilder();
        int _counter;

        public DesignCompileResult Compile(DesignDocument doc)
        {
            _css.Clear();
            _html.Clear();
            _counter = 0;

            // Box-sizing border-box so Fixed sizes + padding behave like Figma.
            _css.Append("* { box-sizing: border-box; }\n");

            string root = doc.Tokens.EmitRoot();
            if (root.Length > 0) _css.Append(root);

            if (doc.Root != null)
            {
                // Expand component instances first so the emit walk sees a concrete tree.
                DesignNode tree = doc.Components.Count > 0
                    ? DesignExpander.Expand(doc.Root, doc)
                    : doc.Root;
                Emit(tree, doc.Tokens, LayoutMode.None, isRoot: true, depth: 0);
            }

            return new DesignCompileResult { Html = _html.ToString(), Css = _css.ToString() };
        }

        void Emit(DesignNode node, DesignTokens tokens, LayoutMode parentLayout, bool isRoot, int depth)
        {
            int id = _counter++;
            string cls = "w" + id;

            var d = new CssDecls();
            ApplyContainer(node, tokens, d);
            ApplyPosition(node, tokens, d);
            ApplySizing(node, d, parentLayout, isRoot || node.IsAbsolute);
            ApplyConstraints(node, d);
            ApplyStyle(node, tokens, d);

            if (!d.IsEmpty)
            {
                _css.Append('.').Append(cls).Append(" {\n");
                d.RenderInto(_css, "  ");
                _css.Append("}\n");
            }

            EmitStates(node, tokens, cls);

            string indent = new string(' ', depth * 2);
            _html.Append(indent).Append("<div class=\"").Append(cls).Append('"');
            if (!string.IsNullOrEmpty(node.Name))
                _html.Append(" data-name=\"").Append(DesignCssText.EscapeAttr(node.Name)).Append('"');
            AppendBindingAttrs(node);
            _html.Append('>');

            string bindText = node.Binding?.Text;
            if (bindText != null)
            {
                _html.Append("{{ ").Append(DesignCssText.EscapeText(bindText)).Append(" }}</div>\n");
                return;
            }

            if (node.IsText)
            {
                _html.Append(DesignCssText.EscapeText(node.Text)).Append("</div>\n");
                return;
            }

            if (node.Children.Count == 0)
            {
                _html.Append("</div>\n");
                return;
            }

            _html.Append('\n');
            foreach (DesignNode child in node.Children)
                Emit(child, tokens, node.Layout, isRoot: false, depth: depth + 1);
            _html.Append(indent).Append("</div>\n");
        }

        // --- Container (this node arranges its children) ---

        static void ApplyContainer(DesignNode n, DesignTokens tokens, CssDecls d)
        {
            if (n.Layout == LayoutMode.Row || n.Layout == LayoutMode.Column)
            {
                d.Set("display", "flex");
                d.Set("flex-direction", n.Layout == LayoutMode.Row ? "row" : "column");
                if (n.Wrap) d.Set("flex-wrap", "wrap");

                string justify = MapMain(n.MainAlign);
                if (justify != null) d.Set("justify-content", justify);

                string align = MapCross(n.CrossAlign);
                if (align != null) d.Set("align-items", align);

                if (!n.Gap.IsZero && n.MainAlign != MainAlign.SpaceBetween)
                    d.Set("gap", n.Gap.Resolve(tokens, "space"));
            }
            else if (n.Layout == LayoutMode.Grid)
            {
                d.Set("display", "grid");
                // Equal columns. minmax(0, 1fr) (not 1fr) so wide children can't blow out
                // the track — the classic CSS-grid min-content overflow trap.
                int cols = n.GridColumns >= 1 ? n.GridColumns : 1;
                d.Set("grid-template-columns",
                    cols == 1 ? "minmax(0, 1fr)" : "repeat(" + cols + ", minmax(0, 1fr))");
                if (!n.Gap.IsZero) d.Set("gap", n.Gap.Resolve(tokens, "space"));
            }

            ApplyPadding(n, tokens, d);
        }

        static void ApplyPadding(DesignNode n, DesignTokens tokens, CssDecls d)
        {
            Dim t = n.PadTop, r = n.PadRight, b = n.PadBottom, l = n.PadLeft;
            if (t.IsZero && r.IsZero && b.IsZero && l.IsZero) return;
            if (t == r && r == b && b == l)
                d.Set("padding", t.Resolve(tokens, "space"));
            else if (t == b && l == r)
                d.Set("padding", t.Resolve(tokens, "space") + " " + r.Resolve(tokens, "space"));
            else
                d.Set("padding", t.Resolve(tokens, "space") + " " + r.Resolve(tokens, "space") + " "
                                 + b.Resolve(tokens, "space") + " " + l.Resolve(tokens, "space"));
        }

        // --- Sizing (this node as a child) ---

        static void ApplySizing(DesignNode n, CssDecls d, LayoutMode parentLayout, bool isRoot)
        {
            if (isRoot)
            {
                if (n.WidthMode == SizeMode.Fixed && n.Width > 0) d.Set("width", DesignCssText.Px(n.Width));
                if (n.HeightMode == SizeMode.Fixed && n.Height > 0) d.Set("height", DesignCssText.Px(n.Height));
                return;
            }

            ApplyAxis(n, d, horizontal: true, parentLayout);
            ApplyAxis(n, d, horizontal: false, parentLayout);
        }

        static void ApplyAxis(DesignNode n, CssDecls d, bool horizontal, LayoutMode parentLayout)
        {
            SizeMode mode = horizontal ? n.WidthMode : n.HeightMode;
            double size = horizontal ? n.Width : n.Height;
            string sizeProp = horizontal ? "width" : "height";
            string minProp = horizontal ? "min-width" : "min-height";

            bool parentIsFlex = parentLayout == LayoutMode.Row || parentLayout == LayoutMode.Column;
            if (!parentIsFlex)
            {
                // Block-flow parent: only Fixed and horizontal-Fill are meaningful.
                if (mode == SizeMode.Fixed && size > 0) d.Set(sizeProp, DesignCssText.Px(size));
                else if (mode == SizeMode.Fill && horizontal) d.Set("width", "100%");
                return;
            }

            bool isMain = (horizontal && parentLayout == LayoutMode.Row)
                          || (!horizontal && parentLayout == LayoutMode.Column);

            switch (mode)
            {
                case SizeMode.Fill:
                    if (isMain)
                    {
                        d.Set("flex-grow", "1");
                        d.Set("flex-shrink", "1");
                        d.Set("flex-basis", "0%");
                        // Defeat the flex min-content floor so it fills freely — but only
                        // when the author hasn't set an explicit min (ApplyConstraints owns
                        // that; CssDecls.Set appends, so a guard avoids a duplicate decl).
                        double userMin = horizontal ? n.MinWidth : n.MinHeight;
                        if (userMin <= 0) d.Set(minProp, "0");
                    }
                    else
                    {
                        d.Set("align-self", "stretch");
                    }
                    break;
                case SizeMode.Fixed:
                    if (size > 0) d.Set(sizeProp, DesignCssText.Px(size));
                    break;
                case SizeMode.Hug:
                default:
                    break; // auto
            }
        }

        // --- Placement (out-of-flow overlays) ---

        static void ApplyPosition(DesignNode n, DesignTokens tokens, CssDecls d)
        {
            if (n.IsAbsolute)
            {
                d.Set("position", "absolute");
                if (n.OffTop.HasValue) d.Set("top", n.OffTop.Value.Resolve(tokens, "space"));
                if (n.OffRight.HasValue) d.Set("right", n.OffRight.Value.Resolve(tokens, "space"));
                if (n.OffBottom.HasValue) d.Set("bottom", n.OffBottom.Value.Resolve(tokens, "space"));
                if (n.OffLeft.HasValue) d.Set("left", n.OffLeft.Value.Resolve(tokens, "space"));
            }
            else if (HasAbsoluteChild(n))
            {
                // Establish the positioning context so absolute children pin to this box.
                d.Set("position", "relative");
            }
        }

        static bool HasAbsoluteChild(DesignNode n)
        {
            for (int i = 0; i < n.Children.Count; i++)
                if (n.Children[i].IsAbsolute) return true;
            return false;
        }

        // --- Size constraints (min/max, independent of sizing mode) ---

        static void ApplyConstraints(DesignNode n, CssDecls d)
        {
            if (n.MinWidth > 0) d.Set("min-width", DesignCssText.Px(n.MinWidth));
            if (n.MaxWidth > 0) d.Set("max-width", DesignCssText.Px(n.MaxWidth));
            if (n.MinHeight > 0) d.Set("min-height", DesignCssText.Px(n.MinHeight));
            if (n.MaxHeight > 0) d.Set("max-height", DesignCssText.Px(n.MaxHeight));
            if (n.AspectRatio > 0) d.Set("aspect-ratio", DesignCssText.Num(n.AspectRatio));
        }

        // --- Data binding attributes ---

        void AppendBindingAttrs(DesignNode node)
        {
            NodeBinding b = node.Binding;
            if (b == null) return;

            if (!string.IsNullOrEmpty(b.RepeatEach))
                Attr("data-each", b.RepeatEach);
            if (!string.IsNullOrEmpty(b.RepeatKey))
                Attr("data-key", b.RepeatKey);

            if (b.Classes != null)
                foreach (var kv in b.Classes)
                    Attr("data-class-" + kv.Key, kv.Value);

            if (b.Events != null)
                foreach (var kv in b.Events)
                    Attr("on-" + kv.Key, kv.Value);
        }

        void Attr(string name, string value)
        {
            _html.Append(' ').Append(name).Append("=\"").Append(DesignCssText.EscapeAttr(value)).Append('"');
        }

        // --- Interactive states ---

        void EmitStates(DesignNode node, DesignTokens tokens, string cls)
        {
            if (!node.HasStates) return;
            foreach (InteractionState s in DesignNode.AllStates)
            {
                StateStyle st = node.GetState(s);
                if (st == null || st.IsEmpty) continue;

                var d = new CssDecls();
                string fill = tokens.ResolvePaint(st.Fill);
                if (fill != null) d.Set("background", fill);
                if (st.Radius.HasValue && !st.Radius.Value.IsZero)
                    d.Set("border-radius", st.Radius.Value.Resolve(tokens, "radius"));
                if (st.Opacity.HasValue) d.Set("opacity", DesignCssText.Num(st.Opacity.Value));
                string shadow = tokens.ResolveShadow(st.Shadow);
                if (shadow != null) d.Set("box-shadow", shadow);
                string color = tokens.ResolveColor(st.TextColor);
                if (color != null) d.Set("color", color);
                string stroke = tokens.ResolveColor(st.Stroke);
                if (stroke != null) d.Set("border", Border(st.StrokeWidth ?? 0, stroke));
                else if (st.StrokeWidth.HasValue) d.Set("border-width", DesignCssText.Px(st.StrokeWidth.Value));
                if (st.TextDecoration.HasValue)
                    d.Set("text-decoration", TextDecorationCss(st.TextDecoration.Value) ?? "none");
                if (st.FontWeight.HasValue) d.Set("font-weight", FontWeightCss(st.FontWeight.Value));
                if (d.IsEmpty) continue;

                _css.Append('.').Append(cls).Append(StateSelector(s)).Append(" {\n");
                d.RenderInto(_css, "  ");
                _css.Append("}\n");
            }
        }

        static string StateSelector(InteractionState s)
        {
            switch (s)
            {
                case InteractionState.Hover: return ":hover";
                case InteractionState.Pressed: return ":active";
                case InteractionState.Focus: return ":focus";
                case InteractionState.Disabled: return ".is-disabled";
                default: return "";
            }
        }

        // --- Style ---

        static void ApplyStyle(DesignNode n, DesignTokens tokens, CssDecls d)
        {
            string fill = tokens.ResolvePaint(n.Fill);
            if (fill != null) d.Set("background", fill);

            // Background image layers over the fill. Emitted after the `background`
            // shorthand so the shorthand's implicit reset of background-image is overridden.
            if (!string.IsNullOrEmpty(n.BackgroundImage))
            {
                d.Set("background-image", "url(" + DesignCssText.Url(n.BackgroundImage) + ")");
                d.Set("background-size", BackgroundSizeCss(n.BackgroundSize));
                d.Set("background-position", "center");
                d.Set("background-repeat", "no-repeat");
            }

            string stroke = tokens.ResolveColor(n.Stroke);
            if (stroke != null)
                d.Set("border", Border(n.StrokeWidth, stroke));

            if (n.HasPerCornerRadius)
            {
                // 4-value shorthand TL TR BR BL; each corner falls back to the uniform Radius.
                d.Set("border-radius",
                    Corner(n.RadiusTopLeft, n.Radius, tokens) + " " + Corner(n.RadiusTopRight, n.Radius, tokens) + " "
                    + Corner(n.RadiusBottomRight, n.Radius, tokens) + " " + Corner(n.RadiusBottomLeft, n.Radius, tokens));
            }
            else if (!n.Radius.IsZero)
            {
                d.Set("border-radius", n.Radius.Resolve(tokens, "radius"));
            }

            string overflow = OverflowCss(n.Overflow);
            if (overflow != null) d.Set("overflow", overflow);

            if (n.Opacity < 1) d.Set("opacity", DesignCssText.Num(n.Opacity));

            string shadow = tokens.ResolveShadow(n.Shadow);
            if (shadow != null) d.Set("box-shadow", shadow);

            string xform = TransformCss(n);
            if (xform != null) d.Set("transform", xform);

            if (n.Cursor == Cursor.Pointer) d.Set("cursor", "pointer");

            // Animate style changes (into hover/pressed/etc) over the requested duration.
            if (n.TransitionMs > 0) d.Set("transition", "all " + DesignCssText.Num(n.TransitionMs) + "ms ease");

            if (n.IsText || n.Binding?.Text != null)
            {
                string color = tokens.ResolveColor(n.TextColor);
                if (color != null) d.Set("color", color);
                if (!n.FontSize.IsZero) d.Set("font-size", n.FontSize.Resolve(tokens, "font"));
                if (n.FontWeight != FontWeight.Normal) d.Set("font-weight", FontWeightCss(n.FontWeight));
                if (n.Italic) d.Set("font-style", "italic");
                string align = TextAlignCss(n.TextAlign);
                if (align != null) d.Set("text-align", align);
                if (n.LineHeight > 0) d.Set("line-height", DesignCssText.Num(n.LineHeight));
                if (n.LetterSpacing != 0) d.Set("letter-spacing", DesignCssText.Px(n.LetterSpacing));
                string transform = TextTransformCss(n.TextTransform);
                if (transform != null) d.Set("text-transform", transform);
                string decoration = TextDecorationCss(n.TextDecoration);
                if (decoration != null) d.Set("text-decoration", decoration);
                // Glyph drop-shadow for legibility over busy backgrounds. Reuses the shadow
                // token table (raw value passes through, {token} → var(--shadow-…)).
                string textShadow = tokens.ResolveShadow(n.TextShadow);
                if (textShadow != null) d.Set("text-shadow", textShadow);
            }
        }

        static string TextTransformCss(TextTransform t)
        {
            switch (t)
            {
                case TextTransform.Uppercase: return "uppercase";
                case TextTransform.Lowercase: return "lowercase";
                case TextTransform.Capitalize: return "capitalize";
                default: return null;
            }
        }

        static string TextDecorationCss(TextDecoration t)
        {
            switch (t)
            {
                case TextDecoration.Underline: return "underline";
                case TextDecoration.LineThrough: return "line-through";
                default: return null;
            }
        }

        /// <summary>Resolve one corner: the per-corner override if set, else the uniform radius.</summary>
        static string Corner(Dim? corner, Dim uniform, DesignTokens tokens)
            => (corner ?? uniform).Resolve(tokens, "radius");

        /// <summary>Compose rotate()/scale() into a transform, or null if neither is set.</summary>
        static string TransformCss(DesignNode n)
        {
            bool hasRot = n.Rotation != 0;
            bool hasScale = n.Scale != 1;
            if (!hasRot && !hasScale) return null;
            if (hasRot && hasScale)
                return "rotate(" + DesignCssText.Num(n.Rotation) + "deg) scale(" + DesignCssText.Num(n.Scale) + ")";
            return hasRot
                ? "rotate(" + DesignCssText.Num(n.Rotation) + "deg)"
                : "scale(" + DesignCssText.Num(n.Scale) + ")";
        }

        static string BackgroundSizeCss(BackgroundSize s)
        {
            switch (s)
            {
                case BackgroundSize.Contain: return "contain";
                case BackgroundSize.Stretch: return "100% 100%";
                default: return "cover";
            }
        }

        static string OverflowCss(Overflow o)
        {
            switch (o)
            {
                case Overflow.Clip: return "hidden";
                case Overflow.Scroll: return "auto";
                default: return null; // Visible = initial
            }
        }

        static string FontWeightCss(FontWeight w)
        {
            switch (w)
            {
                case FontWeight.Medium: return "500";
                case FontWeight.SemiBold: return "600";
                case FontWeight.Bold: return "700";
                default: return "400";
            }
        }

        static string TextAlignCss(TextAlign a)
        {
            switch (a)
            {
                case TextAlign.Center: return "center";
                case TextAlign.End: return "end";
                case TextAlign.Justify: return "justify";
                default: return null; // Start = initial
            }
        }

        /// <summary>
        /// Build a <c>border</c> shorthand. A non-positive width means "the designer set a
        /// stroke color but no weight", which lowers to a sensible 1px solid default
        /// (matching Figma's default stroke weight).
        /// </summary>
        static string Border(double width, string color)
        {
            double w = width > 0 ? width : 1;
            return DesignCssText.Px(w) + " solid " + color;
        }

        static string MapMain(MainAlign a)
        {
            switch (a)
            {
                case MainAlign.Center: return "center";
                case MainAlign.End: return "flex-end";
                case MainAlign.SpaceBetween: return "space-between";
                default: return null; // Start = flex-start (initial)
            }
        }

        static string MapCross(CrossAlign a)
        {
            switch (a)
            {
                case CrossAlign.Start: return "flex-start"; // explicit: override CSS's stretch default
                case CrossAlign.Center: return "center";
                case CrossAlign.End: return "flex-end";
                default: return null; // Stretch = initial
            }
        }
    }
}
