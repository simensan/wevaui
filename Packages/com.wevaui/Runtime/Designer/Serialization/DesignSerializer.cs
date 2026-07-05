using System.Collections.Generic;

namespace Weva.Designer.Serialization
{
    /// <summary>
    /// Persists a <see cref="DesignDocument"/> to/from a stable, versioned, text
    /// (JSON) format — the on-disk representation the editor saves and loads.
    ///
    /// Design goals (production bar): round-trips losslessly, diff-friendly
    /// (deterministic key order, only non-default fields emitted), and
    /// forward-compatible (unknown keys ignored; missing keys defaulted) so newer
    /// documents degrade gracefully in older editors and vice-versa.
    /// </summary>
    public static class DesignSerializer
    {
        public const int CurrentVersion = 1;

        public static string Serialize(DesignDocument doc)
        {
            var root = JsonVal.NewObject();
            root.Set("version", JsonVal.Of(CurrentVersion));
            root.Set("tokens", SerializeTokens(doc.Tokens));
            if (doc.Components.Count > 0) root.Set("components", SerializeComponents(doc));
            if (doc.Root != null) root.Set("root", SerializeNode(doc.Root));
            return Json.Write(root);
        }

        public static DesignDocument Deserialize(string text)
        {
            JsonVal root = Json.Parse(text);
            if (root == null || !root.IsObject)
                throw new JsonException("design document must be a JSON object");

            var doc = new DesignDocument();
            DeserializeTokens(root.Get("tokens"), doc.Tokens);
            DeserializeComponents(root.Get("components"), doc);
            JsonVal rootNode = root.Get("root");
            if (rootNode != null && rootNode.IsObject)
                doc.Root = DeserializeNode(rootNode);
            return doc;
        }

        // --- Tokens ---

        static JsonVal SerializeTokens(DesignTokens t)
        {
            var obj = JsonVal.NewObject();
            if (t.Colors.Count > 0) obj.Set("colors", StringTable(t.Colors));
            if (t.Spacing.Count > 0) obj.Set("spacing", NumberTable(t.Spacing));
            if (t.Radii.Count > 0) obj.Set("radii", NumberTable(t.Radii));
            if (t.FontSizes.Count > 0) obj.Set("fontSizes", NumberTable(t.FontSizes));
            if (t.Shadows.Count > 0) obj.Set("shadows", StringTable(t.Shadows));
            if (t.Gradients.Count > 0) obj.Set("gradients", StringTable(t.Gradients));
            return obj;
        }

        static void DeserializeTokens(JsonVal obj, DesignTokens t)
        {
            if (obj == null || !obj.IsObject) return;
            ReadStringTable(obj.Get("colors"), (k, v) => t.Color(k, v));
            ReadNumberTable(obj.Get("spacing"), (k, v) => t.Space(k, v));
            ReadNumberTable(obj.Get("radii"), (k, v) => t.Radius(k, v));
            ReadNumberTable(obj.Get("fontSizes"), (k, v) => t.Font(k, v));
            ReadStringTable(obj.Get("shadows"), (k, v) => t.Shadow(k, v));
            ReadStringTable(obj.Get("gradients"), (k, v) => t.Gradient(k, v));
        }

        static JsonVal StringTable(Dictionary<string, string> table)
        {
            var obj = JsonVal.NewObject();
            foreach (var kv in table) obj.Set(kv.Key, JsonVal.Of(kv.Value));
            return obj;
        }

        static JsonVal NumberTable(Dictionary<string, double> table)
        {
            var obj = JsonVal.NewObject();
            foreach (var kv in table) obj.Set(kv.Key, JsonVal.Of(kv.Value));
            return obj;
        }

        static void ReadStringTable(JsonVal obj, System.Action<string, string> add)
        {
            if (obj == null || !obj.IsObject) return;
            foreach (var kv in obj.Members) add(kv.Key, kv.Value.AsString(""));
        }

        static void ReadNumberTable(JsonVal obj, System.Action<string, double> add)
        {
            if (obj == null || !obj.IsObject) return;
            foreach (var kv in obj.Members) add(kv.Key, kv.Value.AsDouble());
        }

