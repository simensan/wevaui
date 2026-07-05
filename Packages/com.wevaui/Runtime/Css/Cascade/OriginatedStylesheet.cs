namespace Weva.Css.Cascade {
    public sealed class OriginatedStylesheet {
        public Stylesheet Stylesheet { get; }
        public DeclarationOrigin Origin { get; }

        public OriginatedStylesheet(Stylesheet stylesheet, DeclarationOrigin origin) {
            Stylesheet = stylesheet;
            Origin = origin;
        }

        public static OriginatedStylesheet UserAgent(Stylesheet s) => new(s, DeclarationOrigin.UserAgent);
        public static OriginatedStylesheet User(Stylesheet s) => new(s, DeclarationOrigin.User);
        public static OriginatedStylesheet Author(Stylesheet s) => new(s, DeclarationOrigin.Author);
    }
}
