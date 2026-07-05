namespace Weva.Css.Values {
    public sealed class CssIdentifier : CssValue {
        public string Name { get; }

        public override CssValueKind Kind => CssValueKind.Identifier;

        public CssIdentifier(string name) {
            Name = name ?? "";
            Raw = Name;
        }

        public CssIdentifier(string name, string raw) {
            Name = name ?? "";
            Raw = raw;
        }
    }
}