        // --- Components ---

        static JsonVal SerializeComponents(DesignDocument doc)
        {
            var obj = JsonVal.NewObject();
            foreach (var kv in doc.Components)
            {
                DesignComponent comp = kv.Value;
                var co = JsonVal.NewObject();
                if (comp.Props.Count > 0) co.Set("props", StringTable(comp.Props));
                if (comp.Variants.Count > 0)
                {
                    var vo = JsonVal.NewObject();
                    foreach (var v in comp.Variants) vo.Set(v.Key, StringTable(v.Value));
                    co.Set("variants", vo);
                }
                if (comp.Template != null) co.Set("template", SerializeNode(comp.Template));
                obj.Set(kv.Key, co);
            }
            return obj;
        }

        static void DeserializeComponents(JsonVal obj, DesignDocument doc)
        {
            if (obj == null || !obj.IsObject) return;
            foreach (var kv in obj.Members)
            {
                JsonVal co = kv.Value;
                if (co == null || !co.IsObject) continue;
                var comp = new DesignComponent { Name = kv.Key };
                ReadStringTable(co.Get("props"), (k, v) => comp.Props[k] = v);
                JsonVal variants = co.Get("variants");
                if (variants != null && variants.IsObject)
                    foreach (var v in variants.Members)
                    {
                        var vp = new Dictionary<string, string>();
                        ReadStringTable(v.Value, (k, val) => vp[k] = val);
                        comp.Variants[v.Key] = vp;
                    }
                JsonVal tpl = co.Get("template");
                if (tpl != null && tpl.IsObject) comp.Template = DeserializeNode(tpl);
                doc.Components[kv.Key] = comp;
            }
        }

        // --- Node ---

