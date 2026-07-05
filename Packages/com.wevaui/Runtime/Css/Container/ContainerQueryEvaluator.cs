namespace Weva.Css.Container {
    public static class ContainerQueryEvaluator {
        public static bool Evaluate(ContainerQueryList queries, ContainerContext ctx) {
            if (queries == null) return true;
            return queries.Evaluate(ctx);
        }

        public static bool Evaluate(ContainerQuery query, ContainerContext ctx) {
            if (query == null) return true;
            return query.Evaluate(ctx);
        }
    }
}
