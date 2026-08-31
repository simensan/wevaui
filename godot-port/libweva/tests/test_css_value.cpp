#include "check.h"
#include "weva/css_value.h"
#include <cmath>
#include <string>

using namespace weva;

namespace {
bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }

struct V {
    CssValuePtr v;
    CssParseError err;
    bool run(std::string_view s) {
        err = CssParseError{};
        v = parse_css_value(s, &err);
        return static_cast<bool>(v);
    }
};
} // namespace

void test_css_value() {
    // ---- primitives
    {
        V v;
        CHECK(v.run("16px"));
        CHECK(v.v->kind() == CssValueKind::Length);
        auto* l = static_cast<CssLength*>(v.v.get());
        CHECK(near(l->value, 16) && l->unit == CssLengthUnit::Px);
        CHECK(l->raw == "16px");

        CHECK(v.run("1.5"));
        CHECK(v.v->kind() == CssValueKind::Number);
        CHECK(near(static_cast<CssNumber*>(v.v.get())->value, 1.5));

        CHECK(v.run("50%"));
        CHECK(v.v->kind() == CssValueKind::Percentage);
        CHECK(near(static_cast<CssPercentage*>(v.v.get())->value, 50));

        CHECK(v.run("auto"));
        CHECK(v.v->kind() == CssValueKind::Keyword);
        CHECK(static_cast<CssKeyword*>(v.v.get())->name == "auto");
    }

    // ---- angles are their own type, not lengths (CSS Values 4 6.1)
    {
        V v;
        CHECK(v.run("45deg"));
        CHECK(v.v->kind() == CssValueKind::Angle);
        CHECK(near(static_cast<CssAngle*>(v.v.get())->to_degrees(), 45));

        CHECK(v.run("0.5turn"));
        CHECK(near(static_cast<CssAngle*>(v.v.get())->to_degrees(), 180));

        CHECK(v.run("100grad"));
        CHECK(near(static_cast<CssAngle*>(v.v.get())->to_degrees(), 90));

        CHECK(v.run("1rad"));
        CHECK(std::fabs(static_cast<CssAngle*>(v.v.get())->to_degrees() - 57.29577951308232) < 1e-9);
    }

    // ---- signed values, exercising the from_chars '+' fix end to end
    {
        V v;
        CHECK(v.run("+5px"));
        CHECK(near(static_cast<CssLength*>(v.v.get())->value, 5));
        CHECK(v.run("-2.5em"));
        CHECK(near(static_cast<CssLength*>(v.v.get())->value, -2.5));
    }

    // ---- an unknown unit is a parse failure, not a silent zero
    {
        V v;
        CHECK(!v.run("10furlong"));
        CHECK(v.err.message.find("Unknown length unit") != std::string::npos);
    }

    // ---- hex colours: 3, 4, 6, 8 digits
    {
        V v;
        CHECK(v.run("#f00"));
        auto* c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 255 && c->g == 0 && c->b == 0 && c->a == 1.0f);
        CHECK(c->raw == "#f00");                    // lowercased, '#' restored

        CHECK(v.run("#ABCDEF"));
        c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 0xAB && c->g == 0xCD && c->b == 0xEF);
        CHECK(c->raw == "#abcdef");

        CHECK(v.run("#0000"));                      // 4-digit, alpha 0
        CHECK(static_cast<CssColor*>(v.v.get())->a == 0.0f);

        CHECK(v.run("#11223344"));
        c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 0x11 && c->b == 0x33);
        CHECK(std::fabs(c->a - 0x44 / 255.0f) < 1e-6);

        CHECK(!v.run("#ff"));                       // 2 digits is invalid
        CHECK(!v.run("#12345"));                    // 5 digits is invalid
    }

    // ---- space and comma lists
    {
        V v;
        CHECK(v.run("0 auto"));
        CHECK(v.v->kind() == CssValueKind::List);
        auto* l = static_cast<CssValueList*>(v.v.get());
        CHECK(l->separator == CssListSeparator::Space && l->items.size() == 2);

        CHECK(v.run("red, blue"));
        l = static_cast<CssValueList*>(v.v.get());
        CHECK(l->separator == CssListSeparator::Comma && l->items.size() == 2);

        // A comma list whose segments hold several values nests space lists.
        CHECK(v.run("1px 2px, 3px 4px"));
        l = static_cast<CssValueList*>(v.v.get());
        CHECK(l->separator == CssListSeparator::Comma && l->items.size() == 2);
        CHECK(l->items[0]->kind() == CssValueKind::List);
    }

    // ---- functions round-trip with parsed arguments.
    // (rgb()/hsl()/hwb() are the exception — they collapse to CssColor during
    // parsing; see test_css_color.)
    {
        V v;
        CHECK(v.run("translate(1px, 2px)"));
        CHECK(v.v->kind() == CssValueKind::FunctionCall);
        auto* f = static_cast<CssFunctionCall*>(v.v.get());
        CHECK(f->name == "translate" && f->arguments.size() == 2);
        CHECK(near(static_cast<CssLength*>(f->arguments[1].get())->value, 2));

        // calc() is recognised case-insensitively and becomes CssCalc, not a
        // generic call — see test_css_calc.
        CHECK(v.run("CALC(1px + 2em)"));
        CHECK(v.v->kind() == CssValueKind::Calc);

        CHECK(v.run("var(--x, 4px)"));
        f = static_cast<CssFunctionCall*>(v.v.get());
        CHECK(f->name == "var" && f->arguments.size() == 2);
    }

    // ---- strings and urls
    {
        V v;
        CHECK(v.run("\"hello\""));
        CHECK(v.v->kind() == CssValueKind::String);
        CHECK(static_cast<CssString*>(v.v.get())->text == "hello");

        CHECK(v.run("url(a/b.png)"));
        CHECK(v.v->kind() == CssValueKind::Url);
        CHECK(static_cast<CssUrl*>(v.v.get())->url == "a/b.png");
    }

    // ---- empty and comma-adjacent failures
    {
        V v;
        CHECK(!v.run(""));
        CHECK(v.err.message == "Empty value");
        CHECK(!v.run("red,,blue"));
        CHECK(v.err.message == "Empty value between commas");
    }

    // ---- length -> pixels across every unit family
    {
        LengthContext ctx;   // defaults: 16px font, 1920x1080, 96dpi
        auto px = [&](double val, CssLengthUnit u, const LengthContext& c) {
            CssLength l; l.value = val; l.unit = u;
            double out = 0;
            bool ok = l.to_pixels(c, &out);
            return ok ? out : -12345.0;
        };
        CHECK(near(px(10, CssLengthUnit::Px, ctx), 10));
        CHECK(near(px(2, CssLengthUnit::Em, ctx), 32));
        CHECK(near(px(2, CssLengthUnit::Rem, ctx), 32));
        CHECK(near(px(10, CssLengthUnit::Vw, ctx), 192));
        CHECK(near(px(10, CssLengthUnit::Vh, ctx), 108));
        CHECK(near(px(10, CssLengthUnit::Vmin, ctx), 108));
        CHECK(near(px(10, CssLengthUnit::Vmax, ctx), 192));
        CHECK(near(px(1, CssLengthUnit::In, ctx), 96));
        CHECK(near(px(72, CssLengthUnit::Pt, ctx), 96));
        CHECK(near(px(1, CssLengthUnit::Pc, ctx), 16));
        CHECK(near(px(2.54, CssLengthUnit::Cm, ctx), 96));
        CHECK(near(px(25.4, CssLengthUnit::Mm, ctx), 96));
        // Font-metric approximations, copied from the C# rather than corrected.
        CHECK(near(px(1, CssLengthUnit::Ch, ctx), 8));
        CHECK(near(px(1, CssLengthUnit::Ex, ctx), 8));
        CHECK(near(px(1, CssLengthUnit::Cap, ctx), 11.2));
        CHECK(near(px(1, CssLengthUnit::Ic, ctx), 16));
        // lh falls back to 1.2x font size when no line height is set.
        CHECK(near(px(1, CssLengthUnit::Lh, ctx), 19.2));
        LengthContext lh = ctx; lh.line_height_px = 24;
        CHECK(near(px(1, CssLengthUnit::Lh, lh), 24));
        // The small/large/dynamic viewport units all collapse to vw/vh.
        CHECK(near(px(10, CssLengthUnit::Svw, ctx), 192));
        CHECK(near(px(10, CssLengthUnit::Dvh, ctx), 108));
        // A zero or negative DPI falls back to 96 rather than dividing by zero.
        LengthContext bad = ctx; bad.dpi_pixels_per_inch = 0;
        CHECK(near(px(1, CssLengthUnit::In, bad), 96));
    }

    // ---- percent needs a basis; C# throws, we return false
    {
        CssLength l; l.value = 50; l.unit = CssLengthUnit::Percent;
        LengthContext ctx;
        double out = 0;
        CHECK(!l.to_pixels(ctx, &out));
        CHECK(l.to_pixels(ctx.with_basis(200), &out));
        CHECK(near(out, 100));
    }

    // ---- unit table round-trips
    {
        CssLengthUnit u;
        CHECK(css_length_unit_from_string("rem", &u) && u == CssLengthUnit::Rem);
        CHECK(!css_length_unit_from_string("nope", &u));
        // '%' is not in the table: the tokenizer emits a Percentage token, so
        // CssLengthUnit::Percent is only reachable by direct construction.
        CHECK(!css_length_unit_from_string("%", &u));
        CHECK(std::string(css_length_unit_suffix(CssLengthUnit::Percent)) == "%");
    }
}