        static JsonVal SerializeNode(DesignNode n)
        {
            var obj = JsonVal.NewObject();
            if (!string.IsNullOrEmpty(n.Name)) obj.Set("name", JsonVal.Of(n.Name));
            if (n.Text != null) obj.Set("text", JsonVal.Of(n.Text));

            if (n.Layout != LayoutMode.None) obj.Set("layout", JsonVal.Of(LayoutToStr(n.Layout)));
            if (n.MainAlign != MainAlign.Start) obj.Set("mainAlign", JsonVal.Of(MainToStr(n.MainAlign)));
            if (n.CrossAlign != CrossAlign.Start) obj.Set("crossAlign", JsonVal.Of(CrossToStr(n.CrossAlign)));
            if (n.GridColumns != 0) obj.Set("gridColumns", JsonVal.Of(n.GridColumns));
            if (n.Wrap) obj.Set("wrap", JsonVal.Of(true));
            if (!n.Gap.IsZero) obj.Set("gap", DimToJson(n.Gap));

            if (!n.PadTop.IsZero || !n.PadRight.IsZero || !n.PadBottom.IsZero || !n.PadLeft.IsZero)
            {
                var pad = JsonVal.NewArray();
                pad.Add(DimToJson(n.PadTop));
                pad.Add(DimToJson(n.PadRight));
                pad.Add(DimToJson(n.PadBottom));
                pad.Add(DimToJson(n.PadLeft));
                obj.Set("padding", pad);
            }

            if (n.WidthMode != SizeMode.Hug) obj.Set("widthMode", JsonVal.Of(SizeToStr(n.WidthMode)));
            if (n.HeightMode != SizeMode.Hug) obj.Set("heightMode", JsonVal.Of(SizeToStr(n.HeightMode)));
            if (n.Width != 0) obj.Set("width", JsonVal.Of(n.Width));
            if (n.Height != 0) obj.Set("height", JsonVal.Of(n.Height));
            if (n.MinWidth != 0) obj.Set("minWidth", JsonVal.Of(n.MinWidth));
            if (n.MaxWidth != 0) obj.Set("maxWidth", JsonVal.Of(n.MaxWidth));
            if (n.MinHeight != 0) obj.Set("minHeight", JsonVal.Of(n.MinHeight));
            if (n.MaxHeight != 0) obj.Set("maxHeight", JsonVal.Of(n.MaxHeight));
            if (n.AspectRatio != 0) obj.Set("aspectRatio", JsonVal.Of(n.AspectRatio));

            if (n.Position != Position.InFlow) obj.Set("position", JsonVal.Of("absolute"));
            if (n.OffTop.HasValue) obj.Set("offTop", DimToJson(n.OffTop.Value));
            if (n.OffRight.HasValue) obj.Set("offRight", DimToJson(n.OffRight.Value));
            if (n.OffBottom.HasValue) obj.Set("offBottom", DimToJson(n.OffBottom.Value));
            if (n.OffLeft.HasValue) obj.Set("offLeft", DimToJson(n.OffLeft.Value));

            if (!string.IsNullOrEmpty(n.Fill)) obj.Set("fill", JsonVal.Of(n.Fill));
            if (!string.IsNullOrEmpty(n.BackgroundImage)) obj.Set("bgImage", JsonVal.Of(n.BackgroundImage));
            if (n.BackgroundSize != BackgroundSize.Cover) obj.Set("bgSize", JsonVal.Of(BgSizeToStr(n.BackgroundSize)));
            if (!string.IsNullOrEmpty(n.Stroke)) obj.Set("stroke", JsonVal.Of(n.Stroke));
            if (n.StrokeWidth != 0) obj.Set("strokeWidth", JsonVal.Of(n.StrokeWidth));
            if (!n.Radius.IsZero) obj.Set("radius", DimToJson(n.Radius));
            if (n.RadiusTopLeft.HasValue) obj.Set("radiusTL", DimToJson(n.RadiusTopLeft.Value));
            if (n.RadiusTopRight.HasValue) obj.Set("radiusTR", DimToJson(n.RadiusTopRight.Value));
            if (n.RadiusBottomRight.HasValue) obj.Set("radiusBR", DimToJson(n.RadiusBottomRight.Value));
            if (n.RadiusBottomLeft.HasValue) obj.Set("radiusBL", DimToJson(n.RadiusBottomLeft.Value));
            if (n.Overflow != Overflow.Visible) obj.Set("overflow", JsonVal.Of(OverflowToStr(n.Overflow)));
            if (n.Opacity != 1) obj.Set("opacity", JsonVal.Of(n.Opacity));
            if (!string.IsNullOrEmpty(n.Shadow)) obj.Set("shadow", JsonVal.Of(n.Shadow));
            if (n.Cursor != Cursor.Default) obj.Set("cursor", JsonVal.Of("pointer"));
            if (n.TransitionMs != 0) obj.Set("transitionMs", JsonVal.Of(n.TransitionMs));
            if (n.Rotation != 0) obj.Set("rotation", JsonVal.Of(n.Rotation));
            if (n.Scale != 1) obj.Set("scale", JsonVal.Of(n.Scale));

            if (!string.IsNullOrEmpty(n.TextColor)) obj.Set("textColor", JsonVal.Of(n.TextColor));
            if (!n.FontSize.IsZero) obj.Set("fontSize", DimToJson(n.FontSize));
            if (n.FontWeight != FontWeight.Normal) obj.Set("fontWeight", JsonVal.Of(WeightToStr(n.FontWeight)));
            if (n.Italic) obj.Set("italic", JsonVal.Of(true));
            if (n.TextAlign != TextAlign.Start) obj.Set("textAlign", JsonVal.Of(AlignToStr(n.TextAlign)));
            if (n.LineHeight != 0) obj.Set("lineHeight", JsonVal.Of(n.LineHeight));
            if (n.LetterSpacing != 0) obj.Set("letterSpacing", JsonVal.Of(n.LetterSpacing));
            if (n.TextTransform != TextTransform.None) obj.Set("textTransform", JsonVal.Of(TransformToStr(n.TextTransform)));
            if (n.TextDecoration != TextDecoration.None) obj.Set("textDecoration", JsonVal.Of(DecorationToStr(n.TextDecoration)));
            if (!string.IsNullOrEmpty(n.TextShadow)) obj.Set("textShadow", JsonVal.Of(n.TextShadow));

            if (n.HasStates) SerializeStates(n, obj);
            if (n.HasBinding) SerializeBinding(n, obj);

            if (!string.IsNullOrEmpty(n.ComponentRef)) obj.Set("component", JsonVal.Of(n.ComponentRef));
            if (!string.IsNullOrEmpty(n.Variant)) obj.Set("variant", JsonVal.Of(n.Variant));
            if (n.IsSlot) obj.Set("slot", JsonVal.Of(true));
            if (n.Props != null && n.Props.Count > 0) obj.Set("props", StringTable(n.Props));

            if (n.Children.Count > 0)
            {
                var kids = JsonVal.NewArray();
                foreach (DesignNode c in n.Children) kids.Add(SerializeNode(c));
                obj.Set("children", kids);
            }
            return obj;
        }

