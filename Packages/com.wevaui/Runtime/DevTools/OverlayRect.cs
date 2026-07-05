namespace Weva.DevTools {
    public enum OverlayRectKind {
        Margin,
        Border,
        Padding,
        Content,
    }

    public struct OverlayRect {
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public OverlayRectKind Kind;

        public OverlayRect(double x, double y, double w, double h, OverlayRectKind kind) {
            X = x; Y = y; Width = w; Height = h; Kind = kind;
        }
    }
}
