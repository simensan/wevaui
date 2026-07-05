using System.Collections.Generic;

namespace Weva.Css.Container {
    public sealed class ContainerQueryAndQuery : ContainerQuery {
        public IReadOnlyList<ContainerQuery> Children { get; }

        public ContainerQueryAndQuery(IReadOnlyList<ContainerQuery> children) {
            Children = children ?? new List<ContainerQuery>();
        }

        public override bool Evaluate(ContainerContext ctx) {
            foreach (var c in Children) {
                if (!c.Evaluate(ctx)) return false;
            }
            return true;
        }
    }
}
