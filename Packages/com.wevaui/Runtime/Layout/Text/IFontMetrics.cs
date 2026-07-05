namespace Weva.Layout.Text {
    public interface IFontMetrics {
        double LineHeight(double fontSize);
        double Measure(string text, double fontSize);
        double Ascent(double fontSize);
        double Descent(double fontSize);

        // Substring-window overload: measures text[start .. start+length) in-place
        // without materialising a fresh System.String per call. The LineBreaker's
        // binary-search wrap path probes O(log n) prefixes of the same word per
        // overflow; the string overload above forced a per-probe Substring alloc
        // that showed up as ~10K small string allocs on a 1000-word paragraph
        // (CODE_AUDIT_FINDINGS P7). Implementations should walk the source
        // string with the loop bounds clamped to [start, start+length).
        //
        // Contract: when (start == 0 && length == text?.Length) the result MUST
        // equal Measure(text, fontSize) exactly. Bounds outside the string are
        // clamped to the empty slice (returns 0). Null / empty text returns 0.
        double Measure(string text, int start, int length, double fontSize);
    }
}
