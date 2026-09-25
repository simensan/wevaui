// A layout dump from the Unity host, in the JSON weva_dump writes (see
// Tools/weva_dump/main.cpp and docs/ORACLE.md), so the three-way
// oracle (C# reference, core, Chrome) can run with the core hosted by Unity.
// It is the same core, so any difference from weva_dump is this adapter's:
// the walk here is over the elements the C ABI exposes, not the box tree.
//
// Rules mirrored from weva_dump: html and body are skipped as wrappers and
// do not consume a depth level; anonymous and line boxes never appear (they
// are not elements); an element without a box (display:none) is skipped;
// the rect is the element's border box in document coordinates; numbers
// print at four decimals, rounded half away from zero, trailing zeros cut.
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Native
{
    internal static class NativeLayoutDump
    {
        public struct Entry
        {
            public int Depth;
            public string Tag, Id, Class;
            public double X, Y, W, H;
        }

        /// <summary>Every element with a box, in document order, with its depth and border box.</summary>
        public static List<Entry> Collect(NativeDocument doc)
        {
            var entries = new List<Entry>();
            uint[] all = doc.QueryAll("*");
            // Ancestors of the element being visited, innermost last, kept by
            // containment: an element that does not contain the next one is
            // closed. Wrappers stay on the stack but do not count.
            var stack = new List<uint>();
            var wrapper = new List<bool>();
            foreach (uint e in all)
            {
                while (stack.Count > 0 && !doc.ElementContains(stack[stack.Count - 1], e))
                {
                    stack.RemoveAt(stack.Count - 1);
                    wrapper.RemoveAt(wrapper.Count - 1);
                }
                string tag = doc.TagName(e);
                bool isWrapper = tag == "html" || tag == "body";
                int depth = 0;
                for (int i = 0; i < wrapper.Count; i++) if (!wrapper[i]) depth++;
                if (!isWrapper && doc.TryGetBounds(e, out NativeBounds b))
                {
                    entries.Add(new Entry
                    {
                        Depth = depth + 1,
                        Tag = tag,
                        Id = doc.ElementAttribute(e, "id"),
                        Class = doc.ElementAttribute(e, "class"),
                        X = b.X, Y = b.Y, W = b.Width, H = b.Height,
                    });
                }
                stack.Add(e);
                wrapper.Add(isWrapper);
            }
            return entries;
        }

        /// <summary>The dump as weva_dump's JSON.</summary>
        public static string ToJson(string source, int width, int height, List<Entry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"source\": \"").Append(Escape(source)).Append("\",\n");
            sb.Append("  \"width\": ").Append(width).Append(",\n");
            sb.Append("  \"height\": ").Append(height).Append(",\n");
            sb.Append("  \"count\": ").Append(entries.Count).Append(",\n");
            sb.Append("  \"elements\": [");
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (i > 0) sb.Append(",");
                sb.Append("\n    {");
                sb.Append("\"i\":").Append(i).Append(",");
                sb.Append("\"depth\":").Append(e.Depth).Append(",");
                sb.Append("\"tag\":\"").Append(Escape(e.Tag)).Append("\",");
                sb.Append("\"id\":\"").Append(Escape(e.Id)).Append("\",");
                sb.Append("\"cls\":\"").Append(Escape(e.Class)).Append("\",");
                sb.Append("\"path\":\"\",");
                sb.Append("\"x\":").Append(Num(e.X)).Append(",");
                sb.Append("\"y\":").Append(Num(e.Y)).Append(",");
                sb.Append("\"w\":").Append(Num(e.W)).Append(",");
                sb.Append("\"h\":").Append(Num(e.H));
                sb.Append("}");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>
        /// The dump the core produces from its own box tree (weva_document_layout_dump):
        /// weva_dump's walk and JSON by construction, including box-tree depth,
        /// box order and transform translation, which the element view above
        /// cannot reproduce. width and height are the document's viewport.
        /// </summary>
        public static string Dump(NativeDocument doc, string source, int width, int height)
        {
            string json = doc.LayoutDump(source);
            return json.Length > 0 ? json : ToJson(source, width, height, Collect(doc));
        }

        // weva_dump's format_num: round half away from zero at four decimals,
        // print with trailing zeros removed, and never "-0".
        public static string Num(double v)
        {
            double scaled = v * 10000.0;
            double rounded = (scaled < 0.0 ? -System.Math.Floor(-scaled + 0.5) : System.Math.Floor(scaled + 0.5)) / 10000.0;
            string s = rounded.ToString("F4", CultureInfo.InvariantCulture);
            if (s.IndexOf('.') >= 0)
            {
                s = s.TrimEnd('0');
                if (s.EndsWith(".")) s = s.Substring(0, s.Length - 1);
            }
            if (s == "-0") s = "0";
            return s;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
