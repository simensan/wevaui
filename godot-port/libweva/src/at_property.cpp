#include "weva/at_property.h"

#include "weva/css_value.h"

#include <algorithm>
#include <cctype>

namespace weva {

namespace {

std::string ascii_lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return o;
}

std::string_view trim(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
    return s.substr(b, e - b);
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

bool starts_with_ci(std::string_view s, std::string_view prefix) {
    return s.size() >= prefix.size() && iequals(s.substr(0, prefix.size()), prefix);
}

bool ends_with_ci(std::string_view s, std::string_view suffix) {
    return s.size() >= suffix.size() && iequals(s.substr(s.size() - suffix.size()), suffix);
}

// Mirrors C#'s `double.TryParse(v, NumberStyles.Float, InvariantCulture)`:
// leading/trailing whitespace and a leading sign are accepted, a bare "." or
// "-" is not, and the WHOLE string must be the number. css_parse_double
// handles the leading '+' that std::from_chars rejects and the trailing junk
// it silently ignores.
bool is_double(std::string_view v) {
    v = trim(v);
    if (v.empty()) return false;
    double d = 0;
    return css_parse_double(v, &d);
}

bool is_length(std::string_view v) {
    if (v == "0") return true;
    if (v.empty()) return false;
    if (starts_with_ci(v, "calc(") || starts_with_ci(v, "min(") ||
        starts_with_ci(v, "max(") || starts_with_ci(v, "clamp(")) {
        return true;
    }
    if (starts_with_ci(v, "env(") || starts_with_ci(v, "var(")) return true;
    // Order matters: the first unit whose suffix matches AND whose prefix
    // parses wins, so "10min" fails on "in" (prefix "10m") and finds no other
    // match rather than being read as 10 minutes-of-something.
    static const char* kUnits[] = {
        "px", "em", "rem", "vh", "vw", "vmin", "vmax", "vb", "vi", "%", "pt", "pc",
        "cm", "mm", "in", "q", "ex", "ch", "cap", "ic", "lh", "rlh", "svh", "lvh",
        "dvh", "cqw", "cqh",
    };
    for (const char* u : kUnits) {
        std::string_view unit(u);
        if (ends_with_ci(v, unit) && is_double(v.substr(0, v.size() - unit.size()))) return true;
    }
    return false;
}

bool is_number(std::string_view v) {
    if (v.empty()) return false;
    if (starts_with_ci(v, "calc(")) return true;
    return is_double(v);
}

bool is_integer(std::string_view v) {
    // C# uses int.TryParse, which rejects a decimal point and an exponent but
    // accepts surrounding whitespace and a leading sign.
    v = trim(v);
    if (v.empty()) return false;
    size_t i = (v[0] == '+' || v[0] == '-') ? 1 : 0;
    if (i >= v.size()) return false;
    for (; i < v.size(); ++i) {
        if (v[i] < '0' || v[i] > '9') return false;
    }
    return true;
}

bool is_percentage(std::string_view v) {
    if (v.empty() || v.back() != '%') return false;
    return is_double(v.substr(0, v.size() - 1));
}

bool is_color(std::string_view v) {
    if (v.empty()) return false;
    // Length only — the C# does not check that the characters are hex digits,
    // so `#zzzz` validates. Reproduced rather than tightened: a stricter port
    // would reject an authored value the reference engine accepts.
    if (v[0] == '#') return v.size() == 4 || v.size() == 5 || v.size() == 7 || v.size() == 9;
    static const char* kFuncs[] = {"rgb(", "rgba(", "hsl(", "hsla(", "hwb(", "color(",
                                   "oklch(", "oklab(", "lch(", "lab(", "var("};
    for (const char* f : kFuncs) {
        if (starts_with_ci(v, f)) return true;
    }
    // Case-sensitive on purpose: the C# compares these three spellings exactly
    // and leaves every other casing to the named-colour table below, which is
    // case-insensitive and does not contain them.
    if (v == "transparent" || v == "currentcolor" || v == "currentColor") return true;
    CssColor c;
    return css_color_from_name(v, &c);
}

bool matches_unit_list(std::string_view v, const char* const* units, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        std::string_view u(units[i]);
        if (ends_with_ci(v, u) && is_double(v.substr(0, v.size() - u.size()))) return true;
    }
    return false;
}

bool is_angle(std::string_view v) {
    if (v.empty()) return false;
    static const char* kUnits[] = {"deg", "rad", "grad", "turn"};
    return matches_unit_list(v, kUnits, 4);
}

bool is_time(std::string_view v) {
    if (v.empty()) return false;
    // "ms" must be tried before "s", or every millisecond value parses as a
    // seconds value with a trailing "m" prefix that fails to convert.
    if (ends_with_ci(v, "ms")) return is_double(v.substr(0, v.size() - 2));
    if (ends_with_ci(v, "s")) return is_double(v.substr(0, v.size() - 1));
    return false;
}

bool is_resolution(std::string_view v) {
    if (v.empty()) return false;
    static const char* kUnits[] = {"dpi", "dpcm", "dppx", "x"};
    return matches_unit_list(v, kUnits, 4);
}

bool is_ident(std::string_view v) {
    if (v.empty()) return false;
    char c0 = v[0];
    if (c0 == '-' && v.size() > 1) c0 = v[1];
    const auto is_letter = [](char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    };
    if (!is_letter(c0) && c0 != '_') return false;
    for (size_t i = 1; i < v.size(); ++i) {
        char c = v[i];
        const bool alnum = is_letter(c) || (c >= '0' && c <= '9');
        if (!alnum && c != '-' && c != '_') return false;
    }
    return true;
}

bool matches_single(std::string_view alt_syntax, std::string_view value) {
    // v1 strips the list multipliers rather than enforcing them, so
    // `<length>+` validates a single length.
    std::string_view syn = alt_syntax;
    while (!syn.empty() && (syn.back() == '+' || syn.back() == '#')) syn.remove_suffix(1);

    if (syn == "*") return true;
    if (syn == "<length>") return is_length(value);
    if (syn == "<number>") return is_number(value);
    if (syn == "<integer>") return is_integer(value);
    if (syn == "<percentage>") return is_percentage(value);
    if (syn == "<color>") return is_color(value);
    if (syn == "<angle>") return is_angle(value);
    if (syn == "<time>") return is_time(value);
    if (syn == "<resolution>") return is_resolution(value);
    if (syn == "<url>") return starts_with_ci(value, "url(");
    if (syn == "<image>") {
        return starts_with_ci(value, "url(") || starts_with_ci(value, "linear-gradient(") ||
               starts_with_ci(value, "radial-gradient(") || starts_with_ci(value, "conic-gradient(");
    }
    if (syn == "<string>") {
        return value.size() >= 2 && (value[0] == '"' || value[0] == '\'');
    }
    if (syn == "<custom-ident>") return is_ident(value);
    if (syn == "<transform-function>" || syn == "<transform-list>") {
        return value.find('(') != std::string_view::npos;
    }
    // An unrecognised component accepts anything. Permissive on purpose: a
    // syntax this port does not model yet must not invalidate the author's
    // rule.
    return true;
}

bool contains_variable_reference(std::string_view value) {
    if (value.empty()) return false;
    const std::string lower = ascii_lower(value);
    return lower.find("var(") != std::string::npos || lower.find("env(") != std::string::npos;
}

} // namespace

