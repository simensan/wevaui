namespace Weva.Css.Media {
    public static class MediaQueryEvaluator {
        public static bool Evaluate(MediaQueryList queries, MediaContext ctx) {
            if (queries == null) return true;
            return queries.Evaluate(ctx);
        }

        public static bool Evaluate(MediaQuery query, MediaContext ctx) {
            if (query == null) return true;
            return query.Evaluate(ctx);
        }
    }
}
