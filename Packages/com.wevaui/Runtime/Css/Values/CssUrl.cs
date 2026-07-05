namespace Weva.Css.Values {
    public sealed class CssUrl : CssValue {
        public string Href { get; }

        public override CssValueKind Kind => CssValueKind.Url;

        public CssUrl(string href) {
            Href = href ?? "";
            Raw = "url(" + Href + ")";
        }

        public CssUrl(string href, string raw) {
            Href = href ?? "";
            Raw = raw;
        }
    }
}
