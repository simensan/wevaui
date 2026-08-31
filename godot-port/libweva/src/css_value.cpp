#include "weva/css_calc.h"
#include "weva/css_value.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

std::string ascii_lower(std::string_view s) {
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

bool is_hex(char c) {
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

int hex_val(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
    return 10 + (c - 'A');
}

} // namespace

bool css_length_unit_from_string(std::string_view u, CssLengthUnit* out) {
    struct E { const char* n; CssLengthUnit v; };
    static const E table[] = {
        {"px", CssLengthUnit::Px},   {"em", CssLengthUnit::Em},
        {"rem", CssLengthUnit::Rem}, {"vh", CssLengthUnit::Vh},
        {"vw", CssLengthUnit::Vw},   {"vmin", CssLengthUnit::Vmin},
        {"vmax", CssLengthUnit::Vmax}, {"pt", CssLengthUnit::Pt},
        {"pc", CssLengthUnit::Pc},   {"in", CssLengthUnit::In},
        {"cm", CssLengthUnit::Cm},   {"mm", CssLengthUnit::Mm},
        {"ch", CssLengthUnit::Ch},   {"ex", CssLengthUnit::Ex},
        {"cap", CssLengthUnit::Cap}, {"ic", CssLengthUnit::Ic},
        {"lh", CssLengthUnit::Lh},   {"rlh", CssLengthUnit::Rlh},
        {"svw", CssLengthUnit::Svw}, {"lvw", CssLengthUnit::Lvw},
        {"dvw", CssLengthUnit::Dvw}, {"svh", CssLengthUnit::Svh},
        {"lvh", CssLengthUnit::Lvh}, {"dvh", CssLengthUnit::Dvh},
    };
    for (const E& e : table) {
        if (u == e.n) { *out = e.v; return true; }
    }
    // Note: "%" is deliberately absent. The tokenizer emits a Percentage token
    // for it, so CssLengthUnit::Percent is only ever reachable from code that
    // constructs one directly — matching the C#.
    *out = CssLengthUnit::Px;
    return false;
}

bool css_angle_unit_from_string(std::string_view u, CssAngleUnit* out) {
    if (u == "deg")  { *out = CssAngleUnit::Deg;  return true; }
    if (u == "rad")  { *out = CssAngleUnit::Rad;  return true; }
    if (u == "grad") { *out = CssAngleUnit::Grad; return true; }
    if (u == "turn") { *out = CssAngleUnit::Turn; return true; }
    *out = CssAngleUnit::Deg;
    return false;
}

const char* css_length_unit_suffix(CssLengthUnit u) {
    switch (u) {
        case CssLengthUnit::Px: return "px";   case CssLengthUnit::Em: return "em";
        case CssLengthUnit::Rem: return "rem"; case CssLengthUnit::Percent: return "%";
        case CssLengthUnit::Vh: return "vh";   case CssLengthUnit::Vw: return "vw";
        case CssLengthUnit::Vmin: return "vmin"; case CssLengthUnit::Vmax: return "vmax";
        case CssLengthUnit::Pt: return "pt";   case CssLengthUnit::Pc: return "pc";
        case CssLengthUnit::In: return "in";   case CssLengthUnit::Cm: return "cm";
        case CssLengthUnit::Mm: return "mm";   case CssLengthUnit::Ch: return "ch";
        case CssLengthUnit::Ex: return "ex";   case CssLengthUnit::Cap: return "cap";
        case CssLengthUnit::Ic: return "ic";   case CssLengthUnit::Lh: return "lh";
        case CssLengthUnit::Rlh: return "rlh"; case CssLengthUnit::Svw: return "svw";
        case CssLengthUnit::Lvw: return "lvw"; case CssLengthUnit::Dvw: return "dvw";
        case CssLengthUnit::Svh: return "svh"; case CssLengthUnit::Lvh: return "lvh";
        case CssLengthUnit::Dvh: return "dvh";
    }
    return "";
}

bool CssLength::to_pixels(const LengthContext& ctx, double* out) const {
    const double dpi = ctx.dpi_pixels_per_inch <= 0 ? 96.0 : ctx.dpi_pixels_per_inch;
    switch (unit) {
        case CssLengthUnit::Px:  *out = value; return true;
        case CssLengthUnit::Em:  *out = value * ctx.base_font_size_px; return true;
        case CssLengthUnit::Rem: *out = value * ctx.root_font_size_px; return true;
        case CssLengthUnit::Percent:
            if (!ctx.has_basis) return false;   // C# throws InvalidOperationException
            *out = value * 0.01 * ctx.basis_pixels;
            return true;
        case CssLengthUnit::Vh: *out = value * 0.01 * ctx.viewport_height_px; return true;
        case CssLengthUnit::Vw: *out = value * 0.01 * ctx.viewport_width_px; return true;
        case CssLengthUnit::Vmin:
            *out = value * 0.01 * std::fmin(ctx.viewport_width_px, ctx.viewport_height_px);
            return true;
        case CssLengthUnit::Vmax:
            *out = value * 0.01 * std::fmax(ctx.viewport_width_px, ctx.viewport_height_px);
            return true;
        case CssLengthUnit::Pt: *out = value * (dpi / 72.0); return true;
        case CssLengthUnit::Pc: *out = value * 12.0 * (dpi / 72.0); return true;
        case CssLengthUnit::In: *out = value * dpi; return true;
        case CssLengthUnit::Cm: *out = value * (dpi / 2.54); return true;
        case CssLengthUnit::Mm: *out = value * (dpi / 25.4); return true;
        // ch/ex/cap/ic are font-metric approximations in the C#, not real
        // metrics. Reproduced exactly so the oracle matches; revisit only when
        // the text stack can supply true values, and then in both engines.
        case CssLengthUnit::Ch:  *out = value * 0.5 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Ex:  *out = value * 0.5 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Cap: *out = value * 0.7 * ctx.base_font_size_px; return true;
        case CssLengthUnit::Ic:  *out = value * ctx.base_font_size_px; return true;
        case CssLengthUnit::Lh:
            *out = value * (ctx.line_height_px > 0 ? ctx.line_height_px
                                                   : ctx.base_font_size_px * 1.2);
            return true;
        case CssLengthUnit::Rlh:
            *out = value * (ctx.root_line_height_px > 0 ? ctx.root_line_height_px
                                                        : ctx.root_font_size_px * 1.2);
            return true;
        case CssLengthUnit::Svw:
        case CssLengthUnit::Lvw:
        case CssLengthUnit::Dvw: *out = value * 0.01 * ctx.viewport_width_px; return true;
        case CssLengthUnit::Svh:
        case CssLengthUnit::Lvh:
        case CssLengthUnit::Dvh: *out = value * 0.01 * ctx.viewport_height_px; return true;
    }
    *out = value;
    return true;
}

double CssAngle::to_degrees() const {
    switch (unit) {
        case CssAngleUnit::Deg:  return value;
        case CssAngleUnit::Rad:  return value * 180.0 / 3.14159265358979323846;
        case CssAngleUnit::Grad: return value * 0.9;
        case CssAngleUnit::Turn: return value * 360.0;
    }
    return value;
}

bool css_color_from_hex(std::string_view body, CssColor* out) {
    if (body.empty()) return false;
    for (char c : body) {
        if (!is_hex(c)) return false;
    }
    int r, g, b;
    float a = 1.0f;
    switch (body.size()) {
        case 3:
            r = hex_val(body[0]) * 17; g = hex_val(body[1]) * 17; b = hex_val(body[2]) * 17;
            break;
        case 4:
            r = hex_val(body[0]) * 17; g = hex_val(body[1]) * 17; b = hex_val(body[2]) * 17;
            a = static_cast<float>(hex_val(body[3]) * 17) / 255.0f;
            break;
        case 6:
            r = (hex_val(body[0]) << 4) | hex_val(body[1]);
            g = (hex_val(body[2]) << 4) | hex_val(body[3]);
            b = (hex_val(body[4]) << 4) | hex_val(body[5]);
            break;
        case 8:
            r = (hex_val(body[0]) << 4) | hex_val(body[1]);
            g = (hex_val(body[2]) << 4) | hex_val(body[3]);
            b = (hex_val(body[4]) << 4) | hex_val(body[5]);
            a = static_cast<float>((hex_val(body[6]) << 4) | hex_val(body[7])) / 255.0f;
            break;
        default:
            return false;
    }
    out->r = static_cast<uint8_t>(r);
    out->g = static_cast<uint8_t>(g);
    out->b = static_cast<uint8_t>(b);
    out->a = a;
    out->raw = "#" + ascii_lower(body);
    return true;
}

// ---------------------------------------------------------------- parsing

namespace {

struct Reader {
    const std::vector<CssToken>* toks;
    std::size_t i = 0;
    CssParseError* error;
    bool failed = false;

    const CssToken& peek() const { return (*toks)[i]; }
    void advance() { if (i + 1 < toks->size()) ++i; }
    bool at_end() const { return peek().kind == CssTokenKind::Eof; }
    void skip_ws() {
        while (peek().kind == CssTokenKind::Whitespace && i + 1 < toks->size()) ++i;
    }
    bool ws_ahead() const { return peek().kind == CssTokenKind::Whitespace; }
    std::nullptr_t fail(std::string_view msg, const CssToken& at) {
        failed = true;
        if (error) *error = CssParseError{std::string(msg), at.line, at.column};
        return nullptr;
    }
};

CssValuePtr parse_single(Reader& r);
CalcNodePtr parse_calc_expression(Reader& r, int depth);

// Reads a colour-function argument as a number. Percentages report themselves
// so rgb() can switch to its 0-100 channel scale, and angles are normalised to
// degrees for the hue slot.
bool arg_number(const CssValue& v, double* out, bool* is_percent) {
    *is_percent = false;
    switch (v.kind()) {
        case CssValueKind::Number:
            *out = static_cast<const CssNumber&>(v).value;
            return true;
        case CssValueKind::Percentage:
            *out = static_cast<const CssPercentage&>(v).value;
            *is_percent = true;
            return true;
        case CssValueKind::Angle:
            *out = static_cast<const CssAngle&>(v).to_degrees();
            return true;
        default:
            return false;
    }
}

// rgb()/rgba()/hsl()/hsla()/hwb() collapse to a CssColor. Anything else — and
// any of these whose arguments don't evaluate (a var() or calc() inside) —
// stays a CssFunctionCall for a later pass to resolve.
CssValuePtr eval_colour_function(const CssFunctionCall& call) {
    const std::string& n = call.name;
    bool is_rgb = (n == "rgb" || n == "rgba");
    bool is_hsl = (n == "hsl" || n == "hsla");
    bool is_hwb = (n == "hwb");
    if (!is_rgb && !is_hsl && !is_hwb) return nullptr;
    if (call.arguments.size() < 3 || call.arguments.size() > 4) return nullptr;

    double c[3];
    bool pct[3];
    for (int i = 0; i < 3; ++i) {
        if (!arg_number(*call.arguments[static_cast<std::size_t>(i)], &c[i], &pct[i])) {
            return nullptr;
        }
    }
    double alpha = 1.0;
    if (call.arguments.size() == 4) {
        bool apct = false;
        if (!arg_number(*call.arguments[3], &alpha, &apct)) return nullptr;
        // CSS Color 4: an alpha percentage is 0-100, a number is 0-1.
        if (apct) alpha /= 100.0;
    }

    auto out = std::make_unique<CssColor>();
    if (is_rgb) {
        // C#'s FromRgb takes ONE rgbPercent flag for all three channels, so a
        // mixed `rgb(255, 50%, 0)` follows the first channel. Reproduced.
        css_color_from_rgb(c[0], c[1], c[2], alpha, pct[0], out.get());
    } else if (is_hsl) {
        css_color_from_hsl(c[0], c[1], c[2], alpha, out.get());
    } else {
        css_color_from_hwb(c[0], c[1], c[2], alpha, out.get());
    }
    out->raw = call.raw;
    return out;
}


// --- calc() -------------------------------------------------------------------

// Real sheets nest a couple of levels; the cap stops hostile input from
// recursing the parser off the stack, matching the C#'s MaxCalcDepth.
constexpr int kMaxCalcDepth = 64;

CalcNodePtr calc_from_value(const CssValue& v) {
    switch (v.kind()) {
        case CssValueKind::Length: {
            auto n = std::make_unique<CalcLengthNode>();
            n->value = static_cast<const CssLength&>(v).value;
            n->unit = static_cast<const CssLength&>(v).unit;
            return n;
        }
        case CssValueKind::Number: {
            auto n = std::make_unique<CalcNumberNode>();
            n->value = static_cast<const CssNumber&>(v).value;
            return n;
        }
        case CssValueKind::Percentage: {
            auto n = std::make_unique<CalcPercentageNode>();
            n->value = static_cast<const CssPercentage&>(v).value;
            return n;
        }
        case CssValueKind::Angle: {
            auto n = std::make_unique<CalcAngleNode>();
            n->degrees = static_cast<const CssAngle&>(v).to_degrees();
            return n;
        }
        default:
            return nullptr;
    }
}

CalcNodePtr parse_calc_factor(Reader& r, int depth) {
    r.skip_ws();
    const CssToken t = r.peek();

    if (t.kind == CssTokenKind::LParen) {
        r.advance();
        CalcNodePtr inner = parse_calc_expression(r, depth + 1);
        if (!inner) return nullptr;
        r.skip_ws();
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        return inner;
    }

    if (t.kind == CssTokenKind::Function) {
        std::string fn = ascii_lower(t.text);
        if (fn == "calc") {
            r.advance();
            CalcNodePtr inner = parse_calc_expression(r, depth + 1);
            if (!inner) return nullptr;
            r.skip_ws();
            if (r.peek().kind == CssTokenKind::RParen) r.advance();
            return inner;
        }
        CalcMathFn mf;
        if (fn == "min") mf = CalcMathFn::Min;
        else if (fn == "max") mf = CalcMathFn::Max;
        else if (fn == "clamp") mf = CalcMathFn::Clamp;
        else {
            // Deferred math functions are REJECTED rather than treated as an
            // opaque value: silently mis-evaluating round() or sin() would be
            // far worse than refusing the declaration.
            r.fail("calc() function '" + fn + "' is not supported yet", t);
            return nullptr;
        }
        r.advance();
        auto m = std::make_unique<CalcMathNode>();
        m->fn = mf;
        r.skip_ws();
        while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
            CalcNodePtr a = parse_calc_expression(r, depth + 1);
            if (!a) return nullptr;
            m->args.push_back(std::move(a));
            r.skip_ws();
            if (r.peek().kind == CssTokenKind::Comma) { r.advance(); r.skip_ws(); }
        }
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        if (m->args.empty()) {
            r.fail("calc() math function needs at least one argument", t);
            return nullptr;
        }
        return m;
    }

    CssValuePtr v = parse_single(r);
    if (!v) return nullptr;
    CalcNodePtr n = calc_from_value(*v);
    if (!n) {
        r.fail("calc() operand must be a number, length, percentage or angle", t);
        return nullptr;
    }
    return n;
}

CalcNodePtr parse_calc_term(Reader& r, int depth) {
    CalcNodePtr left = parse_calc_factor(r, depth);
    if (!left) return nullptr;
    for (;;) {
        std::size_t saved = r.i;
        r.skip_ws();
        const CssToken t = r.peek();
        if (t.kind != CssTokenKind::Delim || (t.text != "*" && t.text != "/")) {
            r.i = saved;
            break;
        }
        r.advance();
        CalcNodePtr right = parse_calc_factor(r, depth);
        if (!right) return nullptr;
        auto b = std::make_unique<CalcBinaryNode>();
        b->op = (t.text == "*") ? CalcOp::Mul : CalcOp::Div;
        b->left = std::move(left);
        b->right = std::move(right);
        left = std::move(b);
    }
    return left;
}

CalcNodePtr parse_calc_expression(Reader& r, int depth) {
    if (depth > kMaxCalcDepth) {
        r.fail("calc() nesting too deep", r.peek());
        return nullptr;
    }
    CalcNodePtr left = parse_calc_term(r, depth);
    if (!left) return nullptr;

    for (;;) {
        bool ws_before = r.ws_ahead();
        r.skip_ws();
        if (r.at_end()) break;
        const CssToken t = r.peek();

        // CSS Values 4 §10.1: '+' and '-' MUST be surrounded by whitespace,
        // because the tokenizer folds a leading sign into the number. So
        // `calc(1px+2px)` arrives as Dimension("1px") then Dimension("+2px"),
        // and `calc(1px -2px)` as Dimension("1px"), ws, Dimension("-2px") —
        // both are errors, not additions.
        if (t.kind == CssTokenKind::Dimension || t.kind == CssTokenKind::Number ||
            t.kind == CssTokenKind::Percentage) {
            if (!t.text.empty() && (t.text[0] == '+' || t.text[0] == '-')) {
                r.fail(std::string("calc() requires whitespace around '") + t.text[0] + "'", t);
                return nullptr;
            }
            break;
        }
        if (t.kind != CssTokenKind::Delim) break;
        if (t.text != "+" && t.text != "-") break;
        if (!ws_before) {
            r.fail("calc() requires whitespace around '" + t.text + "'", t);
            return nullptr;
        }
        r.advance();
        if (!r.ws_ahead()) {
            r.fail("calc() requires whitespace around '" + t.text + "'", t);
            return nullptr;
        }
        CalcNodePtr right = parse_calc_term(r, depth);
        if (!right) return nullptr;
        auto b = std::make_unique<CalcBinaryNode>();
        b->op = (t.text == "+") ? CalcOp::Add : CalcOp::Sub;
        b->left = std::move(left);
        b->right = std::move(right);
        left = std::move(b);
    }
    return left;
}

CssValuePtr parse_function(Reader& r) {
    const CssToken fn = r.peek();
    if (ascii_lower(fn.text) == "calc") {
        r.advance();
        CalcNodePtr node = parse_calc_expression(r, 0);
        if (!node) return nullptr;
        r.skip_ws();
        if (r.peek().kind == CssTokenKind::RParen) r.advance();
        auto c = std::make_unique<CssCalc>();
        c->expression = std::move(node);
        c->raw = fn.text + "(";
        return c;
    }
    r.advance();
    auto call = std::make_unique<CssFunctionCall>();
    call->name = ascii_lower(fn.text);
    call->raw = fn.text + "(";

    // Arguments are comma-separated groups; a group with several values
    // becomes a space-separated list, matching ParseTopLevel's shape.
    std::vector<CssValuePtr> group;
    auto flush = [&] {
        if (group.empty()) return;
        if (group.size() == 1) {
            call->arguments.push_back(std::move(group[0]));
        } else {
            auto list = std::make_unique<CssValueList>();
            list->separator = CssListSeparator::Space;
            list->items = std::move(group);
            call->arguments.push_back(std::move(list));
        }
        group.clear();
    };

    r.skip_ws();
    while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
        if (r.peek().kind == CssTokenKind::Comma) {
            r.advance();
            r.skip_ws();
            flush();
            continue;
        }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) group.push_back(std::move(v));
        r.skip_ws();
    }
    flush();
    if (!r.at_end() && r.peek().kind == CssTokenKind::RParen) r.advance();

    if (CssValuePtr colour = eval_colour_function(*call)) return colour;
    return call;
}

