using System.Globalization;

namespace Weva.Css.Values {
    public sealed class CssRatio : CssValue {
        public double Numerator { get; }
        public double Denominator { get; }

        public override CssValueKind Kind => CssValueKind.Number;

        public bool IsValid => Numerator > 0 && Denominator > 0;

        public double Value => Denominator == 0 ? 0 : Numerator / Denominator;

        public CssRatio(double numerator, double denominator) {
            Numerator = numerator;
            Denominator = denominator;
            Raw = numerator.ToString("R", CultureInfo.InvariantCulture)
                + " / "
                + denominator.ToString("R", CultureInfo.InvariantCulture);
        }

        public CssRatio(double numerator, double denominator, string raw) {
            Numerator = numerator;
            Denominator = denominator;
            Raw = raw;
        }

        public static bool TryParse(string raw, out CssRatio ratio) {
            ratio = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string trimmed = raw.Trim();
            if (trimmed == "auto") return false;
            int slash = trimmed.IndexOf('/');
            if (slash < 0) {
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
                if (n <= 0) return false;
                ratio = new CssRatio(n, 1, trimmed);
                return true;
            }
            string left = trimmed.Substring(0, slash).Trim();
            string right = trimmed.Substring(slash + 1).Trim();
            if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double nn)) return false;
            if (!double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double dd)) return false;
            if (nn <= 0 || dd <= 0) return false;
            ratio = new CssRatio(nn, dd, trimmed);
            return true;
        }
    }
}
