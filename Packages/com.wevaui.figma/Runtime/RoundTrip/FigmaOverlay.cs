using System.Collections.Generic;
using System.Text;
using Weva.Figma.Json;

namespace Weva.Figma.RoundTrip
{
    /// <summary>A per-node override applied on top of the design export, keyed by <c>data-figma-id</c>.</summary>
    public sealed class NodeOverride
    {
        public string Tag;    // override the element tag
        public string Id;     // set/override the id attribute
        public string Text;   // override text content (e.g. a "{{ binding }}")
        public readonly List<KeyValuePair<string, string>> Attributes = new List<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// The developer-owned dynamic layer that survives re-export. The design
    /// (HTML structure + CSS) regenerates from Figma every time; this overlay —
    /// keyed on the stable <c>data-figma-id</c> stamped on each element — is
    /// re-applied so hand-added bindings, event hooks, ids, tags, and attributes
    /// are never clobbered when the design changes.
    ///
    /// Persisted as a small JSON sidecar (e.g. <c>menu.overlay.json</c>):
    /// <code>
    /// {
    ///   "1:23": { "tag": "button", "id": "play", "text": "{{ Label }}",
    ///             "attributes": { "on-click": "OnPlay", "aria-label": "Play" } }
    /// }
    /// </code>
    /// </summary>
    public sealed class FigmaOverlay
    {
        public readonly Dictionary<string, NodeOverride> ByFigmaId = new Dictionary<string, NodeOverride>();

        public bool IsEmpty => ByFigmaId.Count == 0;

        public bool TryGet(string figmaId, out NodeOverride o) => ByFigmaId.TryGetValue(figmaId, out o);

        public static FigmaOverlay Parse(string json)
            => Parse(JsonParser.Parse(json));

        public static FigmaOverlay Parse(JsonValue root)
        {
            var overlay = new FigmaOverlay();
            foreach (var kv in root.Members)
            {
                JsonValue v = kv.Value;
                var o = new NodeOverride
                {
                    Tag = v["tag"].AsString(null),
                    Id = v["id"].AsString(null),
                    Text = v["text"].AsString(null),
                };
                foreach (var a in v["attributes"].Members)
                    o.Attributes.Add(new KeyValuePair<string, string>(a.Key, a.Value.AsString("")));
                overlay.ByFigmaId[kv.Key] = o;
            }
            return overlay;
        }

        /// <summary>Deterministic JSON (ids and attribute names sorted) for stable sidecars.</summary>
        public string ToJson()
        {
            var ids = new List<string>(ByFigmaId.Keys);
            ids.Sort(System.StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("{\n");
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                NodeOverride o = ByFigmaId[id];
                sb.Append("  ").Append(Quote(id)).Append(": {");

                var parts = new List<string>();
                if (o.Tag != null) parts.Add(Quote("tag") + ": " + Quote(o.Tag));
                if (o.Id != null) parts.Add(Quote("id") + ": " + Quote(o.Id));
                if (o.Text != null) parts.Add(Quote("text") + ": " + Quote(o.Text));
                if (o.Attributes.Count > 0)
                {
                    var attrs = new List<KeyValuePair<string, string>>(o.Attributes);
                    attrs.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
                    var ab = new StringBuilder("{");
                    for (int j = 0; j < attrs.Count; j++)
                    {
                        if (j > 0) ab.Append(", ");
                        ab.Append(Quote(attrs[j].Key)).Append(": ").Append(Quote(attrs[j].Value));
                    }
                    ab.Append("}");
                    parts.Add(Quote("attributes") + ": " + ab);
                }

                sb.Append(' ').Append(string.Join(", ", parts)).Append(" }");
                sb.Append(i < ids.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
