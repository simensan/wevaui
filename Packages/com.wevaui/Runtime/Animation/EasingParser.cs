using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Animation {
    public static class EasingParser {
        public static EasingFunction Parse(string text) {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string s = text.Trim();
            if (s.Length == 0) throw new FormatException("Empty easing function");

            string lower = CssStringUtil.ToLowerInvariantOrSame(s);
            switch (lower) {
                case "linear": return LinearEasing.Instance;
                case "ease": return EaseEasing.Instance;
                case "ease-in": return EaseInEasing.Instance;
                case "ease-out": return EaseOutEasing.Instance;
                case "ease-in-out": return EaseInOutEasing.Instance;
                case "step-start": return new StepsEasing(1, StepPosition.Start);
                case "step-end": return new StepsEasing(1, StepPosition.End);
            }

            if (TryParseFunction(lower, "cubic-bezier", out string cbArgs)) {
                return ParseCubicBezier(cbArgs);
            }
            if (TryParseFunction(lower, "steps", out string stepArgs)) {
                return ParseSteps(stepArgs);
            }
            if (TryParseFunction(lower, "linear", out string linArgs)) {
                return ParseLinear(linArgs);
            }

            throw new FormatException("Unknown easing function: " + text);
        }

        static bool TryParseFunction(string s, string name, out string args) {
            if (s.StartsWith(name, StringComparison.Ordinal)) {
                int after = name.Length;
                while (after < s.Length && char.IsWhiteSpace(s[after])) after++;
                if (after < s.Length && s[after] == '(' && s[s.Length - 1] == ')') {
                    args = s.Substring(after + 1, s.Length - after - 2);
                    return true;
                }
            }
            args = null;
            return false;
        }

        static CubicBezierEasing ParseCubicBezier(string args) {
            string[] parts = args.Split(',');
            if (parts.Length != 4) {
                throw new FormatException("cubic-bezier requires exactly 4 arguments");
            }
            double x1 = ParseDouble(parts[0]);
            double y1 = ParseDouble(parts[1]);
            double x2 = ParseDouble(parts[2]);
            double y2 = ParseDouble(parts[3]);
            if (x1 < 0 || x1 > 1 || x2 < 0 || x2 > 1) {
                throw new FormatException("cubic-bezier x coordinates must be in [0, 1]");
            }
            return new CubicBezierEasing(x1, y1, x2, y2);
        }

        static StepsEasing ParseSteps(string args) {
            string[] parts = args.Split(',');
            if (parts.Length < 1 || parts.Length > 2) {
                throw new FormatException("steps requires 1 or 2 arguments");
            }
            string countText = parts[0].Trim();
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)) {
                throw new FormatException("steps count must be an integer");
            }
            if (count < 1) {
                throw new FormatException("steps count must be >= 1");
            }
            StepPosition pos = StepPosition.End;
            if (parts.Length == 2) {
                string posText = parts[1].Trim();
                switch (posText) {
                    case "start": pos = StepPosition.Start; break;
                    case "end": pos = StepPosition.End; break;
                    case "jump-start": pos = StepPosition.JumpStart; break;
                    case "jump-end": pos = StepPosition.JumpEnd; break;
                    case "jump-both": pos = StepPosition.JumpBoth; break;
                    case "jump-none": pos = StepPosition.JumpNone; break;
                    default:
                        throw new FormatException("Unknown steps position: " + posText);
                }
            }
            // CSS Easing Functions L1 §2.3: "If the <step-position> is
            // jump-none, the <integer> must be at least 2, or the function
            // is invalid." With count=1 the sampler would divide by
            // (N-1)=0, producing NaN/Infinity outputs that corrupt the
            // animated property.
            if (pos == StepPosition.JumpNone && count < 2) {
                throw new FormatException("steps(jump-none) count must be >= 2");
            }
            return new StepsEasing(count, pos);
        }

        static PiecewiseLinearEasing ParseLinear(string args) {
            if (string.IsNullOrWhiteSpace(args)) {
                throw new FormatException("linear() requires at least one control point");
            }
            string[] parts = args.Split(',');
            // Raw points: output value plus 0, 1, or 2 explicit input percentages.
            var raw = new List<(double output, double? a, double? b)>(parts.Length);
            foreach (var part in parts) {
                string token = part.Trim();
                if (token.Length == 0) throw new FormatException("linear() empty control point");
                string[] toks = token.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (toks.Length < 1 || toks.Length > 3) {
                    throw new FormatException("linear() control point must be <number> [<percentage> [<percentage>]]");
                }
                double output = ParseDouble(toks[0]);
                double? a = null, b = null;
                if (toks.Length >= 2) a = ParsePercent(toks[1]);
                if (toks.Length >= 3) b = ParsePercent(toks[2]);
                raw.Add((output, a, b));
            }

            // CSS Easing L2 §3.2 step 1: expand double-position shorthand (output A% B%) into two points.
            var expanded = new List<(double output, double? input)>(raw.Count + 4);
            foreach (var (output, a, b) in raw) {
                expanded.Add((output, a));
                if (b.HasValue) expanded.Add((output, b));
            }
            if (expanded.Count < 2) {
                throw new FormatException("linear() requires at least two control points");
            }

            // §3.2 steps 2-3: anchor endpoints, then fill missing inputs by linear distribution
            // across each run of consecutive points without an explicit input. Also enforce
            // non-decreasing inputs by clamping each to the running maximum.
            if (!expanded[0].input.HasValue) expanded[0] = (expanded[0].output, 0.0);
            int lastIdx = expanded.Count - 1;
            if (!expanded[lastIdx].input.HasValue) expanded[lastIdx] = (expanded[lastIdx].output, 1.0);

            int i2 = 0;
            while (i2 < expanded.Count) {
                if (expanded[i2].input.HasValue) { i2++; continue; }
                int start = i2 - 1; // last known
                int end = i2;
                while (end < expanded.Count && !expanded[end].input.HasValue) end++;
                double lo = expanded[start].input.Value;
                double hi = expanded[end].input.Value;
                int gaps = end - start;
                for (int k = i2; k < end; k++) {
                    double frac = (double)(k - start) / gaps;
                    expanded[k] = (expanded[k].output, lo + frac * (hi - lo));
                }
                i2 = end;
            }

            var points = new List<PiecewiseLinearEasing.Point>(expanded.Count);
            double runningMax = expanded[0].input.Value;
            foreach (var (output, input) in expanded) {
                double clamped = input.Value;
                if (clamped < runningMax) clamped = runningMax;
                else runningMax = clamped;
                points.Add(new PiecewiseLinearEasing.Point(output, clamped));
            }
            return new PiecewiseLinearEasing(points);
        }

        static double ParsePercent(string s) {
            string trimmed = s.Trim();
            if (!trimmed.EndsWith("%", StringComparison.Ordinal)) {
                throw new FormatException("linear() input position must be a percentage: " + s);
            }
            string numText = trimmed.Substring(0, trimmed.Length - 1);
            if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) {
                throw new FormatException("Invalid percentage: " + s);
            }
            return pct / 100.0;
        }

        static double ParseDouble(string s) {
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) {
                throw new FormatException("Invalid number: " + s);
            }
            return d;
        }
    }
}