        static DesignNode DeserializeNode(JsonVal obj)
        {
            var n = new DesignNode
            {
                Name = obj.GetString("name"),
                Text = obj.Get("text") != null ? obj.GetString("text") : null,
                Layout = StrToLayout(obj.GetString("layout"), LayoutMode.None),
                MainAlign = StrToMain(obj.GetString("mainAlign"), MainAlign.Start),
                CrossAlign = StrToCross(obj.GetString("crossAlign"), CrossAlign.Start),
                GridColumns = (int)obj.GetDouble("gridColumns"),
                Wrap = obj.GetBool("wrap"),
                Gap = ReadDim(obj.Get("gap")),
                WidthMode = StrToSize(obj.GetString("widthMode"), SizeMode.Hug),
                HeightMode = StrToSize(obj.GetString("heightMode"), SizeMode.Hug),
                Width = obj.GetDouble("width"),
                Height = obj.GetDouble("height"),
                MinWidth = obj.GetDouble("minWidth"),
                MaxWidth = obj.GetDouble("maxWidth"),
                MinHeight = obj.GetDouble("minHeight"),
                MaxHeight = obj.GetDouble("maxHeight"),
                AspectRatio = obj.GetDouble("aspectRatio"),
                Position = obj.GetString("position") == "absolute" ? Position.Absolute : Position.InFlow,
                OffTop = ReadDimNullable(obj.Get("offTop")),
                OffRight = ReadDimNullable(obj.Get("offRight")),
                OffBottom = ReadDimNullable(obj.Get("offBottom")),
                OffLeft = ReadDimNullable(obj.Get("offLeft")),
                Fill = obj.GetString("fill"),
                BackgroundImage = obj.GetString("bgImage"),
                BackgroundSize = StrToBgSize(obj.GetString("bgSize"), BackgroundSize.Cover),
                Stroke = obj.GetString("stroke"),
                StrokeWidth = obj.GetDouble("strokeWidth"),
                Radius = ReadDim(obj.Get("radius")),
                RadiusTopLeft = ReadDimNullable(obj.Get("radiusTL")),
                RadiusTopRight = ReadDimNullable(obj.Get("radiusTR")),
                RadiusBottomRight = ReadDimNullable(obj.Get("radiusBR")),
                RadiusBottomLeft = ReadDimNullable(obj.Get("radiusBL")),
                Overflow = StrToOverflow(obj.GetString("overflow"), Overflow.Visible),
                Opacity = obj.GetDouble("opacity", 1),
                Shadow = obj.GetString("shadow"),
                Cursor = obj.GetString("cursor") == "pointer" ? Cursor.Pointer : Cursor.Default,
                TransitionMs = obj.GetDouble("transitionMs"),
                Rotation = obj.GetDouble("rotation"),
                Scale = obj.GetDouble("scale", 1),
                TextColor = obj.GetString("textColor"),
                FontSize = ReadDim(obj.Get("fontSize")),
                FontWeight = StrToWeight(obj.GetString("fontWeight"), FontWeight.Normal),
                Italic = obj.GetBool("italic"),
                TextAlign = StrToAlign(obj.GetString("textAlign"), TextAlign.Start),
                LineHeight = obj.GetDouble("lineHeight"),
                LetterSpacing = obj.GetDouble("letterSpacing"),
                TextTransform = StrToTransform(obj.GetString("textTransform"), TextTransform.None),
                TextDecoration = StrToDecoration(obj.GetString("textDecoration"), TextDecoration.None),
                TextShadow = obj.GetString("textShadow"),
                ComponentRef = obj.GetString("component"),
                Variant = obj.GetString("variant"),
                IsSlot = obj.GetBool("slot"),
            };
            ReadStringTable(obj.Get("props"), (k, v) => n.SetProp(k, v));

            JsonVal pad = obj.Get("padding");
            if (pad != null && pad.IsArray && pad.Items.Count == 4)
            {
                n.PadTop = ReadDim(pad.Items[0]);
                n.PadRight = ReadDim(pad.Items[1]);
                n.PadBottom = ReadDim(pad.Items[2]);
                n.PadLeft = ReadDim(pad.Items[3]);
            }

            DeserializeStates(obj.Get("states"), n);
            DeserializeBinding(obj.Get("binding"), n);

            JsonVal kids = obj.Get("children");
            if (kids != null && kids.IsArray)
                foreach (JsonVal c in kids.Items)
                    if (c.IsObject) n.Children.Add(DeserializeNode(c));

            return n;
        }

