using System.Collections.Generic;
using System.Text;

namespace Weva.Figma.Mapping
{
    /// <summary>An ordered list of CSS declarations for one rule. Insertion order is preserved so output is deterministic.</summary>
    public sealed class CssBlock
    {
        readonly List<KeyValuePair<string, string>> _decls = new List<KeyValuePair<string, string>>();

        public bool IsEmpty => _decls.Count == 0;
        public int Count => _decls.Count;
        public IReadOnlyList<KeyValuePair<string, string>> Declarations => _decls;

        /// <summary>Append a declaration. No-op if <paramref name="value"/> is null/empty.</summary>
        public void Set(string property, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            _decls.Add(new KeyValuePair<string, string>(property, value));
        }

        public bool Has(string property)
        {
            foreach (var d in _decls) if (d.Key == property) return true;
            return false;
        }

        public string TryGet(string property)
        {
            foreach (var d in _decls) if (d.Key == property) return d.Value;
            return null;
        }

        public string Render(string indent)
        {
            var sb = new StringBuilder();
            foreach (var d in _decls)
                sb.Append(indent).Append(d.Key).Append(": ").Append(d.Value).Append(";\n");
            return sb.ToString();
        }
    }
}
