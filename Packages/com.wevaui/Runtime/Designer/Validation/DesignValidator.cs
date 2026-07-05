using System.Collections.Generic;

namespace Weva.Designer.Validation
{
    public enum DiagnosticSeverity { Info, Warning, Error }

    /// <summary>One problem found by <see cref="DesignValidator"/>, for the editor to surface.</summary>
    public sealed class DesignDiagnostic
    {
        public DiagnosticSeverity Severity;
        public string Code;
        public string Message;
        public DesignNode Node; // the offending node, or null

        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }

    /// <summary>
    /// Validates a <see cref="DesignDocument"/> and reports authoring problems — unknown
    /// component/variant/token references, undeclared props, invalid repeat syntax,
    /// unknown events, misplaced slots, component cycles. The editor shows these instead
    /// of letting the document mis-render silently (house rule: never silent-wrong).
    /// Pure/read-only and headless-testable.
    /// </summary>
    public static class DesignValidator
    {
        public static List<DesignDiagnostic> Validate(DesignDocument doc)
        {
            var diags = new List<DesignDiagnostic>();
            if (doc == null) return diags;

            ValidateComponents(doc, diags);
            DetectComponentCycles(doc, diags);
            if (doc.Root != null) ValidateTree(doc.Root, doc, inComponentTemplate: false, diags);

            return diags;
        }

        public static bool HasErrors(List<DesignDiagnostic> diags)
        {
            for (int i = 0; i < diags.Count; i++)
                if (diags[i].Severity == DiagnosticSeverity.Error) return true;
            return false;
        }

        // --- Components ---

        static void ValidateComponents(DesignDocument doc, List<DesignDiagnostic> diags)
        {
            foreach (var kv in doc.Components)
            {
                DesignComponent comp = kv.Value;
                if (comp.Template == null)
                {
                    Add(diags, DiagnosticSeverity.Error, "component-no-template",
                        $"Component '{kv.Key}' has no template.", null);
                    continue;
                }
                int slots = CountSlots(comp.Template);
                if (slots > 1)
                    Add(diags, DiagnosticSeverity.Warning, "multiple-slots",
                        $"Component '{kv.Key}' has {slots} slots; only the first receives instance children.", comp.Template);
                ValidateTree(comp.Template, doc, inComponentTemplate: true, diags);
            }
        }

        static int CountSlots(DesignNode node)
        {
            int n = node.IsSlot ? 1 : 0;
            for (int i = 0; i < node.Children.Count; i++) n += CountSlots(node.Children[i]);
            return n;
        }

        static void DetectComponentCycles(DesignDocument doc, List<DesignDiagnostic> diags)
        {
            var visiting = new HashSet<string>();
            var done = new HashSet<string>();
            foreach (var name in doc.Components.Keys)
                if (VisitForCycle(name, doc, visiting, done))
                {
                    Add(diags, DiagnosticSeverity.Warning, "component-cycle",
                        $"Component '{name}' is part of a reference cycle; expansion is depth-limited.", null);
                }
        }

        static bool VisitForCycle(string name, DesignDocument doc, HashSet<string> visiting, HashSet<string> done)
        {
            if (done.Contains(name)) return false;
            if (!visiting.Add(name)) return true; // back-edge → cycle
            bool cycle = false;
            if (doc.Components.TryGetValue(name, out DesignComponent comp) && comp.Template != null)
                foreach (string referenced in ReferencedComponents(comp.Template))
                    if (VisitForCycle(referenced, doc, visiting, done)) { cycle = true; break; }
            visiting.Remove(name);
            done.Add(name);
            return cycle;
        }

        static IEnumerable<string> ReferencedComponents(DesignNode node)
        {
            if (node.IsInstance) yield return node.ComponentRef;
            for (int i = 0; i < node.Children.Count; i++)
                foreach (string r in ReferencedComponents(node.Children[i]))
                    yield return r;
        }

        // --- Node tree ---