        // --- Interactive states ---

        static void SerializeStates(DesignNode n, JsonVal obj)
        {
            var states = JsonVal.NewObject();
            foreach (InteractionState s in DesignNode.AllStates)
            {
                StateStyle st = n.GetState(s);
                if (st == null || st.IsEmpty) continue;
                var so = JsonVal.NewObject();
                if (!string.IsNullOrEmpty(st.Fill)) so.Set("fill", JsonVal.Of(st.Fill));
                if (!string.IsNullOrEmpty(st.TextColor)) so.Set("textColor", JsonVal.Of(st.TextColor));
                if (!string.IsNullOrEmpty(st.Shadow)) so.Set("shadow", JsonVal.Of(st.Shadow));
                if (!string.IsNullOrEmpty(st.Stroke)) so.Set("stroke", JsonVal.Of(st.Stroke));
                if (st.StrokeWidth.HasValue) so.Set("strokeWidth", JsonVal.Of(st.StrokeWidth.Value));
                if (st.Radius.HasValue) so.Set("radius", DimToJson(st.Radius.Value));
                if (st.Opacity.HasValue) so.Set("opacity", JsonVal.Of(st.Opacity.Value));
                if (st.TextDecoration.HasValue) so.Set("textDecoration", JsonVal.Of(DecorationToStr(st.TextDecoration.Value)));
                if (st.FontWeight.HasValue) so.Set("fontWeight", JsonVal.Of(WeightToStr(st.FontWeight.Value)));
                states.Set(StateName(s), so);
            }
            if (states.Members.Count > 0) obj.Set("states", states);
        }

        static void DeserializeStates(JsonVal states, DesignNode n)
        {
            if (states == null || !states.IsObject) return;
            foreach (var kv in states.Members)
            {
                if (!TryParseState(kv.Key, out InteractionState s)) continue;
                JsonVal so = kv.Value;
                if (so == null || !so.IsObject) continue;
                StateStyle st = n.State(s);
                st.Fill = so.GetString("fill");
                st.TextColor = so.GetString("textColor");
                st.Shadow = so.GetString("shadow");
                st.Stroke = so.GetString("stroke");
                if (so.Get("strokeWidth") != null) st.StrokeWidth = so.GetDouble("strokeWidth");
                if (so.Get("radius") != null) st.Radius = ReadDim(so.Get("radius"));
                if (so.Get("opacity") != null) st.Opacity = so.GetDouble("opacity");
                if (so.Get("textDecoration") != null) st.TextDecoration = StrToDecoration(so.GetString("textDecoration"), Weva.Designer.TextDecoration.None);
                if (so.Get("fontWeight") != null) st.FontWeight = StrToWeight(so.GetString("fontWeight"), Weva.Designer.FontWeight.Normal);
                if (st.IsEmpty) n.SetStateStyle(s, null);
            }
        }

