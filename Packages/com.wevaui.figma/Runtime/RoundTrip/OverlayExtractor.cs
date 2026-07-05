using System.Collections.Generic;
using System.Text;

namespace Weva.Figma.RoundTrip
{
    /// <summary>
    /// Builds a <see cref="FigmaOverlay"/> from previously-generated (and possibly
    /// hand-edited) HTML by capturing the *dynamic* layer per <c>data-figma-id</c>:
    /// semantic tag overrides, ids, event/binding/aria/form attributes, and
    /// <c>{{ binding }}</c> text. Design-owned bits (class, geometry) are ignored.
    ///
    /// Scoped to the exporter's own well-formed output (double-quoted attributes,
    /// simple nesting) — not a general HTML parser.
    /// </summary>
    public static class OverlayExtractor
    {
        static readonly HashSet<string> FormAttrs = new HashSet<string>
        {
            "placeholder", "href", "alt", "title", "name", "value", "type",
            "disabled", "checked", "for", "role", "tabindex", "rel", "src",
        };

        public static FigmaOverlay Extract(string html)
        {
            var overlay = new FigmaOverlay();
            if (string.IsNullOrEmpty(html)) return overlay;

            int i = 0;
            int len = html.Length;
            while ((i = html.IndexOf('<', i)) >= 0)
            {
                if (i + 1 >= len) break;
                char next = html[i + 1];
                if (next == '/' || next == '!') { i += 2; continue; }

                int tagEnd = html.IndexOf('>', i + 1);
                if (tagEnd < 0) break;

                string inner = html.Substring(i + 1, tagEnd - (i + 1));
                string tagName;
                var attrs = ParseTag(inner, out tagName);

                string text = null;
                int nextLt = html.IndexOf('<', tagEnd + 1);
                if (nextLt > tagEnd + 1)
                {
                    string between = html.Substring(tagEnd + 1, nextLt - (tagEnd + 1)).Trim();
                    if (between.Length > 0) text = Unescape(between);
                }

                Process(tagName, attrs, text, overlay);
                i = tagEnd + 1;
            }
            return overlay;
        }

        static void Process(string tag, List<KeyValuePair<string, string>> attrs, string text, FigmaOverlay overlay)
        {
            string figmaId = Get(attrs, "data-figma-id");
            if (figmaId == null) return;

            var o = new NodeOverride();
            bool any = false;

            if (tag != "div" && tag != "span" && tag != "template") { o.Tag = tag; any = true; }

            string id = Get(attrs, "id");
            if (id != null) { o.Id = id; any = true; }

            foreach (var a in attrs)
            {
                if (a.Key == "class" || a.Key == "data-figma-id" || a.Key == "id") continue;
                if (IsDynamic(a.Key)) { o.Attributes.Add(a); any = true; }
            }

            if (text != null && text.IndexOf("{{", System.StringComparison.Ordinal) >= 0)
            {
                o.Text = text;
                any = true;
            }

            if (any) overlay.ByFigmaId[figmaId] = o;
        }

        static bool IsDynamic(string name)
            => name.StartsWith("on-", System.StringComparison.Ordinal)
               || name.StartsWith("data-class-", System.StringComparison.Ordinal)
               || name.StartsWith("data-bind", System.StringComparison.Ordinal)
               || name.StartsWith("aria-", System.StringComparison.Ordinal)
               || FormAttrs.Contains(name);

        static List<KeyValuePair<string, string>> ParseTag(string inner, out string tagName)
        {
            var attrs = new List<KeyValuePair<string, string>>();
            int i = 0, len = inner.Length;
            while (i < len && IsNameChar(inner[i])) i++;
            tagName = inner.Substring(0, i);

            while (i < len)
            {
                while (i < len && IsSpace(inner[i])) i++;
                int nameStart = i;
                while (i < len && IsNameChar(inner[i])) i++;
                if (i == nameStart) { i++; continue; } // skip stray chars like '/'
                string name = inner.Substring(nameStart, i - nameStart);

                while (i < len && IsSpace(inner[i])) i++;
                string value = "";
                if (i < len && inner[i] == '=')
                {
                    i++;
                    while (i < len && IsSpace(inner[i])) i++;
                    if (i < len && inner[i] == '"')
                    {
                        i++;
                        int vs = i;
                        while (i < len && inner[i] != '"') i++;
                        value = inner.Substring(vs, i - vs);
                        if (i < len) i++; // closing quote
                    }
                    else
                    {
                        int vs = i;
                        while (i < len && !IsSpace(inner[i])) i++;
                        value = inner.Substring(vs, i - vs);
                    }
                }
                attrs.Add(new KeyValuePair<string, string>(name, Unescape(value)));
            }
            return attrs;
        }

        static string Get(List<KeyValuePair<string, string>> attrs, string name)
        {
            foreach (var a in attrs) if (a.Key == name) return a.Value;
            return null;
        }

        static bool IsNameChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
               || c == '-' || c == '_' || c == ':';

        static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

        static string Unescape(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            return s.Replace("&quot;", "\"").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }
    }
}