void test_css_color() {
    // ---- named colours, case-insensitive, including system colours whose
    // table entries carry authored casing ("AccentColor").
    {
        CssColor c;
        CHECK(css_color_from_name("red", &c) && c.r == 255 && c.g == 0 && c.b == 0);
        CHECK(css_color_from_name("RED", &c) && c.r == 255);
        CHECK(css_color_from_name("rebeccapurple", &c) && c.r == 102 && c.g == 51 && c.b == 153);
        CHECK(css_color_from_name("AccentColor", &c) && c.r == 0 && c.g == 102 && c.b == 204);
        CHECK(css_color_from_name("accentcolor", &c) && c.b == 204);
        // `transparent` is the one entry with a non-1 alpha.
        CHECK(css_color_from_name("transparent", &c) && c.a == 0.0f);
        // aqua and cyan are aliases.
        CssColor aqua, cyan;
        CHECK(css_color_from_name("aqua", &aqua) && css_color_from_name("cyan", &cyan));
        CHECK(aqua.r == cyan.r && aqua.g == cyan.g && aqua.b == cyan.b);
        CHECK(!css_color_from_name("notacolour", &c));
    }

    // ---- channel rounding is HALF-TO-EVEN, matching C#'s parameterless
    // Math.Round. std::round would give 1/2/3 here instead of 0/2/2, putting
    // every midpoint channel one off. This is the OPPOSITE convention to the
    // layout dump's away-from-zero Round2 — both live in the same C# codebase.
    {
        CssColor c;
        css_color_from_rgb(0.5, 1.5, 2.5, 1.0, false, &c);
        CHECK(c.r == 0);
        CHECK(c.g == 2);
        CHECK(c.b == 2);
    }

    // ---- channels clamp rather than wrap
    {
        CssColor c;
        css_color_from_rgb(-20, 300, 128, 5.0, false, &c);
        CHECK(c.r == 0 && c.g == 255 && c.b == 128);
        CHECK(c.a == 1.0f);              // alpha clamps to 0..1
        css_color_from_rgb(0, 0, 0, -1, false, &c);
        CHECK(c.a == 0.0f);
    }

    // ---- percentage channels scale by 2.55
    {
        CssColor c;
        css_color_from_rgb(100, 50, 0, 1.0, true, &c);
        CHECK(c.r == 255);
        // 50 * 2.55 is 127.49999999999998, NOT 127.5 — 2.55 has no exact double
        // representation, so this is below the midpoint and rounds down under
        // either mode. C# does the identical multiply and also yields 127.
        // Do not "correct" this to 128.
        CHECK(c.g == 127);
        CHECK(c.b == 0);
    }

    // ---- and a genuine midpoint, to actually exercise half-to-even
    {
        CssColor c;
        css_color_from_rgb(127.5, 128.5, 0.5, 1.0, false, &c);
        CHECK(c.r == 128);   // 127.5 -> even -> 128
        CHECK(c.g == 128);   // 128.5 -> even -> 128 (away-from-zero would give 129)
        CHECK(c.b == 0);     // 0.5   -> even -> 0
    }

    // ---- hsl basics and hue wrapping
    {
        CssColor c;
        css_color_from_hsl(0, 100, 50, 1.0, &c);
        CHECK(c.r == 255 && c.g == 0 && c.b == 0);
        css_color_from_hsl(120, 100, 50, 1.0, &c);
        CHECK(c.r == 0 && c.g == 255 && c.b == 0);
        css_color_from_hsl(-240, 100, 50, 1.0, &c);   // wraps to 120
        CHECK(c.r == 0 && c.g == 255 && c.b == 0);
        css_color_from_hsl(480, 100, 50, 1.0, &c);    // wraps to 120
        CHECK(c.g == 255);
        css_color_from_hsl(0, 0, 50, 1.0, &c);        // achromatic
        CHECK(c.r == c.g && c.g == c.b);
    }

    // ---- hwb, including the w+b >= 1 grey collapse (CSS Color 4 10)
    {
        CssColor c;
        css_color_from_hwb(0, 0, 0, 1.0, &c);
        CHECK(c.r == 255 && c.g == 0 && c.b == 0);
        css_color_from_hwb(0, 60, 60, 1.0, &c);       // w+b > 1 -> grey at w/(w+b)
        CHECK(c.r == c.g && c.g == c.b);
        CHECK(c.r == 128);                            // 0.5*255 = 127.5 -> even -> 128
    }

    // ---- colour functions collapse to CssColor during parsing
    {
        V v;
        CHECK(v.run("rgb(255, 128, 0)"));
        CHECK(v.v->kind() == CssValueKind::Color);
        auto* c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 255 && c->g == 128 && c->b == 0 && c->a == 1.0f);

        CHECK(v.run("rgba(0, 0, 0, 0.5)"));
        CHECK(std::fabs(static_cast<CssColor*>(v.v.get())->a - 0.5f) < 1e-6);

        CHECK(v.run("hsl(120, 100%, 50%)"));
        c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 0 && c->g == 255 && c->b == 0);

        // An alpha PERCENTAGE is 0-100 where a number is 0-1.
        CHECK(v.run("rgba(0,0,0,50%)"));
        CHECK(std::fabs(static_cast<CssColor*>(v.v.get())->a - 0.5f) < 1e-6);

        // A hue given as an angle is normalised to degrees.
        CHECK(v.run("hsl(0.5turn, 100%, 50%)"));
        c = static_cast<CssColor*>(v.v.get());
        CHECK(c->r == 0 && c->g == 255 && c->b == 255);   // 180deg = cyan
    }

    // ---- a bare ident that names a colour becomes a colour, not a keyword
    {
        V v;
        CHECK(v.run("red"));
        CHECK(v.v->kind() == CssValueKind::Color);
        // ...but currentcolor stays a keyword: it resolves in the cascade.
        CHECK(v.run("currentcolor"));
        CHECK(v.v->kind() == CssValueKind::Keyword);
        CHECK(v.run("auto"));
        CHECK(v.v->kind() == CssValueKind::Keyword);
    }

    // ---- a colour function whose arguments can't be evaluated stays a call
    {
        V v;
        CHECK(v.run("rgb(var(--r), 0, 0)"));
        CHECK(v.v->kind() == CssValueKind::FunctionCall);
        CHECK(v.run("rgb(1,2)"));                      // wrong arity
        CHECK(v.v->kind() == CssValueKind::FunctionCall);
        CHECK(v.run("color-mix(in srgb, red, blue)")); // not a colour function here
        CHECK(v.v->kind() == CssValueKind::FunctionCall);
    }
}
