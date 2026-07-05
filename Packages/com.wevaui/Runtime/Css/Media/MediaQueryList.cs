using System.Collections.Generic;

namespace Weva.Css.Media {
    public sealed class MediaQueryList {
        public IReadOnlyList<MediaQuery> Items { get; }

        public MediaQueryList(IReadOnlyList<MediaQuery> items) {
            Items = items ?? new List<MediaQuery>();
        }

        public bool Evaluate(MediaContext ctx) {
            if (Items.Count == 0) return true;
            foreach (var q in Items) {
                if (q.Evaluate(ctx)) return true;
            }
            return false;
        }
    }
}
