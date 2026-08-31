#pragma once
#include "weva/css_value.h"

#include <memory>
#include <string>
#include <vector>

// Ports Runtime/Css/Values/CssCalc.cs and the ParseCalc path of
// CssValueParser.cs.
//
// Scope: the node model, type classification, +-*/ with CSS's operand-type
// rules, and min()/max()/clamp(). Deferred — and rejected at parse time rather
// than silently mis-evaluated: round/mod/rem/pow/sqrt/log/exp/sign/hypot, the
// trig family, var() references (need the cascade) and relative-colour channel
// idents (need the colour parser).

namespace weva {

enum class CalcOp { Add, Sub, Mul, Div };
enum class CalcType { Unknown, Number, Length, Percentage, Angle };
enum class CalcMathFn { Min, Max, Clamp };

struct CalcNode;
using CalcNodePtr = std::unique_ptr<CalcNode>;

struct CalcNode {
    enum class Tag { Length, Number, Percentage, Angle, Binary, Math };
    virtual ~CalcNode() = default;
    virtual Tag tag() const = 0;
};

struct CalcLengthNode : CalcNode {
    Tag tag() const override { return Tag::Length; }
    double value = 0;
    CssLengthUnit unit = CssLengthUnit::Px;
};

struct CalcNumberNode : CalcNode {
    Tag tag() const override { return Tag::Number; }
    double value = 0;
};

struct CalcPercentageNode : CalcNode {
    Tag tag() const override { return Tag::Percentage; }
    double value = 0;
};

struct CalcAngleNode : CalcNode {
    Tag tag() const override { return Tag::Angle; }
    double degrees = 0;
};

struct CalcBinaryNode : CalcNode {
    Tag tag() const override { return Tag::Binary; }
    CalcOp op = CalcOp::Add;
    CalcNodePtr left, right;
};

struct CalcMathNode : CalcNode {
    Tag tag() const override { return Tag::Math; }
    CalcMathFn fn = CalcMathFn::Min;
    std::vector<CalcNodePtr> args;
};

struct CssCalc : CssValue {
    CssValueKind kind() const override { return CssValueKind::Calc; }
    CalcNodePtr expression;

    // Returns false when the expression cannot be resolved in this context —
    // a percentage with no basis, a division by zero, incompatible operand
    // types. C# throws InvalidOperationException for each.
    bool evaluate(const LengthContext& ctx, double* out, std::string* why) const;
};

CalcType calc_classify(const CalcNode& n);
bool calc_types_compatible(CalcType a, CalcType b);

} // namespace weva
