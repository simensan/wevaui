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

    // ---- functions round-trip with parsed arguments
    {
        V v;
        CHECK(v.run("rgb(255, 128, 0)"));
        CHECK(v.v->kind() == CssValueKind::FunctionCall);
        auto* f = static_cast<CssFunctionCall*>(v.v.get());
        CHECK(f->name == "rgb" && f->arguments.size() == 3);
        CHECK(near(static_cast<CssNumber*>(f->arguments[1].get())->value, 128));

        CHECK(v.run("CALC(1px + 2em)"));
        f = static_cast<CssFunctionCall*>(v.v.get());
        CHECK(f->name == "calc");                   // name lowercased
        CHECK(f->arguments.size() == 1);            // one space-separated group
        CHECK(f->arguments[0]->kind() == CssValueKind::List);

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
