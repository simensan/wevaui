namespace Weva.Css.Media {
    public sealed class MediaNotQuery : MediaQuery {
        public MediaQuery Child { get; }

        public MediaNotQuery(MediaQuery child) {
            Child = child;
        }

        public override bool Evaluate(MediaContext ctx) {
            if (Child == null) return false;
            return !Child.Evaluate(ctx);
        }
    }
}