        static void ValidateTree(DesignNode node, DesignDocument doc, bool inComponentTemplate, List<DesignDiagnostic> diags)
        {
            if (node.IsInstance) ValidateInstance(node, doc, diags);

            if (node.IsSlot && !inComponentTemplate)
                Add(diags, DiagnosticSeverity.Warning, "slot-outside-component",
                    "Slot is only meaningful inside a component template.", node);

            // Token references.
            CheckPaintRef(node.Fill, doc, node, "fill", diags);
            CheckColorRef(node.Stroke, doc, node, "stroke", diags);
            CheckColorRef(node.TextColor, doc, node, "textColor", diags);
            CheckShadowRef(node.Shadow, doc, node, diags);
            CheckDim(node.Gap, doc.Tokens.Spacing, "spacing", node, "gap", diags);
            CheckDim(node.PadTop, doc.Tokens.Spacing, "spacing", node, "padding", diags);
            CheckDim(node.PadRight, doc.Tokens.Spacing, "spacing", node, "padding", diags);
            CheckDim(node.PadBottom, doc.Tokens.Spacing, "spacing", node, "padding", diags);
            CheckDim(node.PadLeft, doc.Tokens.Spacing, "spacing", node, "padding", diags);
            CheckDim(node.Radius, doc.Tokens.Radii, "radius", node, "radius", diags);
            if (node.RadiusTopLeft.HasValue) CheckDim(node.RadiusTopLeft.Value, doc.Tokens.Radii, "radius", node, "corner radius", diags);
            if (node.RadiusTopRight.HasValue) CheckDim(node.RadiusTopRight.Value, doc.Tokens.Radii, "radius", node, "corner radius", diags);
            if (node.RadiusBottomRight.HasValue) CheckDim(node.RadiusBottomRight.Value, doc.Tokens.Radii, "radius", node, "corner radius", diags);
            if (node.RadiusBottomLeft.HasValue) CheckDim(node.RadiusBottomLeft.Value, doc.Tokens.Radii, "radius", node, "corner radius", diags);
            CheckDim(node.FontSize, doc.Tokens.FontSizes, "font", node, "fontSize", diags);

            if (node.States != null)
                foreach (var st in node.States.Values)
                {
                    CheckPaintRef(st.Fill, doc, node, "state fill", diags);
                    CheckColorRef(st.Stroke, doc, node, "state stroke", diags);
                    CheckColorRef(st.TextColor, doc, node, "state textColor", diags);
                    CheckShadowRef(st.Shadow, doc, node, diags);
                    if (st.Radius.HasValue) CheckDim(st.Radius.Value, doc.Tokens.Radii, "radius", node, "state radius", diags);
                }

            if (node.Binding != null) ValidateBinding(node, diags);

            CheckGeometry(node, diags);

            for (int i = 0; i < node.Children.Count; i++)
                ValidateTree(node.Children[i], doc, inComponentTemplate, diags);
        }

        static void ValidateInstance(DesignNode node, DesignDocument doc, List<DesignDiagnostic> diags)
        {
            if (!doc.Components.TryGetValue(node.ComponentRef, out DesignComponent comp))
            {
                Add(diags, DiagnosticSeverity.Error, "unknown-component",
                    $"Instance references unknown component '{node.ComponentRef}'.", node);
                return;
            }
            if (node.Variant != null && !comp.Variants.ContainsKey(node.Variant))
                Add(diags, DiagnosticSeverity.Warning, "unknown-variant",
                    $"Component '{node.ComponentRef}' has no variant '{node.Variant}'.", node);
            if (node.Props != null)
                foreach (string key in node.Props.Keys)
                    if (!comp.Props.ContainsKey(key))
                        Add(diags, DiagnosticSeverity.Warning, "undeclared-prop",
                            $"Prop '{key}' is not declared on component '{node.ComponentRef}'.", node);
        }

        /// <summary>
        /// Surface common geometry mistakes the sizing/placement properties make easy to
        /// hit, so the document never silently mis-renders (house rule: never silent-wrong).
        /// </summary>
        static void CheckGeometry(DesignNode n, List<DesignDiagnostic> diags)
        {
            if (n.MinWidth > 0 && n.MaxWidth > 0 && n.MaxWidth < n.MinWidth)
                Add(diags, DiagnosticSeverity.Warning, "max-below-min",
                    $"max-width ({n.MaxWidth}) is less than min-width ({n.MinWidth}); the element cannot satisfy both.", n);
            if (n.MinHeight > 0 && n.MaxHeight > 0 && n.MaxHeight < n.MinHeight)
                Add(diags, DiagnosticSeverity.Warning, "max-below-min",
                    $"max-height ({n.MaxHeight}) is less than min-height ({n.MinHeight}); the element cannot satisfy both.", n);

            if (n.WidthMode == SizeMode.Fixed && n.Width <= 0)
                Add(diags, DiagnosticSeverity.Warning, "fixed-without-size",
                    "Width sizing is Fixed but no width is set; it will hug its contents instead.", n);
            if (n.HeightMode == SizeMode.Fixed && n.Height <= 0)
                Add(diags, DiagnosticSeverity.Warning, "fixed-without-size",
                    "Height sizing is Fixed but no height is set; it will hug its contents instead.", n);

            if (!n.IsAbsolute && (n.OffTop.HasValue || n.OffRight.HasValue || n.OffBottom.HasValue || n.OffLeft.HasValue))
                Add(diags, DiagnosticSeverity.Info, "offsets-without-absolute",
                    "Edge offsets (top/right/bottom/left) are ignored unless Position is Absolute.", n);

            if (n.AspectRatio < 0)
                Add(diags, DiagnosticSeverity.Warning, "invalid-aspect-ratio",
                    $"Aspect ratio ({n.AspectRatio}) must be positive; it will be ignored.", n);
        }

