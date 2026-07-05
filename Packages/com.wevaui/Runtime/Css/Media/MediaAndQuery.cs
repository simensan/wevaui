using System.Collections.Generic;

namespace Weva.Css.Media {
    public sealed class MediaAndQuery : MediaQuery {
        public IReadOnlyList<MediaQuery> Children { get; }

        public MediaAndQuery(IReadOnlyList<MediaQuery> children) {
            Children = children ?? new List<MediaQuery>();
        }

        public override bool Evaluate(MediaContext ctx) {
            foreach (var c in Children) {
                if (!c.Evaluate(ctx)) return false;
            }
            return true;
        }
    }
}
