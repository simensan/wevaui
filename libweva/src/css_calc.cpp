#include "weva/css_calc.h"

#include <cmath>
#include <limits>

namespace weva {

namespace {

const char* type_name(CalcType t) {
    switch (t) {
        case CalcType::Number:     return "<number>";
        case CalcType::Length:     return "<length>";
        case CalcType::Percentage: return "<percentage>";
        case CalcType::Angle:      return "<angle>";
        case CalcType::Unknown:    break;
    }
    return "<unknown>";
}

char op_char(CalcOp op) {
    switch (op) {
        case CalcOp::Add: return '+';
        case CalcOp::Sub: return '-';
        case CalcOp::Mul: return '*';
        case CalcOp::Div: return '/';
    }
    return '?';
}

bool eval(const CalcNode& n, const LengthContext& ctx, double* out, std::string* why);

bool require_compatible_args(const CalcMathNode& m, const char* fn, std::string* why) {
    if (m.args.size() < 2) return true;
    CalcType base = calc_classify(*m.args[0]);
    for (std::size_t i = 1; i < m.args.size(); ++i) {
        CalcType t = calc_classify(*m.args[i]);
        if (!calc_types_compatible(base, t)) {
            *why = std::string(fn) + "() arguments must share a type (got " +
                   type_name(base) + " and " + type_name(t) + ")";
            return false;
        }
        if (base == CalcType::Unknown) base = t;
    }
    return true;
}

bool eval(const CalcNode& n, const LengthContext& ctx, double* out, std::string* why) {
    switch (n.tag()) {
        case CalcNode::Tag::Length: {
            const auto& l = static_cast<const CalcLengthNode&>(n);
            CssLength tmp;
            tmp.value = l.value;
            tmp.unit = l.unit;
            if (!tmp.to_pixels(ctx, out)) {
                *why = "Cannot resolve percent length without a basis";
                return false;
            }
            return true;
        }
        case CalcNode::Tag::Number:
            *out = static_cast<const CalcNumberNode&>(n).value;
            return true;
        case CalcNode::Tag::Percentage: {
            if (!ctx.has_basis) {
                *why = "Cannot resolve percentage without a basis";
                return false;
            }
            *out = static_cast<const CalcPercentageNode&>(n).value * 0.01 * ctx.basis_pixels;
            return true;
        }
        case CalcNode::Tag::Angle:
            *out = static_cast<const CalcAngleNode&>(n).degrees;
            return true;

        case CalcNode::Tag::Binary: {
            const auto& b = static_cast<const CalcBinaryNode&>(n);
            CalcType lt = calc_classify(*b.left);
            CalcType rt = calc_classify(*b.right);
            double l = 0, r = 0;
            switch (b.op) {
                case CalcOp::Add:
                case CalcOp::Sub:
                    if (!calc_types_compatible(lt, rt)) {
                        *why = std::string("calc() '") + op_char(b.op) +
                               "' requires compatible operand types (got " +
                               type_name(lt) + " and " + type_name(rt) + ")";
                        return false;
                    }
                    if (!eval(*b.left, ctx, &l, why) || !eval(*b.right, ctx, &r, why)) return false;
                    *out = (b.op == CalcOp::Add) ? l + r : l - r;
                    return true;
                case CalcOp::Mul:
                    if (lt != CalcType::Number && rt != CalcType::Number) {
                        *why = std::string("calc() '*' requires at least one <number> operand (got ") +
                               type_name(lt) + " and " + type_name(rt) + ")";
                        return false;
                    }
                    if (!eval(*b.left, ctx, &l, why) || !eval(*b.right, ctx, &r, why)) return false;
                    *out = l * r;
                    return true;
                case CalcOp::Div:
                    if (rt != CalcType::Number) {
                        *why = std::string("calc() '/' denominator must be a <number> (got ") +
                               type_name(rt) + ")";
                        return false;
                    }
                    if (!eval(*b.left, ctx, &l, why) || !eval(*b.right, ctx, &r, why)) return false;
                    if (r == 0) { *why = "Division by zero in calc()"; return false; }
                    *out = l / r;
                    return true;
            }
            return false;
        }

        case CalcNode::Tag::Math: {
            const auto& m = static_cast<const CalcMathNode&>(n);
            switch (m.fn) {
                case CalcMathFn::Min: {
                    if (!require_compatible_args(m, "min", why)) return false;
                    double best = std::numeric_limits<double>::infinity();
                    for (const auto& a : m.args) {
                        double v = 0;
                        if (!eval(*a, ctx, &v, why)) return false;
                        if (v < best) best = v;
                    }
                    *out = best;
                    return true;
                }
                case CalcMathFn::Max: {
                    if (!require_compatible_args(m, "max", why)) return false;
                    double best = -std::numeric_limits<double>::infinity();
                    for (const auto& a : m.args) {
                        double v = 0;
                        if (!eval(*a, ctx, &v, why)) return false;
                        if (v > best) best = v;
                    }
                    *out = best;
                    return true;
                }
                case CalcMathFn::Clamp: {
                    if (m.args.size() != 3) {
                        *why = "clamp() requires exactly 3 arguments";
                        return false;
                    }
                    if (!require_compatible_args(m, "clamp", why)) return false;
                    double lo = 0, val = 0, hi = 0;
                    if (!eval(*m.args[0], ctx, &lo, why)) return false;
                    if (!eval(*m.args[1], ctx, &val, why)) return false;
                    if (!eval(*m.args[2], ctx, &hi, why)) return false;
                    // clamp(MIN, VAL, MAX) == max(MIN, min(VAL, MAX)).
                    // Note this is NOT symmetric when MIN > MAX: the spec makes
                    // MIN win, and this ordering reproduces that.
                    *out = std::fmax(lo, std::fmin(val, hi));
                    return true;
                }
            }
            return false;
        }
    }
    return false;
}

} // namespace

CalcType calc_classify(const CalcNode& n) {
    switch (n.tag()) {
        case CalcNode::Tag::Number:     return CalcType::Number;
        case CalcNode::Tag::Length:     return CalcType::Length;
        case CalcNode::Tag::Percentage: return CalcType::Percentage;
        case CalcNode::Tag::Angle:      return CalcType::Angle;
        case CalcNode::Tag::Binary: {
            const auto& b = static_cast<const CalcBinaryNode&>(n);
            CalcType lt = calc_classify(*b.left);
            CalcType rt = calc_classify(*b.right);
            switch (b.op) {
                case CalcOp::Add:
                case CalcOp::Sub:
                    // length +- percentage keeps the length type, so a later
                    // consumer still treats the result as a length.
                    if (lt == CalcType::Length && rt == CalcType::Percentage) return CalcType::Length;
                    if (lt == CalcType::Percentage && rt == CalcType::Length) return CalcType::Length;
                    return lt;
                case CalcOp::Mul:
                    return lt == CalcType::Number ? rt : lt;
                case CalcOp::Div:
                    return lt;
            }
            return CalcType::Unknown;
        }
        case CalcNode::Tag::Math: {
            const auto& m = static_cast<const CalcMathNode&>(n);
            if (!m.args.empty()) return calc_classify(*m.args[0]);
            return CalcType::Unknown;
        }
    }
    return CalcType::Unknown;
}

bool calc_types_compatible(CalcType a, CalcType b) {
    if (a == CalcType::Unknown || b == CalcType::Unknown) return true;
    if (a == b) return true;
    return (a == CalcType::Length && b == CalcType::Percentage) ||
           (a == CalcType::Percentage && b == CalcType::Length);
}

bool CssCalc::evaluate(const LengthContext& ctx, double* out, std::string* why) const {
    std::string sink;
    if (!why) why = &sink;
    if (!expression) { *why = "empty calc()"; return false; }
    return eval(*expression, ctx, out, why);
}

} // namespace weva
