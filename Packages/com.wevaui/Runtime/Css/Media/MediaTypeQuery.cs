namespace Weva.Css.Media {
    public sealed class MediaTypeQuery : MediaQuery {
        public MediaType Type { get; }

        public MediaTypeQuery(MediaType type) {
            Type = type;
        }

        public override bool Evaluate(MediaContext ctx) {
            if (Type == MediaType.All) return true;
            return Type == ctx.Type;
        }
    }
}
