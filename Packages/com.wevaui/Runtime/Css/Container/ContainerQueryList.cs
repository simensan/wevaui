using System.Collections.Generic;

namespace Weva.Css.Container {
    public sealed class ContainerQueryList {
        public IReadOnlyList<ContainerQuery> Items { get; }

        public ContainerQueryList(IReadOnlyList<ContainerQuery> items) {
            Items = items ?? new List<ContainerQuery>();
        }

        public bool Evaluate(ContainerContext ctx) {
            if (Items.Count == 0) return true;
            foreach (var q in Items) {
                if (q.Evaluate(ctx)) return true;
            }
            return false;
        }
    }
}
