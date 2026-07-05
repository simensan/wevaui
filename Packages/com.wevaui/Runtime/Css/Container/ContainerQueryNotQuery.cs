namespace Weva.Css.Container {
    public sealed class ContainerQueryNotQuery : ContainerQuery {
        public ContainerQuery Child { get; }

        public ContainerQueryNotQuery(ContainerQuery child) {
            Child = child;
        }

        public override bool Evaluate(ContainerContext ctx) {
            if (Child == null) return false;
            return !Child.Evaluate(ctx);
        }
    }
}
