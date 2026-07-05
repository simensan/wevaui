using System.Collections.Generic;
using System.Text;
using Weva.Figma.Model;

namespace Weva.Figma.Mapping
{
    public sealed class ExportOptions
    {
        public string Indent = "  ";
        /// <summary>Optional prefix on every generated class (e.g. "menu" → ".menu-card").</summary>
        public string ClassPrefix = "";
        /// <summary>Prepend <c>* { box-sizing: border-box; }</c> so widths match Figma's border-box geometry.</summary>
        public bool IncludeReset = true;
        /// <summary>Developer-owned dynamic layer re-applied by <c>data-figma-id</c> (round-trip safe).</summary>
        public RoundTrip.FigmaOverlay Overlay;
    }

    /// <summary>
    /// Walks a parsed Figma node tree and emits a Weva-conformant HTML fragment
    /// plus a CSS stylesheet (one class per node), recording the assets that must
    /// be rasterized separately.
    ///
    /// Layer-name <see cref="NameAnnotations"/> turn the static design into dynamic
    /// markup: <c>{{ bindings }}</c>, <c>@event</c> hooks, <c>.class?toggles</c>,
    /// <c>#ids</c>, <c>&lt;tag&gt;</c> overrides, and <c>*each</c> repeat templates.
    ///
    /// Output is deterministic: depth-first document order, class names from
    /// sanitized clean layer names with stable numeric de-duplication, and a
    /// <c>data-figma-id</c> on every element for round-trip identity.
    /// </summary>
    public static class FigmaDocumentExporter
    {
        public static ExportedDocument Export(FigmaNode root, ExportOptions options = null)
        {
            options = options ?? new ExportOptions();
            var doc = new ExportedDocument();
            var classes = new Dictionary<FigmaNode, string>();
            var annos = new Dictionary<FigmaNode, NameAnnotations>();
            var used = new HashSet<string>();
            var order = new List<FigmaNode>();
            var imageRefs = new HashSet<string>();

            Collect(root, options, classes, annos, used, order, doc, imageRefs);

            var html = new StringBuilder();
            BuildHtml(root, classes, annos, options, html, 0);
            doc.Html = html.ToString();

            var css = new StringBuilder();
            if (options.IncludeReset)
                css.Append("* {\n").Append(options.Indent).Append("box-sizing: border-box;\n}\n\n");
            foreach (FigmaNode n in order)
            {
                var block = new CssBlock();
                LayoutMapper.Apply(n, block);
                StyleMapper.Apply(n, block);
                if (n.IsVector)
                {
                    block.Set("background-image", $"url(\"{RasterNaming.VectorFile(n)}\")");
                    block.Set("background-size", "contain");
                    block.Set("background-repeat", "no-repeat");
                    block.Set("background-position", "center");
                }
                if (block.IsEmpty) continue;
                css.Append('.').Append(classes[n]).Append(" {\n");
                css.Append(block.Render(options.Indent));
                css.Append("}\n\n");
            }
            doc.Css = css.ToString();
            return doc;
        }

        static void Collect(FigmaNode node, ExportOptions options, Dictionary<FigmaNode, string> classes,
            Dictionary<FigmaNode, NameAnnotations> annos, HashSet<string> used, List<FigmaNode> order,
            ExportedDocument doc, HashSet<string> imageRefs)
        {
            if (!node.Visible) return;
            NameAnnotations ann = NameAnnotations.Parse(node.Name);
            ApplyOverlay(ann, options.Overlay, node.Id);
            annos[node] = ann;
            order.Add(node);
            classes[node] = MakeClass(node, ann, options, used);

            if (node.IsVector)
            {
                doc.RasterRequests.Add(new RasterRequest
                {
                    Kind = RasterKind.Vector,
                    NodeId = node.Id,
                    FileName = RasterNaming.VectorFile(node),
                });
                return; // a rasterized shape absorbs its own children
            }

            FigmaPaint fill = FigmaNode.FirstVisible(node.Fills);
            if (fill != null && fill.IsImage && fill.ImageRef != null && imageRefs.Add(fill.ImageRef))
                doc.RasterRequests.Add(new RasterRequest
                {
                    Kind = RasterKind.ImageFill,
                    ImageRef = fill.ImageRef,
                    FileName = RasterNaming.ImageFile(fill.ImageRef),
                });

            if (!node.HasChildren) return;

            if (ann.Each != null)
            {
                // Only the first visible child is the repeat prototype; the rest are
                // design-time duplicates and are dropped.
                FigmaNode proto = FirstVisibleChild(node);
                if (proto != null) Collect(proto, options, classes, annos, used, order, doc, imageRefs);
            }
            else
            {
                foreach (FigmaNode c in node.Children)
                    Collect(c, options, classes, annos, used, order, doc, imageRefs);
            }
        }

