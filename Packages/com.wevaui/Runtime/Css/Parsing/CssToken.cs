namespace Weva.Css {
    public struct CssToken {
        public CssTokenKind Kind;
        public string Text;
        public double Number;
        public string Unit;
        public int Line;
        public int Column;
    }
}
