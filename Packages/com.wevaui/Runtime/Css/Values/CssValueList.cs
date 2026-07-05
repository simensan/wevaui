using System.Collections.Generic;
using System.Text;

namespace Weva.Css.Values {
    public enum CssValueListSeparator {
        Space,
        Comma
    }

    public sealed class CssValueList : CssValue {
        public IReadOnlyList<CssValue> Items { get; }
        public CssValueListSeparator Separator { get; }

        public override CssValueKind Kind => CssValueKind.List;

        public CssValueList(IReadOnlyList<CssValue> items, CssValueListSeparator separator) {
            Items = items;
            Separator = separator;
            Raw = BuildRaw(items, separator);
        }

        // Lazy-raw variant. When `raw` is null, the printed form is built
        // on demand via ToString — important for animation overlays whose
        // inner items mutate every Tick: pre-building Raw at construction
        // (then never updating it) would yield a stale string to consumers
        // that fall back to Raw. Passing the original animation property
        // name is fine too if the caller wants Get(propId) to return that
        // string.
        public CssValueList(IReadOnlyList<CssValue> items, CssValueListSeparator separator, string raw) {
            Items = items;
            Separator = separator;
            Raw = raw;
        }

        public override string ToString() {
            return Raw ?? BuildRaw(Items, Separator);
        }

        static string BuildRaw(IReadOnlyList<CssValue> items, CssValueListSeparator sep) {
            var sb = new StringBuilder();
            string joiner = sep == CssValueListSeparator.Comma ? ", " : " ";
            for (int i = 0; i < items.Count; i++) {
                if (i > 0) sb.Append(joiner);
                sb.Append(items[i].Raw ?? items[i].ToString());
            }
            return sb.ToString();
        }
    }
}
