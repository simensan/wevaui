namespace Weva.Css.Values {
    public sealed class CssString : CssValue {
        public string Value { get; }

        public override CssValueKind Kind => CssValueKind.String;

        public CssString(string value, string raw) {
            Value = value ?? "";
            Raw = raw;
        }

        public CssString(string value, char quote) {
            Value = value ?? "";
            Raw = quote + (value ?? "") + quote;
        }
    }
}
