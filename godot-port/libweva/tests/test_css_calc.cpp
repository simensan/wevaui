#include "check.h"
#include "weva/css_calc.h"
#include "weva/css_value.h"
#include <cmath>
#include <string>

using namespace weva;

namespace {
bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

struct C {
    CssValuePtr v;
    CssParseError err;
    bool run(std::string_view s) {
        err = CssParseError{};
        v = parse_css_value(s, &err);
        return v && v->kind() == CssValueKind::Calc;
    }
    bool eval(double* out, const LengthContext& ctx) {
        why.clear();
        return static_cast<CssCalc*>(v.get())->evaluate(ctx, out, &why);
    }
    std::string why;
};
} // namespace

void test_css_calc() {
    LengthContext ctx;   // 16px font, 1920x1080, 96dpi

    // ---- arithmetic and precedence
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(1px + 2px)") && c.eval(&r, ctx) && near(r, 3));
        CHECK(c.run("calc(10px - 4px)") && c.eval(&r, ctx) && near(r, 6));
        CHECK(c.run("calc(2 * 8px)") && c.eval(&r, ctx) && near(r, 16));
        CHECK(c.run("calc(16px / 2)") && c.eval(&r, ctx) && near(r, 8));
        // * and / bind tighter than + and -
        CHECK(c.run("calc(1px + 2 * 3px)") && c.eval(&r, ctx) && near(r, 7));
        CHECK(c.run("calc((1px + 2px) * 3)") && c.eval(&r, ctx) && near(r, 9));
    }

    // ---- mixed units resolve through LengthContext
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(1em + 4px)") && c.eval(&r, ctx) && near(r, 20));
        CHECK(c.run("calc(10vw - 10px)") && c.eval(&r, ctx) && near(r, 182));
        CHECK(c.run("calc(2rem)") && c.eval(&r, ctx) && near(r, 32));
    }

    // ---- percentages need a basis, exactly like a bare percent length
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(50% + 10px)"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("basis") != std::string::npos);
        CHECK(c.eval(&r, ctx.with_basis(200)) && near(r, 110));
    }

    // ---- CSS Values 4 10.1: '+' and '-' REQUIRE surrounding whitespace,
    // because the tokenizer folds a leading sign into the number.
    {
        C c;
        CHECK(!c.run("calc(1px+2px)"));
        CHECK(c.err.message.find("whitespace") != std::string::npos);
        CHECK(!c.run("calc(1px -2px)"));       // reads as two dimensions
        CHECK(c.err.message.find("whitespace") != std::string::npos);
        CHECK(!c.run("calc(1px- 2px)"));
        // '*' and '/' have no such rule.
        double r = 0;
        CHECK(c.run("calc(2*8px)") && c.eval(&r, ctx) && near(r, 16));
        CHECK(c.run("calc(16px/2)") && c.eval(&r, ctx) && near(r, 8));
    }

    // ---- operand type rules
    {
        C c;
        double r = 0;
        // '+' needs compatible types; length+percentage is allowed and stays a length.
        CHECK(c.run("calc(1px + 2deg)"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("compatible operand types") != std::string::npos);
        // '*' needs at least one number.
        CHECK(c.run("calc(2px * 3px)"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("<number>") != std::string::npos);
        // '/' needs a number denominator.
        CHECK(c.run("calc(8px / 2px)"));
        CHECK(!c.eval(&r, ctx));
        // division by zero is reported, not inf/nan
        CHECK(c.run("calc(8px / 0)"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("Division by zero") != std::string::npos);
    }

    // ---- length + percentage classifies as length (so it can nest in a length slot)
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(10px + 50%)"));
        CHECK(c.eval(&r, ctx.with_basis(100)) && near(r, 60));
        CHECK(calc_types_compatible(CalcType::Length, CalcType::Percentage));
        CHECK(!calc_types_compatible(CalcType::Length, CalcType::Angle));
        // Unknown is compatible with everything, matching the C#.
        CHECK(calc_types_compatible(CalcType::Unknown, CalcType::Angle));
    }

    // ---- min / max / clamp
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(min(10px, 4px, 7px))") && c.eval(&r, ctx) && near(r, 4));
        CHECK(c.run("calc(max(10px, 4px))") && c.eval(&r, ctx) && near(r, 10));
        CHECK(c.run("calc(clamp(2px, 9px, 5px))") && c.eval(&r, ctx) && near(r, 5));
        CHECK(c.run("calc(clamp(2px, 1px, 5px))") && c.eval(&r, ctx) && near(r, 2));
        // clamp is max(MIN, min(VAL, MAX)) — when MIN > MAX, MIN wins.
        CHECK(c.run("calc(clamp(10px, 5px, 2px))") && c.eval(&r, ctx) && near(r, 10));
        // arity and type checks
        CHECK(c.run("calc(clamp(1px, 2px))"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("exactly 3") != std::string::npos);
        CHECK(c.run("calc(min(1px, 2deg))"));
        CHECK(!c.eval(&r, ctx));
        CHECK(c.why.find("share a type") != std::string::npos);
    }

    // ---- nesting
    {
        C c;
        double r = 0;
        CHECK(c.run("calc(calc(1px + 1px) * 2)") && c.eval(&r, ctx) && near(r, 4));
        CHECK(c.run("calc(min(100px, max(10px, 2em)))") && c.eval(&r, ctx) && near(r, 32));
    }

    // ---- unsupported math functions are REJECTED, not silently mis-evaluated
    {
        C c;
        CHECK(!c.run("calc(round(1.5px))"));
        CHECK(c.err.message.find("not supported yet") != std::string::npos);
        CHECK(!c.run("calc(sin(45deg))"));
        CHECK(!c.run("calc(pow(2, 3))"));
    }

    // ---- hostile nesting terminates rather than overflowing the stack
    {
        std::string deep = "calc(";
        for (int i = 0; i < 500; ++i) deep += "(";
        deep += "1px";
        for (int i = 0; i < 500; ++i) deep += ")";
        deep += ")";
        C c;
        c.run(deep);   // must return, pass or fail
        CHECK(true);
    }
}