        static void BuildHtml(FigmaNode node, Dictionary<FigmaNode, string> classes,
            Dictionary<FigmaNode, NameAnnotations> annos, ExportOptions options, StringBuilder sb, int depth)
        {
            if (!node.Visible) return;
            NameAnnotations ann = annos[node];
            string indent = Indent(options.Indent, depth);
            string tag = ann.Tag ?? (node.IsText ? "span" : "div");
            string attrs = BuildAttrs(node, ann, classes);

            if (node.IsText)
            {
                string text = ann.Binding ?? HtmlWriter.EscapeText(node.Characters ?? "").Replace("\n", "<br>");
                sb.Append(indent).Append('<').Append(tag).Append(attrs).Append('>')
                  .Append(text).Append("</").Append(tag).Append(">\n");
            }
            else if (node.IsVector || !node.HasChildren)
            {
                sb.Append(indent).Append('<').Append(tag).Append(attrs).Append("></").Append(tag).Append(">\n");
            }
            else if (ann.Each != null)
            {
                FigmaNode proto = FirstVisibleChild(node);
                sb.Append(indent).Append('<').Append(tag).Append(attrs).Append(">\n");
                string childIndent = Indent(options.Indent, depth + 1);
                string each = $"data-each=\"{HtmlWriter.EscapeAttr(ann.Each.Collection)} as {HtmlWriter.EscapeAttr(ann.Each.Item)}\"";
                if (ann.Each.Key != null) each += $" data-key=\"{HtmlWriter.EscapeAttr(ann.Each.Key)}\"";
                sb.Append(childIndent).Append("<template ").Append(each).Append(">\n");
                if (proto != null) BuildHtml(proto, classes, annos, options, sb, depth + 2);
                sb.Append(childIndent).Append("</template>\n");
                sb.Append(indent).Append("</").Append(tag).Append(">\n");
            }
            else
            {
                sb.Append(indent).Append('<').Append(tag).Append(attrs).Append(">\n");
                foreach (FigmaNode c in node.Children)
                    BuildHtml(c, classes, annos, options, sb, depth + 1);
                sb.Append(indent).Append("</").Append(tag).Append(">\n");
            }
        }

        static void ApplyOverlay(NameAnnotations ann, RoundTrip.FigmaOverlay overlay, string figmaId)
        {
            if (overlay == null || figmaId == null) return;
            if (!overlay.TryGet(figmaId, out RoundTrip.NodeOverride o)) return;
            if (o.Tag != null) ann.Tag = o.Tag;
            if (o.Id != null) ann.Id = o.Id;
            if (o.Text != null) ann.Binding = o.Text; // text-content override (used on text nodes)
            foreach (var a in o.Attributes)
                ann.ExtraAttributes.Add(a);
        }

        static string BuildAttrs(FigmaNode node, NameAnnotations ann, Dictionary<FigmaNode, string> classes)
        {
            // Ordered with de-dup: a later Set on the same name replaces in place, so
            // overlay attributes can override annotation-derived ones without emitting
            // duplicate attributes. class + data-figma-id are fixed and not overridable.
            var attrs = new List<KeyValuePair<string, string>>();
            attrs.Add(new KeyValuePair<string, string>("class", classes[node]));
            if (ann.Id != null) SetAttr(attrs, "id", ann.Id);
            attrs.Add(new KeyValuePair<string, string>("data-figma-id", node.Id ?? ""));
            foreach (var ev in ann.Events) SetAttr(attrs, "on-" + ev.Key, ev.Value);
            foreach (var ct in ann.ClassToggles) SetAttr(attrs, "data-class-" + ct.Key, ct.Value);
            foreach (var ea in ann.ExtraAttributes)
            {
                if (ea.Key == "class" || ea.Key == "data-figma-id") continue;
                SetAttr(attrs, ea.Key, ea.Value);
            }

            var sb = new StringBuilder();
            foreach (var a in attrs)
                sb.Append(' ').Append(a.Key).Append("=\"").Append(HtmlWriter.EscapeAttr(a.Value)).Append('"');
            return sb.ToString();
        }

        static void SetAttr(List<KeyValuePair<string, string>> attrs, string name, string value)
        {
            for (int i = 0; i < attrs.Count; i++)
                if (attrs[i].Key == name) { attrs[i] = new KeyValuePair<string, string>(name, value); return; }
            attrs.Add(new KeyValuePair<string, string>(name, value));
        }

        static FigmaNode FirstVisibleChild(FigmaNode node)
        {
            if (node.Children == null) return null;
            foreach (FigmaNode c in node.Children)
                if (c.Visible) return c;
            return null;
        }

        static string MakeClass(FigmaNode node, NameAnnotations ann, ExportOptions options, HashSet<string> used)
        {
            string baseName = CssText.SanitizeIdent(ann.CleanName);
            if (baseName.Length == 0) baseName = CssText.SanitizeIdent(ann.Binding);
            if (baseName.Length == 0) baseName = CssText.SanitizeIdent(node.Type);
            if (baseName.Length == 0) baseName = "node";

            string candidate = baseName;
            int i = 2;
            while (used.Contains(candidate))
                candidate = baseName + "-" + (i++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            used.Add(candidate);

            string prefix = CssText.SanitizeIdent(options.ClassPrefix);
            return prefix.Length > 0 ? prefix + "-" + candidate : candidate;
        }

        static string Indent(string unit, int depth)
        {
            if (depth <= 0) return "";
            var sb = new StringBuilder(unit.Length * depth);
            for (int i = 0; i < depth; i++) sb.Append(unit);
            return sb.ToString();
        }
    }
}
