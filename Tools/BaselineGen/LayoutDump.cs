using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.BaselineGen {
    static class LayoutDump {
        public static int Run(string[] args) {
            if (args.Length < 4) {
                Console.Error.WriteLine("Usage: layout-dump <htmlPath> <width> <height> [outPath] [cssPath]");
                return 2;
            }

            string htmlPath = Path.GetFullPath(args[1]);
            if (!File.Exists(htmlPath)) {
                Console.Error.WriteLine("HTML not found: " + htmlPath);
                return 2;
            }

            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) || width <= 0 ||
                !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) || height <= 0) {
                Console.Error.WriteLine("Viewport must be positive integer width/height.");
                return 2;
            }

            string outPath = args.Length > 4
                ? Path.GetFullPath(args[4])
                : Path.Combine(Path.GetDirectoryName(htmlPath) ?? ".", Path.GetFileNameWithoutExtension(htmlPath) + ".unity-layout.json");

            string html = File.ReadAllText(htmlPath);
            string cssPath = args.Length > 5
                ? Path.GetFullPath(args[5])
                : Path.ChangeExtension(htmlPath, ".css");
            string css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : string.Empty;

            var boxes = BuildUnityBoxes(html, css, width, height);
            WriteJson(outPath, Path.GetFileName(htmlPath), width, height, boxes);
            Console.WriteLine($"Wrote {boxes.Count} elements -> {outPath}");
            return 0;
        }

        readonly struct ElementRect {
            public readonly int Depth;
            public readonly string Tag;
            public readonly string Id;
            public readonly string ClassName;
            public readonly double X;
            public readonly double Y;
            public readonly double Width;
            public readonly double Height;

            public ElementRect(int depth, string tag, string id, string className,
                               double x, double y, double width, double height) {
                Depth = depth;
                Tag = tag ?? string.Empty;
                Id = id ?? string.Empty;
                ClassName = className ?? string.Empty;
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        static List<ElementRect> BuildUnityBoxes(string html, string css, int width, int height) {
            var doc = HtmlParser.Parse(html ?? string.Empty, new ParseOptions { ThrowOnError = false });
            var sheets = new List<OriginatedStylesheet> { UserAgentStylesheet.Parse() };
            if (!string.IsNullOrEmpty(css)) {
                var authorSheet = CssParser.Parse(css, new ParseOptions { ThrowOnError = false });
                sheets.Add(OriginatedStylesheet.Author(authorSheet));
            }

            var media = MediaContext.Default(width, height);
            var cascade = new CascadeEngine(sheets, media);
            var styles = cascade.ComputeAll(doc);
            var fontMetrics = MonoFontMetrics.ChromeSansSerif();
            var ctx = new LayoutContext(fontMetrics) {
                ViewportWidthPx = width,
                ViewportHeightPx = height,
                Snapshot = cascade.LastSnapshot,
            };
            ctx.RegisterFont("monospace", MonoFontMetrics.ChromeMonospace());

            var layout = new LayoutEngine(fontMetrics);
            layout.BackdropStyleOf = e => cascade.ComputeBackdrop(e);
            var root = layout.Layout(doc, e => styles.TryGetValue(e, out var s) ? s : null, ctx);

            var order = new List<ElementRect>();
            var seenElements = new HashSet<Element>();
            Walk(root, 0, 0, 0, order, seenElements);
            return order;
        }

        static void Walk(Box box, double parentAbsX, double parentAbsY, int depth,
                         List<ElementRect> order, HashSet<Element> seenElements) {
            if (box == null) return;

            double absX = parentAbsX + box.X;
            double absY = parentAbsY + box.Y;
            var translation = ResolveTransformTranslation(box);
            double visualX = absX + translation.X;
            double visualY = absY + translation.Y;

            bool isWrapperTag = box.Element != null
                                && (box.Element.TagName == "html" || box.Element.TagName == "body");
            bool isPrincipal = box.Element != null
                               && !isWrapperTag
                               && box is not LineBox
                               && box is not AnonymousBlockBox
                               && box is not AnonymousInlineBox
                               && seenElements.Add(box.Element);
            if (isPrincipal) {
                order.Add(new ElementRect(
                    depth,
                    (box.Element.TagName ?? string.Empty).ToLowerInvariant(),
                    box.Element.Id,
                    box.Element.ClassName,
                    Round2(visualX),
                    Round2(visualY),
                    Round2(box.Width),
                    Round2(box.Height)));
            }

            foreach (var child in box.Children) {
                Walk(child, visualX, visualY, isWrapperTag ? depth : depth + 1, order, seenElements);
            }
        }

        static (double X, double Y) ResolveTransformTranslation(Box box) {
            string raw = box?.Style?.Get(CssProperties.TransformId);
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "none") return (0, 0);

            double x = 0;
            double y = 0;
            int cursor = 0;
            while (cursor < raw.Length) {
                int open = raw.IndexOf('(', cursor);
                if (open < 0) break;
                int close = FindFunctionClose(raw, open);
                if (close < 0) break;

                string name = raw.Substring(cursor, open - cursor).Trim().ToLowerInvariant();
                string args = raw.Substring(open + 1, close - open - 1);
                if (name.EndsWith("translatex", StringComparison.Ordinal)) {
                    x += ResolveLengthPercent(FirstArg(args), box.Width);
                } else if (name.EndsWith("translatey", StringComparison.Ordinal)) {
                    y += ResolveLengthPercent(FirstArg(args), box.Height);
                } else if (name.EndsWith("translate", StringComparison.Ordinal)) {
                    SplitFirstTwoArgs(args, out var a, out var b);
                    x += ResolveLengthPercent(a, box.Width);
                    y += ResolveLengthPercent(b, box.Height);
                }
                cursor = close + 1;
            }
            return (x, y);
        }

        static int FindFunctionClose(string raw, int open) {
            int depth = 0;
            for (int i = open; i < raw.Length; i++) {
                char c = raw[i];
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static string FirstArg(string args) {
            SplitFirstTwoArgs(args, out var first, out _);
            return first;
        }

        static void SplitFirstTwoArgs(string args, out string first, out string second) {
            first = args ?? string.Empty;
            second = string.Empty;
            if (string.IsNullOrEmpty(args)) return;

            int depth = 0;
            for (int i = 0; i < args.Length; i++) {
                char c = args[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if ((c == ',' || char.IsWhiteSpace(c)) && depth == 0) {
                    first = args.Substring(0, i).Trim();
                    int j = i + 1;
                    while (j < args.Length && (args[j] == ',' || char.IsWhiteSpace(args[j]))) j++;
                    second = j < args.Length ? args.Substring(j).Trim() : string.Empty;
                    return;
                }
            }
            first = args.Trim();
        }

        static double ResolveLengthPercent(string token, double basis) {
            if (string.IsNullOrWhiteSpace(token)) return 0;
            string s = token.Trim();
            if (s.EndsWith("%", StringComparison.Ordinal)) {
                if (double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) {
                    return basis * pct * 0.01;
                }
                return 0;
            }
            if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 2);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) ? px : 0;
        }

        static void WriteJson(string outPath, string source, int width, int height, List<ElementRect> boxes) {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"source\": \"").Append(JsonEscape(source)).Append("\",\n");
            sb.Append("  \"width\": ").Append(width).Append(",\n");
            sb.Append("  \"height\": ").Append(height).Append(",\n");
            sb.Append("  \"count\": ").Append(boxes.Count).Append(",\n");
            sb.Append("  \"elements\": [");
            for (int i = 0; i < boxes.Count; i++) {
                var b = boxes[i];
                if (i > 0) sb.Append(",");
                sb.Append("\n    {");
                sb.Append("\"i\":").Append(i).Append(",");
                sb.Append("\"depth\":").Append(b.Depth).Append(",");
                sb.Append("\"tag\":\"").Append(JsonEscape(b.Tag)).Append("\",");
                sb.Append("\"id\":\"").Append(JsonEscape(b.Id)).Append("\",");
                sb.Append("\"cls\":\"").Append(JsonEscape(b.ClassName)).Append("\",");
                sb.Append("\"x\":").Append(Format(b.X)).Append(",");
                sb.Append("\"y\":").Append(Format(b.Y)).Append(",");
                sb.Append("\"w\":").Append(Format(b.Width)).Append(",");
                sb.Append("\"h\":").Append(Format(b.Height));
                sb.Append("}");
            }
            sb.Append("\n  ]\n");
            sb.Append("}\n");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllText(outPath, sb.ToString());
        }

        static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        static string JsonEscape(string value) {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
