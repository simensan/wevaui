namespace Weva.Css.Values {
    public sealed class CssKeyword : CssValue {
        public string Identifier { get; }

        public override CssValueKind Kind => CssValueKind.Keyword;

        public CssKeyword(string identifier) {
            Identifier = identifier == null ? "" : CssStringUtil.ToLowerInvariantOrSame(identifier);
            Raw = Identifier;
        }

        public CssKeyword(string identifier, string raw) {
            Identifier = identifier == null ? "" : CssStringUtil.ToLowerInvariantOrSame(identifier);
            Raw = raw;
        }
    }
}