        static void ValidateBinding(DesignNode node, List<DesignDiagnostic> diags)
        {
            NodeBinding b = node.Binding;
            if (!string.IsNullOrEmpty(b.RepeatEach) && !IsValidRepeat(b.RepeatEach))
                Add(diags, DiagnosticSeverity.Warning, "invalid-repeat",
                    $"Repeat expression '{b.RepeatEach}' is not of the form 'items as alias'.", node);
            if (!string.IsNullOrEmpty(b.RepeatKey) && string.IsNullOrEmpty(b.RepeatEach))
                Add(diags, DiagnosticSeverity.Warning, "key-without-repeat",
                    "data-key is set but there is no repeat expression.", node);
            if (b.Events != null)
                foreach (string ev in b.Events.Keys)
                    if (!IsKnownEvent(ev))
                        Add(diags, DiagnosticSeverity.Warning, "unknown-event",
                            $"Unknown event '{ev}'. Known: click, change, input, submit, focus, blur.", node);
        }

        static bool IsValidRepeat(string raw)
        {
            string[] parts = raw.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 && string.Equals(parts[1], "as", System.StringComparison.OrdinalIgnoreCase);
        }

        static bool IsKnownEvent(string ev)
        {
            for (int i = 0; i < NodeBinding.EventNames.Length; i++)
                if (NodeBinding.EventNames[i] == ev) return true;
            return false;
        }

        // --- Token reference checks ---

        static void CheckColorRef(string value, DesignDocument doc, DesignNode node, string where, List<DesignDiagnostic> diags)
        {
            string name = TokenName(value);
            if (name != null && !doc.Tokens.Colors.ContainsKey(name))
                Add(diags, DiagnosticSeverity.Warning, "unknown-color-token",
                    $"Unknown color token '{name}' on {where}.", node);
        }

        static void CheckPaintRef(string value, DesignDocument doc, DesignNode node, string where, List<DesignDiagnostic> diags)
        {
            string name = TokenName(value);
            if (name != null && !doc.Tokens.Colors.ContainsKey(name) && !doc.Tokens.Gradients.ContainsKey(name))
                Add(diags, DiagnosticSeverity.Warning, "unknown-color-token",
                    $"Unknown color/gradient token '{name}' on {where}.", node);
        }

        static void CheckShadowRef(string value, DesignDocument doc, DesignNode node, List<DesignDiagnostic> diags)
        {
            string name = TokenName(value);
            if (name != null && !doc.Tokens.Shadows.ContainsKey(name))
                Add(diags, DiagnosticSeverity.Warning, "unknown-shadow-token",
                    $"Unknown shadow token '{name}'.", node);
        }

        static void CheckDim(Dim d, Dictionary<string, double> table, string category, DesignNode node, string where, List<DesignDiagnostic> diags)
        {
            if (d.HasToken && !table.ContainsKey(d.TokenName))
                Add(diags, DiagnosticSeverity.Warning, "unknown-" + category + "-token",
                    $"Unknown {category} token '{d.TokenName}' on {where}.", node);
        }

        static string TokenName(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.Length > 2 && value[0] == '{' && value[value.Length - 1] == '}')
                return value.Substring(1, value.Length - 2);
            return null;
        }

        static void Add(List<DesignDiagnostic> diags, DiagnosticSeverity sev, string code, string msg, DesignNode node)
            => diags.Add(new DesignDiagnostic { Severity = sev, Code = code, Message = msg, Node = node });
    }
}
