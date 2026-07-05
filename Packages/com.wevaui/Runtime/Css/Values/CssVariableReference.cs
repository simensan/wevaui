namespace Weva.Css.Values {
    public sealed class CssVariableReference : CssValue {
        public string Name { get; }
        public CssValue Fallback { get; }

        public override CssValueKind Kind => CssValueKind.VariableReference;

        public CssVariableReference(string name, CssValue fallback) {
            Name = name ?? "";
            Fallback = fallback;
            Raw = fallback == null
                ? "var(" + Name + ")"
                : "var(" + Name + ", " + (fallback.Raw ?? fallback.ToString()) + ")";
        }

        public CssVariableReference(string name, CssValue fallback, string raw) {
            Name = name ?? "";
            Fallback = fallback;
            Raw = raw;
        }
    }
}