bool PropertyDescriptor::validate(std::string_view syntax, std::string_view value) {
    if (syntax == "*") return true;
    const std::string_view v = trim(value);
    // Split on `|` for alternatives. The C# splits on every `|`, including one
    // inside `<...>`; no spec syntax puts a bar there, so the simple split is
    // kept rather than made cleverer than the reference.
    size_t start = 0;
    while (true) {
        const size_t bar = syntax.find('|', start);
        const std::string_view alt =
            bar == std::string_view::npos ? syntax.substr(start) : syntax.substr(start, bar - start);
        if (matches_single(trim(alt), v)) return true;
        if (bar == std::string_view::npos) break;
        start = bar + 1;
    }
    return false;
}

std::optional<PropertyDescriptor> PropertyDescriptor::try_create(
    std::string_view name, const std::optional<std::string>& syntax,
    const std::optional<std::string>& initial_value,
    const std::optional<std::string>& inherits_text) {
    if (name.size() < 2 || name.substr(0, 2) != "--") return std::nullopt;
    if (!syntax || syntax->empty()) return std::nullopt;
    // Present-but-empty is valid here (universal syntax); absent is not.
    if (!initial_value) return std::nullopt;
    if (!inherits_text || inherits_text->empty()) return std::nullopt;

    const std::string inherits_lower = ascii_lower(trim(*inherits_text));
    bool inherits;
    if (inherits_lower == "true") inherits = true;
    else if (inherits_lower == "false") inherits = false;
    else return std::nullopt;   // any other keyword invalidates the rule

    // Authors write both `syntax: <length>` and `syntax: "<length>"`.
    std::string_view syn = trim(*syntax);
    if (syn.size() >= 2 && syn.front() == '"' && syn.back() == '"') {
        syn = trim(syn.substr(1, syn.size() - 2));
    }
    if (syn.empty()) return std::nullopt;

    // CSS Properties & Values L1 §3.4: an initial-value may not reference a
    // variable, since it has to be computable without an element.
    if (contains_variable_reference(*initial_value)) return std::nullopt;

    if (syn != "*" && !validate(syn, *initial_value)) return std::nullopt;

    PropertyDescriptor d;
    d.name = std::string(name);
    d.syntax = std::string(syn);
    d.initial_value = *initial_value;
    d.inherits = inherits;
    return d;
}

void AtPropertyRegistry::register_descriptor(const PropertyDescriptor& d) {
    if (d.name.empty()) return;
    descriptors_[d.name] = d;
}

const PropertyDescriptor* AtPropertyRegistry::find(std::string_view name) const {
    auto it = descriptors_.find(std::string(name));
    return it == descriptors_.end() ? nullptr : &it->second;
}

bool AtPropertyRegistry::is_non_inheriting(std::string_view name) const {
    const PropertyDescriptor* d = find(name);
    return d && !d->inherits;
}

const std::string* AtPropertyRegistry::initial_value(std::string_view name) const {
    const PropertyDescriptor* d = find(name);
    return d ? &d->initial_value : nullptr;
}

bool AtPropertyRegistry::validate_value(std::string_view name, std::string_view value) const {
    const PropertyDescriptor* d = find(name);
    if (!d) return true;   // unregistered: no declared syntax to violate
    return PropertyDescriptor::validate(d->syntax, value);
}

std::optional<PropertyDescriptor> parse_at_property_rule(const GenericAtRule& rule) {
    const std::string_view name = trim(rule.prelude);
    if (!rule.has_block) return std::nullopt;

    std::optional<std::string> syntax, initial_value, inherits_text;
    for (const Declaration& d : rule.declarations) {
        // First occurrence of each descriptor wins, matching the C#.
        if (d.property == "syntax") {
            if (!syntax) syntax = d.value_text;
        } else if (d.property == "initial-value") {
            if (!initial_value) initial_value = d.value_text;
        } else if (d.property == "inherits") {
            if (!inherits_text) inherits_text = d.value_text;
        }
    }
    return PropertyDescriptor::try_create(name, syntax, initial_value, inherits_text);
}

} // namespace weva