        // --- Data binding ---

        static void SerializeBinding(DesignNode n, JsonVal obj)
        {
            NodeBinding b = n.Binding;
            var bo = JsonVal.NewObject();
            if (!string.IsNullOrEmpty(b.Text)) bo.Set("text", JsonVal.Of(b.Text));
            if (!string.IsNullOrEmpty(b.RepeatEach)) bo.Set("each", JsonVal.Of(b.RepeatEach));
            if (!string.IsNullOrEmpty(b.RepeatKey)) bo.Set("key", JsonVal.Of(b.RepeatKey));
            if (b.Classes != null && b.Classes.Count > 0) bo.Set("classes", StringTable(b.Classes));
            if (b.Events != null && b.Events.Count > 0) bo.Set("events", StringTable(b.Events));
            if (bo.Members.Count > 0) obj.Set("binding", bo);
        }

        static void DeserializeBinding(JsonVal bo, DesignNode n)
        {
            if (bo == null || !bo.IsObject) return;
            NodeBinding b = n.Bind();
            b.Text = bo.GetString("text");
            b.RepeatEach = bo.GetString("each");
            b.RepeatKey = bo.GetString("key");
            ReadStringTable(bo.Get("classes"), (k, v) => b.BindClass(k, v));
            ReadStringTable(bo.Get("events"), (k, v) => b.BindEvent(k, v));
            if (b.IsEmpty) n.Binding = null;
        }

        static string StateName(InteractionState s)
        {
            switch (s)
            {
                case InteractionState.Hover: return "hover";
                case InteractionState.Pressed: return "pressed";
                case InteractionState.Focus: return "focus";
                case InteractionState.Disabled: return "disabled";
                default: return "hover";
            }
        }

        static bool TryParseState(string name, out InteractionState s)
        {
            switch (name)
            {
                case "hover": s = InteractionState.Hover; return true;
                case "pressed": s = InteractionState.Pressed; return true;
                case "focus": s = InteractionState.Focus; return true;
                case "disabled": s = InteractionState.Disabled; return true;
                default: s = InteractionState.Hover; return false;
            }
        }

        // --- Dim (px number or "{token}" string) ---

        static JsonVal DimToJson(Dim d)
            => d.HasToken ? JsonVal.Of("{" + d.TokenName + "}") : JsonVal.Of(d.Px);

        static Dim? ReadDimNullable(JsonVal v)
        {
            if (v == null) return null;
            return ReadDim(v);
        }

        static Dim ReadDim(JsonVal v)
        {
            if (v == null) return default;
            if (v.Kind == JsonKind.Number) return Dim.Of(v.Number);
            if (v.Kind == JsonKind.String)
            {
                string s = v.Str;
                if (s != null && s.Length > 2 && s[0] == '{' && s[s.Length - 1] == '}')
                    return Dim.Token(s.Substring(1, s.Length - 2));
            }
            return default;
        }

        // --- Enum <-> string ---

        static string LayoutToStr(LayoutMode m)
        {
            switch (m) { case LayoutMode.Row: return "row"; case LayoutMode.Column: return "column"; case LayoutMode.Grid: return "grid"; default: return "none"; }
        }
        static LayoutMode StrToLayout(string s, LayoutMode d)
        {
            switch (s) { case "row": return LayoutMode.Row; case "column": return LayoutMode.Column; case "grid": return LayoutMode.Grid; case "none": return LayoutMode.None; default: return d; }
        }

        static string SizeToStr(SizeMode m)
        {
            switch (m) { case SizeMode.Fixed: return "fixed"; case SizeMode.Fill: return "fill"; default: return "hug"; }
        }
        static SizeMode StrToSize(string s, SizeMode d)
        {
            switch (s) { case "fixed": return SizeMode.Fixed; case "fill": return SizeMode.Fill; case "hug": return SizeMode.Hug; default: return d; }
        }

