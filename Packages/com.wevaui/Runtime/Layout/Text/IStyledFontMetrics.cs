using Weva.Paint;

namespace Weva.Layout.Text {
    // Optional extension for metrics providers that can resolve CSS font
    // style/weight variants. IFontMetrics stays source-compatible for tests
    // and simple backends; layout asks for this only when available.
    public interface IStyledFontMetrics : IFontMetrics {
        double Measure(string text, double fontSize, string family, FontStyle style, int weight);

        // Substring-window styled overload. See IFontMetrics.Measure(...) for
        // the same allocation-avoidance rationale (CODE_AUDIT_FINDINGS P7/P8).
        // When (start == 0 && length == text?.Length) the result MUST match
        // Measure(text, fontSize, family, style, weight) exactly.
        double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight);

        // PAINT-1: weight/style-aware line-box metrics. The unstyled overloads
        // on IFontMetrics use the DEFAULT face — when a span declares
        // `font-weight: 900` and the bold face has a different ascender ratio
        // than the regular face, layout's line-box height (based on the regular
        // face) diverges from paint's baseline placement (which uses the 900
        // face's actual metrics via `Metrics.MetricsFor(family, style, weight)`).
        // The visible symptom is a top-heavy or bottom-heavy text block in
        // flex-column containers because the per-line glyph centroid no longer
        // lands at the centre of the line-box layout sized for it.
        double LineHeight(double fontSize, string family, FontStyle style, int weight);
        double Ascent(double fontSize, string family, FontStyle style, int weight);
        double Descent(double fontSize, string family, FontStyle style, int weight);
    }
}