CssValuePtr parse_paren_group(Reader& r) {
    r.advance();   // '('
    auto list = std::make_unique<CssValueList>();
    list->separator = CssListSeparator::Space;
    r.skip_ws();
    while (!r.at_end() && r.peek().kind != CssTokenKind::RParen) {
        if (r.peek().kind == CssTokenKind::Comma) { r.advance(); r.skip_ws(); continue; }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) list->items.push_back(std::move(v));
        r.skip_ws();
    }
    if (!r.at_end() && r.peek().kind == CssTokenKind::RParen) r.advance();
    if (list->items.size() == 1) return std::move(list->items[0]);
    return list;
}

CssValuePtr parse_single(Reader& r) {
    const CssToken t = r.peek();
    switch (t.kind) {
        case CssTokenKind::Ident: {
            r.advance();
            std::string lower = ascii_lower(t.text);
            // `currentcolor` stays a keyword — it resolves against the cascade,
            // not here.
            if (lower != "currentcolor") {
                auto c = std::make_unique<CssColor>();
                if (css_color_from_name(lower, c.get())) {
                    c->raw = t.text;
                    return c;
                }
            }
            auto k = std::make_unique<CssKeyword>();
            k->name = t.text;
            k->raw = t.text;
            return k;
        }
        case CssTokenKind::Number: {
            r.advance();
            auto n = std::make_unique<CssNumber>();
            n->value = t.number;
            n->raw = t.text;
            return n;
        }
        case CssTokenKind::Dimension: {
            r.advance();
            std::string unit_lower = ascii_lower(t.unit);
            CssAngleUnit au;
            if (css_angle_unit_from_string(unit_lower, &au)) {
                // CSS Values 4 §6.1: angles are their own type so length
                // consumers never have to special-case unit categories.
                auto a = std::make_unique<CssAngle>();
                a->value = t.number;
                a->unit = au;
                a->raw = t.text;
                return a;
            }
            CssLengthUnit lu;
            if (!css_length_unit_from_string(unit_lower, &lu)) {
                return r.fail("Unknown length unit '" + t.unit + "'", t);
            }
            auto l = std::make_unique<CssLength>();
            l->value = t.number;
            l->unit = lu;
            l->raw = t.text;
            return l;
        }
        case CssTokenKind::Percentage: {
            r.advance();
            auto p = std::make_unique<CssPercentage>();
            p->value = t.number;
            p->raw = t.text;
            return p;
        }
        case CssTokenKind::Hash: {
            r.advance();
            auto c = std::make_unique<CssColor>();
            if (!css_color_from_hex(t.text, c.get())) {
                return r.fail("Invalid hex color '#" + t.text + "'", t);
            }
            return c;
        }
        case CssTokenKind::String: {
            r.advance();
            auto s = std::make_unique<CssString>();
            s->text = t.text;
            s->raw = t.text;
            return s;
        }
        case CssTokenKind::Url: {
            r.advance();
            auto u = std::make_unique<CssUrl>();
            u->url = t.text;
            u->raw = t.text;
            return u;
        }
        case CssTokenKind::Function:
            return parse_function(r);
        case CssTokenKind::Delim: {
            r.advance();
            auto id = std::make_unique<CssIdentifier>();
            id->name = t.text;
            id->raw = t.text;
            return id;
        }
        case CssTokenKind::LParen:
            return parse_paren_group(r);
        default:
            return r.fail("Unexpected token '" + t.text + "'", t);
    }
}

} // namespace

