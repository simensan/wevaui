using System.Collections.Generic;

namespace Weva.Parsing {
    public static class HtmlEntities {
        static readonly Dictionary<string, string> map = new() {
            { "amp", "&" }, { "lt", "<" }, { "gt", ">" }, { "shy", "\u00AD" },
            { "quot", "\"" }, { "apos", "'" }, { "nbsp", " " },
            { "copy", "©" }, { "reg", "®" }, { "trade", "™" },
            { "hellip", "…" }, { "mdash", "—" }, { "ndash", "–" },
            { "lsquo", "‘" }, { "rsquo", "’" },
            { "ldquo", "“" }, { "rdquo", "”" },
            { "bull", "•" }, { "middot", "·" }, { "deg", "°" },
            { "times", "×" }, { "divide", "÷" },
            { "laquo", "«" }, { "raquo", "»" }
        };

        public static bool Lookup(string name, out string value) {
            return map.TryGetValue(name, out value);
        }
    }
}