        static string MainToStr(MainAlign m)
        {
            switch (m) { case MainAlign.Center: return "center"; case MainAlign.End: return "end"; case MainAlign.SpaceBetween: return "space-between"; default: return "start"; }
        }
        static MainAlign StrToMain(string s, MainAlign d)
        {
            switch (s) { case "center": return MainAlign.Center; case "end": return MainAlign.End; case "space-between": return MainAlign.SpaceBetween; case "start": return MainAlign.Start; default: return d; }
        }

        static string OverflowToStr(Overflow o)
        {
            switch (o) { case Overflow.Clip: return "clip"; case Overflow.Scroll: return "scroll"; default: return "visible"; }
        }
        static Overflow StrToOverflow(string s, Overflow d)
        {
            switch (s) { case "clip": return Overflow.Clip; case "scroll": return Overflow.Scroll; case "visible": return Overflow.Visible; default: return d; }
        }

        static string WeightToStr(FontWeight w)
        {
            switch (w) { case FontWeight.Medium: return "medium"; case FontWeight.SemiBold: return "semibold"; case FontWeight.Bold: return "bold"; default: return "normal"; }
        }
        static FontWeight StrToWeight(string s, FontWeight d)
        {
            switch (s) { case "medium": return FontWeight.Medium; case "semibold": return FontWeight.SemiBold; case "bold": return FontWeight.Bold; case "normal": return FontWeight.Normal; default: return d; }
        }

        static string BgSizeToStr(BackgroundSize s)
        {
            switch (s) { case BackgroundSize.Contain: return "contain"; case BackgroundSize.Stretch: return "stretch"; default: return "cover"; }
        }
        static BackgroundSize StrToBgSize(string s, BackgroundSize d)
        {
            switch (s) { case "contain": return BackgroundSize.Contain; case "stretch": return BackgroundSize.Stretch; case "cover": return BackgroundSize.Cover; default: return d; }
        }

        static string TransformToStr(TextTransform t)
        {
            switch (t) { case TextTransform.Uppercase: return "uppercase"; case TextTransform.Lowercase: return "lowercase"; case TextTransform.Capitalize: return "capitalize"; default: return "none"; }
        }
        static TextTransform StrToTransform(string s, TextTransform d)
        {
            switch (s) { case "uppercase": return TextTransform.Uppercase; case "lowercase": return TextTransform.Lowercase; case "capitalize": return TextTransform.Capitalize; case "none": return TextTransform.None; default: return d; }
        }

        static string DecorationToStr(TextDecoration t)
        {
            switch (t) { case TextDecoration.Underline: return "underline"; case TextDecoration.LineThrough: return "line-through"; default: return "none"; }
        }
        static TextDecoration StrToDecoration(string s, TextDecoration d)
        {
            switch (s) { case "underline": return TextDecoration.Underline; case "line-through": return TextDecoration.LineThrough; case "none": return TextDecoration.None; default: return d; }
        }

        static string AlignToStr(TextAlign a)
        {
            switch (a) { case TextAlign.Center: return "center"; case TextAlign.End: return "end"; case TextAlign.Justify: return "justify"; default: return "start"; }
        }
        static TextAlign StrToAlign(string s, TextAlign d)
        {
            switch (s) { case "center": return TextAlign.Center; case "end": return TextAlign.End; case "justify": return TextAlign.Justify; case "start": return TextAlign.Start; default: return d; }
        }

        static string CrossToStr(CrossAlign m)
        {
            switch (m) { case CrossAlign.Center: return "center"; case CrossAlign.End: return "end"; case CrossAlign.Stretch: return "stretch"; default: return "start"; }
        }
        static CrossAlign StrToCross(string s, CrossAlign d)
        {
            switch (s) { case "center": return CrossAlign.Center; case "end": return CrossAlign.End; case "stretch": return CrossAlign.Stretch; case "start": return CrossAlign.Start; default: return d; }
        }
    }
}
