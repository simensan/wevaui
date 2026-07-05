using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Weva.Css.Values {
    public sealed class CssCalc : CssValue {
        public CalcNode Expression { get; }

        public override CssValueKind Kind => CssValueKind.Calc;

        public CssCalc(CalcNode expression, string raw) {
            Expression = expression;
            Raw = raw;
        }

        public CssCalc(CalcNode expression) {
            Expression = expression;
            Raw = "calc(" + NodeToText(expression) + ")";
        }

        public double Evaluate(LengthContext ctx) {
            return EvaluateNode(Expression, ctx, default);
        }

        // CSS Color L5 §4: calc() expressions inside a relative-color channel
        // slot may reference channel identifiers (e.g. `r`, `g`, `b`, `alpha`,
        // `h`, `s`, `l`, `w`, `c`, `a` depending on the host color space). The
        // caller pre-populates `channels` with the resolved source-color
        // channel values; outside a relative-color slot, `channels` is `default`
        // and any CalcChannelNode resolves to a typed exception.
        public double Evaluate(LengthContext ctx, CalcChannelBindings channels) {
            return EvaluateNode(Expression, ctx, channels);
        }

        public string ToText() {
            return "calc(" + NodeToText(Expression) + ")";
        }

        static double EvaluateNode(CalcNode node, LengthContext ctx, CalcChannelBindings channels) {
            switch (node) {
                case CalcLengthNode l: return l.Length.ToPixels(ctx);
                case CalcNumberNode n: return n.Number.Value;
                case CalcPercentageNode p: return p.Percentage.ToPixels(ctx);
                case CalcAngleNode a: return a.Degrees;
                case CalcChannelNode ch:
                    if (!channels.TryGet(ch.ChannelName, out double chVal)) {
                        throw new InvalidOperationException("calc() references channel '" + ch.ChannelName + "' outside a relative-color slot");
                    }
                    return chVal;
                case CalcVariableNode v:
                    throw new InvalidOperationException("Cannot evaluate calc() containing unresolved var(" + v.Variable.Name + ")");
                case CalcBinaryNode b: {
                    switch (b.Op) {
                        case CalcOp.Add:
                        case CalcOp.Sub: {
                            CalcType lt = ClassifyType(b.Left);
                            CalcType rt = ClassifyType(b.Right);
                            if (!TypesCompatible(lt, rt)) {
                                throw new InvalidOperationException("calc() '" + OpChar(b.Op) + "' requires compatible operand types (got " + TypeName(lt) + " and " + TypeName(rt) + ")");
                            }
                            double l = EvaluateNode(b.Left, ctx, channels);
                            double r = EvaluateNode(b.Right, ctx, channels);
                            return b.Op == CalcOp.Add ? l + r : l - r;
                        }
                        case CalcOp.Mul: {
                            CalcType lt = ClassifyType(b.Left);
                            CalcType rt = ClassifyType(b.Right);
                            if (lt != CalcType.Number && rt != CalcType.Number) {
                                throw new InvalidOperationException("calc() '*' requires at least one <number> operand (got " + TypeName(lt) + " and " + TypeName(rt) + ")");
                            }
                            return EvaluateNode(b.Left, ctx, channels) * EvaluateNode(b.Right, ctx, channels);
                        }
                        case CalcOp.Div: {
                            CalcType rt = ClassifyType(b.Right);
                            if (rt != CalcType.Number) {
                                throw new InvalidOperationException("calc() '/' denominator must be a <number> (got " + TypeName(rt) + ")");
                            }
                            double l = EvaluateNode(b.Left, ctx, channels);
                            double r = EvaluateNode(b.Right, ctx, channels);
                            if (r == 0) throw new InvalidOperationException("Division by zero in calc()");
                            return l / r;
                        }
                    }
                    break;
                }
                case CalcMathNode m: {
                    switch (m.Function) {
                        case CalcMathFunction.Min: {
                            RequireCompatibleArgs(m, "min");
                            double best = double.PositiveInfinity;
                            for (int i = 0; i < m.Args.Count; i++) {
                                double v = EvaluateNode(m.Args[i], ctx, channels);
                                if (v < best) best = v;
                            }
                            return best;
                        }
                        case CalcMathFunction.Max: {
                            RequireCompatibleArgs(m, "max");
                            double best = double.NegativeInfinity;
                            for (int i = 0; i < m.Args.Count; i++) {
                                double v = EvaluateNode(m.Args[i], ctx, channels);
                                if (v > best) best = v;
                            }
                            return best;
                        }
                        case CalcMathFunction.Clamp: {
                            // clamp(MIN, VAL, MAX) = max(MIN, min(VAL, MAX))
                            if (m.Args.Count != 3) {
                                throw new InvalidOperationException("clamp() requires exactly 3 arguments");
                            }
                            RequireCompatibleArgs(m, "clamp");
                            double lo = EvaluateNode(m.Args[0], ctx, channels);
                            double val = EvaluateNode(m.Args[1], ctx, channels);
                            double hi = EvaluateNode(m.Args[2], ctx, channels);
                            double upper = val < hi ? val : hi;
                            return lo > upper ? lo : upper;
                        }
                        case CalcMathFunction.Round: {
                            if (m.Args.Count != 2) {
                                throw new InvalidOperationException("round() requires exactly 2 arguments");
                            }
                            RequireCompatibleArgs(m, "round");
                            double a = EvaluateNode(m.Args[0], ctx, channels);
                            double b = EvaluateNode(m.Args[1], ctx, channels);
                            return ApplyRound(a, b, m.RoundingStrategy);
                        }
                        case CalcMathFunction.Mod: {
                            // CSS Values L4 §10.7.2: mod(A, B) result takes the sign of B.
                            if (m.Args.Count != 2) {
                                throw new InvalidOperationException("mod() requires exactly 2 arguments");
                            }
                            RequireCompatibleArgs(m, "mod");
                            double a = EvaluateNode(m.Args[0], ctx, channels);
                            double b = EvaluateNode(m.Args[1], ctx, channels);
                            if (b == 0) return double.NaN;
                            return a - b * Math.Floor(a / b);
                        }
                        case CalcMathFunction.Rem: {
                            // CSS Values L4 §10.7.3: rem(A, B) result takes the sign of A.
                            if (m.Args.Count != 2) {
                                throw new InvalidOperationException("rem() requires exactly 2 arguments");
                            }
                            RequireCompatibleArgs(m, "rem");
                            double a = EvaluateNode(m.Args[0], ctx, channels);
                            double b = EvaluateNode(m.Args[1], ctx, channels);
                            if (b == 0) return double.NaN;
                            return a - b * Math.Truncate(a / b);
                        }
                        case CalcMathFunction.Pow: {
                            if (m.Args.Count != 2) {
                                throw new InvalidOperationException("pow() requires exactly 2 arguments");
                            }
                            double a = EvaluateNode(m.Args[0], ctx, channels);
                            double b = EvaluateNode(m.Args[1], ctx, channels);
                            return Math.Pow(a, b);
                        }
                        case CalcMathFunction.Sqrt: {
                            if (m.Args.Count != 1) {
                                throw new InvalidOperationException("sqrt() requires exactly 1 argument");
                            }
                            return Math.Sqrt(EvaluateNode(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Hypot: {
                            if (m.Args.Count < 1) {
                                throw new InvalidOperationException("hypot() requires at least 1 argument");
                            }
                            RequireCompatibleArgs(m, "hypot");
                            double sum = 0;
                            for (int i = 0; i < m.Args.Count; i++) {
                                double v = EvaluateNode(m.Args[i], ctx, channels);
                                sum += v * v;
                            }
                            return Math.Sqrt(sum);
                        }
                        case CalcMathFunction.Log: {
                            if (m.Args.Count == 1) {
                                return Math.Log(EvaluateNode(m.Args[0], ctx, channels));
                            }
                            if (m.Args.Count == 2) {
                                double a = EvaluateNode(m.Args[0], ctx, channels);
                                double b = EvaluateNode(m.Args[1], ctx, channels);
                                return Math.Log(a, b);
                            }
                            throw new InvalidOperationException("log() requires 1 or 2 arguments");
                        }
                        case CalcMathFunction.Exp: {
                            if (m.Args.Count != 1) {
                                throw new InvalidOperationException("exp() requires exactly 1 argument");
                            }
                            return Math.Exp(EvaluateNode(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Abs: {
                            if (m.Args.Count != 1) {
                                throw new InvalidOperationException("abs() requires exactly 1 argument");
                            }
                            return Math.Abs(EvaluateNode(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Sign: {
                            if (m.Args.Count != 1) {
                                throw new InvalidOperationException("sign() requires exactly 1 argument");
                            }
                            double s = EvaluateNode(m.Args[0], ctx, channels);
                            if (s > 0) return 1;
                            if (s < 0) return -1;
                            return 0;
                        }
                        case CalcMathFunction.Sin: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("sin() requires exactly 1 argument");
                            return Math.Sin(TrigInputRadians(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Cos: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("cos() requires exactly 1 argument");
                            return Math.Cos(TrigInputRadians(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Tan: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("tan() requires exactly 1 argument");
                            return Math.Tan(TrigInputRadians(m.Args[0], ctx, channels));
                        }
                        case CalcMathFunction.Asin: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("asin() requires exactly 1 argument");
                            return RadiansToDegrees(Math.Asin(EvaluateNode(m.Args[0], ctx, channels)));
                        }
                        case CalcMathFunction.Acos: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("acos() requires exactly 1 argument");
                            return RadiansToDegrees(Math.Acos(EvaluateNode(m.Args[0], ctx, channels)));
                        }
                        case CalcMathFunction.Atan: {
                            if (m.Args.Count != 1) throw new InvalidOperationException("atan() requires exactly 1 argument");
                            return RadiansToDegrees(Math.Atan(EvaluateNode(m.Args[0], ctx, channels)));
                        }
                        case CalcMathFunction.Atan2: {
                            if (m.Args.Count != 2) throw new InvalidOperationException("atan2() requires exactly 2 arguments");
                            RequireCompatibleArgs(m, "atan2");
                            double a = EvaluateNode(m.Args[0], ctx, channels);
                            double b = EvaluateNode(m.Args[1], ctx, channels);
                            return RadiansToDegrees(Math.Atan2(a, b));
                        }
                    }
                    break;
                }
            }
            throw new InvalidOperationException("Unknown calc node");
        }

        enum CalcType { Unknown, Number, Length, Percentage, Angle }

        // CSS Values 3 §10: classify a calc subexpression by the type it
        // produces. Mul/Div fold the non-number operand's type through;
        // Add/Sub propagate the (already-checked) compatible type.
        static CalcType ClassifyType(CalcNode node) {
            switch (node) {
                case CalcNumberNode _: return CalcType.Number;
                case CalcLengthNode _: return CalcType.Length;
                case CalcPercentageNode _: return CalcType.Percentage;
                case CalcAngleNode _: return CalcType.Angle;
                // CSS Color L5 §4: channel-ident references inside calc()
                // resolve to <number>-valued channel scalars (e.g. `r` -> 0..255,
                // `l` -> 0..1, `h` -> degrees). Typing them as <number> lets
                // them combine with `+ 20` / `* 0.5` without the
                // type-compatibility checker rejecting the expression.
                case CalcChannelNode _: return CalcType.Number;
                case CalcVariableNode _: return CalcType.Unknown;
                case CalcBinaryNode b: {
                    CalcType lt = ClassifyType(b.Left);
                    CalcType rt = ClassifyType(b.Right);
                    switch (b.Op) {
                        case CalcOp.Add:
                        case CalcOp.Sub:
                            if (lt == CalcType.Length && rt == CalcType.Percentage) return CalcType.Length;
                            if (lt == CalcType.Percentage && rt == CalcType.Length) return CalcType.Length;
                            return lt;
                        case CalcOp.Mul:
                            if (lt == CalcType.Number) return rt;
                            return lt;
                        case CalcOp.Div:
                            return lt;
                    }
                    return CalcType.Unknown;
                }
                case CalcMathNode m: {
                    switch (m.Function) {
                        case CalcMathFunction.Asin:
                        case CalcMathFunction.Acos:
                        case CalcMathFunction.Atan:
                        case CalcMathFunction.Atan2:
                            return CalcType.Angle;
                        case CalcMathFunction.Sin:
                        case CalcMathFunction.Cos:
                        case CalcMathFunction.Tan:
                        case CalcMathFunction.Sqrt:
                        case CalcMathFunction.Pow:
                        case CalcMathFunction.Log:
                        case CalcMathFunction.Exp:
                        case CalcMathFunction.Sign:
                            return CalcType.Number;
                    }
                    if (m.Args.Count > 0) return ClassifyType(m.Args[0]);
                    return CalcType.Unknown;
                }
            }
            return CalcType.Unknown;
        }

        static bool TypesCompatible(CalcType a, CalcType b) {
            if (a == CalcType.Unknown || b == CalcType.Unknown) return true;
            if (a == b) return true;
            if ((a == CalcType.Length && b == CalcType.Percentage) ||
                (a == CalcType.Percentage && b == CalcType.Length)) return true;
            return false;
        }

        static string TypeName(CalcType t) {
            switch (t) {
                case CalcType.Number: return "<number>";
                case CalcType.Length: return "<length>";
                case CalcType.Percentage: return "<percentage>";
                case CalcType.Angle: return "<angle>";
            }
            return "<unknown>";
        }

        static void RequireCompatibleArgs(CalcMathNode m, string fn) {
            if (m.Args.Count < 2) return;
            CalcType baseType = ClassifyType(m.Args[0]);
            for (int i = 1; i < m.Args.Count; i++) {
                CalcType t = ClassifyType(m.Args[i]);
                if (!TypesCompatible(baseType, t)) {
                    throw new InvalidOperationException(fn + "() arguments must share a type (got " + TypeName(baseType) + " and " + TypeName(t) + ")");
                }
                if (baseType == CalcType.Unknown) baseType = t;
            }
        }

        static double ApplyRound(double a, double b, CalcRoundingStrategy strategy) {
            if (b == 0) return double.NaN;
            double q = a / b;
            double k;
            switch (strategy) {
                case CalcRoundingStrategy.Up: k = Math.Ceiling(q); break;
                case CalcRoundingStrategy.Down: k = Math.Floor(q); break;
                case CalcRoundingStrategy.ToZero: k = Math.Truncate(q); break;
                default: k = Math.Floor(q + 0.5); break;
            }
            return k * b;
        }

        // CSS Values L4 §10.8: trig inputs accept <angle> or <number> (radians).
        // CalcAngleNode stores degrees so we convert; bare numbers pass through.
        static double TrigInputRadians(CalcNode arg, LengthContext ctx, CalcChannelBindings channels) {
            double v = EvaluateNode(arg, ctx, channels);
            if (IsAngleNode(arg)) return v * Math.PI / 180.0;
            return v;
        }

        static bool IsAngleNode(CalcNode n) {
            if (n is CalcAngleNode) return true;
            if (n is CalcBinaryNode b) return IsAngleNode(b.Left) || IsAngleNode(b.Right);
            if (n is CalcMathNode m) {
                switch (m.Function) {
                    case CalcMathFunction.Asin:
                    case CalcMathFunction.Acos:
                    case CalcMathFunction.Atan:
                    case CalcMathFunction.Atan2:
                        return true;
                }
            }
            return false;
        }

        static double RadiansToDegrees(double r) {
            return r * 180.0 / Math.PI;
        }

        static string NodeToText(CalcNode node) {
            var sb = new StringBuilder();
            AppendNode(sb, node);
            return sb.ToString();
        }

        static void AppendNode(StringBuilder sb, CalcNode node) {
            switch (node) {
                case CalcLengthNode l:
                    sb.Append(l.Length.Raw);
                    return;
                case CalcNumberNode n:
                    sb.Append(n.Number.Raw ?? n.Number.Value.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case CalcPercentageNode p:
                    sb.Append(p.Percentage.Raw);
                    return;
                case CalcAngleNode a:
                    sb.Append(a.Raw ?? (a.Degrees.ToString("R", CultureInfo.InvariantCulture) + "deg"));
                    return;
                case CalcChannelNode ch:
                    sb.Append(ch.ChannelName);
                    return;
                case CalcVariableNode v:
                    sb.Append(v.Variable.Raw);
                    return;
                case CalcBinaryNode b: {
                    bool needLeftParen = b.Left is CalcBinaryNode lb && Precedence(lb.Op) < Precedence(b.Op);
                    bool needRightParen = b.Right is CalcBinaryNode rb && Precedence(rb.Op) < Precedence(b.Op);
                    if (needLeftParen) sb.Append('(');
                    AppendNode(sb, b.Left);
                    if (needLeftParen) sb.Append(')');
                    sb.Append(' ');
                    sb.Append(OpChar(b.Op));
                    sb.Append(' ');
                    if (needRightParen) sb.Append('(');
                    AppendNode(sb, b.Right);
                    if (needRightParen) sb.Append(')');
                    return;
                }
                case CalcMathNode m: {
                    sb.Append(MathFunctionName(m.Function));
                    sb.Append('(');
                    bool first = true;
                    if (m.Function == CalcMathFunction.Round && m.RoundingStrategy != CalcRoundingStrategy.Nearest) {
                        sb.Append(RoundingStrategyName(m.RoundingStrategy));
                        first = false;
                    }
                    for (int i = 0; i < m.Args.Count; i++) {
                        if (!first) sb.Append(", ");
                        first = false;
                        AppendNode(sb, m.Args[i]);
                    }
                    sb.Append(')');
                    return;
                }
            }
        }

        static string MathFunctionName(CalcMathFunction f) {
            switch (f) {
                case CalcMathFunction.Min: return "min";
                case CalcMathFunction.Max: return "max";
                case CalcMathFunction.Clamp: return "clamp";
                case CalcMathFunction.Round: return "round";
                case CalcMathFunction.Mod: return "mod";
                case CalcMathFunction.Rem: return "rem";
                case CalcMathFunction.Pow: return "pow";
                case CalcMathFunction.Sqrt: return "sqrt";
                case CalcMathFunction.Hypot: return "hypot";
                case CalcMathFunction.Log: return "log";
                case CalcMathFunction.Exp: return "exp";
                case CalcMathFunction.Abs: return "abs";
                case CalcMathFunction.Sign: return "sign";
                case CalcMathFunction.Sin: return "sin";
                case CalcMathFunction.Cos: return "cos";
                case CalcMathFunction.Tan: return "tan";
                case CalcMathFunction.Asin: return "asin";
                case CalcMathFunction.Acos: return "acos";
                case CalcMathFunction.Atan: return "atan";
                case CalcMathFunction.Atan2: return "atan2";
            }
            return "";
        }

        static string RoundingStrategyName(CalcRoundingStrategy s) {
            switch (s) {
                case CalcRoundingStrategy.Up: return "up";
                case CalcRoundingStrategy.Down: return "down";
                case CalcRoundingStrategy.ToZero: return "to-zero";
            }
            return "nearest";
        }

        static int Precedence(CalcOp op) {
            switch (op) {
                case CalcOp.Mul:
                case CalcOp.Div: return 2;
                case CalcOp.Add:
                case CalcOp.Sub: return 1;
            }
            return 0;
        }

        static char OpChar(CalcOp op) {
            switch (op) {
                case CalcOp.Add: return '+';
                case CalcOp.Sub: return '-';
                case CalcOp.Mul: return '*';
                case CalcOp.Div: return '/';
            }
            return '?';
        }
    }

    public enum CalcOp {
        Add,
        Sub,
        Mul,
        Div
    }

    public abstract class CalcNode { }

    public sealed class CalcLengthNode : CalcNode {
        public CssLength Length { get; }
        public CalcLengthNode(CssLength length) { Length = length; }
    }

    public sealed class CalcNumberNode : CalcNode {
        public CssNumber Number { get; }
        public CalcNumberNode(CssNumber number) { Number = number; }
    }

    public sealed class CalcPercentageNode : CalcNode {
        public CssPercentage Percentage { get; }
        public CalcPercentageNode(CssPercentage percentage) { Percentage = percentage; }
    }

    public sealed class CalcAngleNode : CalcNode {
        public double Degrees { get; }
        public string Raw { get; }
        public CalcAngleNode(double degrees, string raw) { Degrees = degrees; Raw = raw; }
    }

    public sealed class CalcVariableNode : CalcNode {
        public CssVariableReference Variable { get; }
        public CalcVariableNode(CssVariableReference v) { Variable = v; }
    }

    // CSS Color L5 §4: a channel-ident reference inside a relative-color
    // calc() slot (e.g. `calc(r + 20)` inside `rgb(from C ...)`). ChannelName
    // is the literal identifier (`r`, `g`, `b`, `alpha`, `h`, `s`, `l`, `w`,
    // `c`, `a`); the resolver matches case-insensitively. The evaluator pulls
    // the actual numeric value from the CalcChannelBindings supplied by the
    // host color-parser at evaluation time.
    public sealed class CalcChannelNode : CalcNode {
        public string ChannelName { get; }
        public CalcChannelNode(string channelName) { ChannelName = channelName; }
    }

    // Channel bindings passed to CssCalc.Evaluate by the relative-color
    // parser. Pre-populated with the source color's channel values (already
    // mapped into the host function's output space and channel units). A
    // `default` value (no map) signals "not in a relative-color slot" — any
    // CalcChannelNode encountered then raises a typed exception.
    public struct CalcChannelBindings {
        Dictionary<string, double> map;

        public static CalcChannelBindings Create() {
            return new CalcChannelBindings { map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) };
        }

        public void Set(string name, double value) {
            if (map == null) map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            map[name] = value;
        }

        public bool TryGet(string name, out double value) {
            if (map == null) { value = 0; return false; }
            return map.TryGetValue(name, out value);
        }

        public bool Has(string name) {
            return map != null && map.ContainsKey(name);
        }

        public bool IsActive => map != null;
    }

    public sealed class CalcBinaryNode : CalcNode {
        public CalcOp Op { get; }
        public CalcNode Left { get; }
        public CalcNode Right { get; }
        public CalcBinaryNode(CalcOp op, CalcNode left, CalcNode right) {
            Op = op; Left = left; Right = right;
        }
    }

    public enum CalcMathFunction {
        Min,
        Max,
        Clamp,
        Round,
        Mod,
        Rem,
        Pow,
        Sqrt,
        Hypot,
        Log,
        Exp,
        Abs,
        Sign,
        Sin,
        Cos,
        Tan,
        Asin,
        Acos,
        Atan,
        Atan2
    }

    public enum CalcRoundingStrategy {
        Nearest,
        Up,
        Down,
        ToZero
    }

    public sealed class CalcMathNode : CalcNode {
        public CalcMathFunction Function { get; }
        public List<CalcNode> Args { get; }
        public CalcRoundingStrategy RoundingStrategy { get; }
        public CalcMathNode(CalcMathFunction function, List<CalcNode> args) {
            Function = function;
            Args = args;
            RoundingStrategy = CalcRoundingStrategy.Nearest;
        }
        public CalcMathNode(CalcMathFunction function, List<CalcNode> args, CalcRoundingStrategy strategy) {
            Function = function;
            Args = args;
            RoundingStrategy = strategy;
        }
    }
}