CssValuePtr parse_css_value(std::string_view text, CssParseError* error) {
    std::vector<CssToken> tokens;
    CssTokenizer tokenizer(text, /*strict=*/true);
    if (!tokenizer.tokenize(&tokens, error)) return nullptr;

    Reader r{&tokens, 0, error};
    std::vector<std::vector<CssValuePtr>> segments;
    std::vector<CssValuePtr> current;

    r.skip_ws();
    while (!r.at_end()) {
        if (r.peek().kind == CssTokenKind::Comma) {
            r.advance();
            r.skip_ws();
            segments.push_back(std::move(current));
            current.clear();
            continue;
        }
        CssValuePtr v = parse_single(r);
        if (r.failed) return nullptr;
        if (v) current.push_back(std::move(v));
        r.skip_ws();
    }
    segments.push_back(std::move(current));

    auto join = [](std::vector<CssValuePtr> items) -> CssValuePtr {
        if (items.size() == 1) return std::move(items[0]);
        auto list = std::make_unique<CssValueList>();
        list->separator = CssListSeparator::Space;
        list->items = std::move(items);
        return list;
    };

    if (segments.size() == 1) {
        if (segments[0].empty()) {
            if (error) *error = CssParseError{"Empty value", 1, 1};
            return nullptr;
        }
        return join(std::move(segments[0]));
    }

    auto out = std::make_unique<CssValueList>();
    out->separator = CssListSeparator::Comma;
    for (auto& seg : segments) {
        if (seg.empty()) {
            if (error) *error = CssParseError{"Empty value between commas", 1, 1};
            return nullptr;
        }
        out->items.push_back(join(std::move(seg)));
    }
    return out;
}

} // namespace weva
