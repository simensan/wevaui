#include "weva/env_attr.h"

#include "weva/css_value.h"
#include "weva/dom.h"

#include <charconv>
#include <cstdio>

namespace weva {

namespace {

constexpr int kMaxDepth = 32;

bool is_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}
std::string trim(std::string_view s) {
    std::size_t b = 0, e = s.size();
    while (b < e && is_ws(s[b])) ++b;
    while (e > b && is_ws(s[e - 1])) --e;
    return std::string(s.substr(b, e - b));
}
std::string ascii_lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return o;
}
bool starts_with_ci(std::string_view s, std::size_t at, std::string_view needle) {
    if (at + needle.size() > s.size()) return false;
    for (std::size_t i = 0; i < needle.size(); ++i) {
        char a = s[at + i], b = needle[i];
        if (a >= 'A' && a <= 'Z') a = static_cast<char>(a + 32);
        if (b >= 'A' && b <= 'Z') b = static_cast<char>(b + 32);
        if (a != b) return false;
    }
    return true;
}
std::size_t find_matching_paren(std::string_view s, std::size_t open) {
    int depth = 0;
    char quote = '\0';
    for (std::size_t i = open; i < s.size(); ++i) {
        char c = s[i];
        if (quote != '\0') {
            if (c == '\\' && i + 1 < s.size()) { ++i; continue; }
            if (c == quote) quote = '\0';
            continue;
        }
        if (c == '"' || c == '\'') { quote = c; continue; }
        if (c == '(') ++depth;
        else if (c == ')') { --depth; if (depth == 0) return i; }
    }
    return std::string_view::npos;
}
void split_first_comma(std::string_view inside, std::string* head, std::string* tail,
                       bool* has_tail) {
    int depth = 0;
    char quote = '\0';
    for (std::size_t i = 0; i < inside.size(); ++i) {
        char c = inside[i];
        if (quote != '\0') {
            if (c == '\\' && i + 1 < inside.size()) { ++i; continue; }
            if (c == quote) quote = '\0';
            continue;
        }
        if (c == '"' || c == '\'') { quote = c; continue; }
        if (c == '(') ++depth;
        else if (c == ')') --depth;
        else if (c == ',' && depth == 0) {
            *head = std::string(inside.substr(0, i));
            *tail = std::string(inside.substr(i + 1));
            *has_tail = true;
            return;
        }
    }
    *head = std::string(inside);
    tail->clear();
    *has_tail = false;
}

bool has_ci(std::string_view s, std::string_view needle) {
    for (std::size_t i = 0; i + needle.size() <= s.size(); ++i) {
        if (starts_with_ci(s, i, needle)) return true;
    }
    return false;
}

// --- env ---------------------------------------------------------------------

bool resolve_env_internal(std::string_view value, int depth, std::string* out);

bool resolve_env_call(std::string_view inside, int depth, std::string* out) {
    std::string head, fallback;
    bool has_fallback = false;
    split_first_comma(inside, &head, &fallback, &has_fallback);

    // The spec allows an index list after the name (`env(name 2 0, fb)`); the
    // name is the first token.
    std::string name = trim(head);
    if (auto sp = name.find_first_of(" \t"); sp != std::string::npos) {
        name = name.substr(0, sp);
    }
    if (name.empty()) return false;

    std::string v;
    if (EnvironmentVariables::instance().get(name, &v)) {
        // env() values are UA-supplied literals; an env() inside one resolves
        // transitively, but a var() inside one does NOT — they are not part of
        // the author's custom-property namespace.
        return resolve_env_internal(v, depth + 1, out);
    }
    if (!has_fallback) return false;
    if (!resolve_env_internal(fallback, depth + 1, out)) return false;
    // The text after the comma keeps the separator's whitespace, so
    // `env(a, env(b))` would otherwise yield " 44px". The C# trims here too.
    *out = trim(*out);
    return true;
}

bool resolve_env_internal(std::string_view value, int depth, std::string* out) {
    if (depth > kMaxDepth) return false;
    if (!has_ci(value, "env(")) { out->assign(trim(value)); return true; }

    std::string sb;
    std::size_t i = 0;
    while (i < value.size()) {
        if (starts_with_ci(value, i, "env(")) {
            std::size_t paren = i + 3;
            std::size_t end = find_matching_paren(value, paren);
            if (end == std::string_view::npos) { sb.append(value.substr(i)); break; }
            std::string rep;
            if (!resolve_env_call(value.substr(paren + 1, end - paren - 1), depth, &rep)) {
                return false;   // taints the whole declaration, like var()
            }
            sb += rep;
            i = end + 1;
            continue;
        }
        sb.push_back(value[i]);
        ++i;
    }
    *out = sb;
    return true;
}

// --- attr --------------------------------------------------------------------

const char* kLengthUnits[] = {"px", "em", "rem", "ex", "ch", "cap", "ic", "lh", "rlh",
                              "vw", "vh", "vmin", "vmax", "vi", "vb",
                              "svw", "svh", "lvw", "lvh", "dvw", "dvh",
                              "cm", "mm", "in", "pt", "pc", "q"};
const char* kAngleUnits[] = {"deg", "grad", "rad", "turn"};
const char* kTimeUnits[] = {"ms", "s"};

bool parse_number(std::string_view s, double* out) {
    if (s.empty()) return false;
    if (s.front() == '+') s.remove_prefix(1);
    if (s.empty()) return false;
    auto res = std::from_chars(s.data(), s.data() + s.size(), *out);
    return res.ec == std::errc() && res.ptr == s.data() + s.size();
}

std::string format_number(double v) {
    // Shortest form that round-trips, matching C#'s "R" format.
    //
    // Deliberately NOT %g at low precision: %.1g turns 10 into "1e+01", which
    // round-trips and so passes a naive shortest-form search, producing
    // `attr(data-len length)` -> "1e+01px". std::to_chars gives the shortest
    // round-trip representation and only uses an exponent when that is
    // genuinely shorter.
    char buf[64];
    auto res = std::to_chars(buf, buf + sizeof(buf), v);
    if (res.ec == std::errc()) return std::string(buf, res.ptr);
    std::snprintf(buf, sizeof(buf), "%.17g", v);
    return buf;
}

template <std::size_t N>
bool format_dimension(std::string_view s, const char* const (&units)[N], std::string* out) {
    for (const char* u : units) {
        std::size_t ulen = std::char_traits<char>::length(u);
        if (s.size() <= ulen) continue;
        if (ascii_lower(s.substr(s.size() - ulen)) != u) continue;
        double n = 0;
        if (!parse_number(trim(s.substr(0, s.size() - ulen)), &n)) continue;
        *out = format_number(n) + u;
        return true;
    }
    return false;
}

bool format_attr(std::string_view raw, std::string_view type, std::string* out) {
    if (type == "string" || type == "raw-string") { *out = std::string(raw); return true; }

    std::string t = trim(raw);
    // <ident>: permissive, matching Chrome — the property's own parser rejects
    // an illegal ident downstream rather than attr() doing it here.
    if (type == "ident") {
        if (t.empty()) return false;
        *out = t;
        return true;
    }
    if (type == "color") {
        if (t.empty()) return false;
        if (t[0] == '#') {
            for (std::size_t k = 1; k < t.size(); ++k) {
                char c = t[k];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            std::size_t len = t.size() - 1;
            if (len != 3 && len != 4 && len != 6 && len != 8) return false;
            *out = t;
            return true;
        }
        if (auto p = t.find('('); p != std::string::npos && p > 0 && t.back() == ')') {
            *out = t;
            return true;
        }
        CssColor c;
        if (css_color_from_name(ascii_lower(t), &c)) { *out = t; return true; }
        return false;
    }
    if (type == "length") return format_dimension(t, kLengthUnits, out);
    if (type == "angle") return format_dimension(t, kAngleUnits, out);
    if (type == "time") return format_dimension(t, kTimeUnits, out);
    if (type == "integer") {
        double n = 0;
        if (!parse_number(t, &n)) return false;
        if (n != static_cast<double>(static_cast<long long>(n))) return false;
        *out = std::to_string(static_cast<long long>(n));
        return true;
    }

    double n = 0;
    if (!parse_number(t, &n)) return false;
    std::string num = format_number(n);
    if (type == "number") { *out = num; return true; }
    if (type == "percentage" || type == "%") { *out = num + "%"; return true; }
    for (const char* u : {"px", "em", "rem", "vw", "vh", "vmin", "vmax"}) {
        if (type == u) { *out = num + u; return true; }
    }
    return false;
}

std::string resolve_attr_internal(std::string_view value, const Element& e, int depth);

std::string resolve_attr_call(std::string_view inside, const Element& e, int depth) {
    std::string head, fallback;
    bool has_fallback = false;
    split_first_comma(inside, &head, &fallback, &has_fallback);
    head = trim(head);
    auto fb = [&] {
        return has_fallback ? resolve_attr_internal(trim(fallback), e, depth + 1) : std::string();
    };
    if (head.empty()) return fb();

    std::string name = head, type = "string";
    if (auto sp = head.find_first_of(" \t"); sp != std::string::npos) {
        name = trim(std::string_view(head).substr(0, sp));
        type = ascii_lower(trim(std::string_view(head).substr(sp + 1)));
        if (type.empty()) type = "string";
    }

    if (!e.has_attribute(name)) return fb();
    std::string formatted;
    if (format_attr(e.get_attribute(name), type, &formatted)) return formatted;
    return fb();
}

std::string resolve_attr_internal(std::string_view value, const Element& e, int depth) {
    if (depth > kMaxDepth) return std::string();
    if (!has_ci(value, "attr(")) return std::string(value);

    std::string sb;
    std::size_t i = 0;
    while (i < value.size()) {
        if (starts_with_ci(value, i, "attr(")) {
            std::size_t paren = i + 4;
            std::size_t end = find_matching_paren(value, paren);
            if (end == std::string_view::npos) { sb.append(value.substr(i)); break; }
            sb += resolve_attr_call(value.substr(paren + 1, end - paren - 1), e, depth);
            i = end + 1;
            continue;
        }
        sb.push_back(value[i]);
        ++i;
    }
    return sb;
}

} // namespace

EnvironmentVariables::EnvironmentVariables() { reset_to_defaults(); }

EnvironmentVariables& EnvironmentVariables::instance() {
    static EnvironmentVariables e;
    return e;
}

void EnvironmentVariables::reset_to_defaults() {
    vars_.clear();
    // Typical web usage is notch / safe-area avoidance; a host with no insets
    // reports zero rather than leaving the name unresolvable, so
    // `padding-top: env(safe-area-inset-top)` works everywhere.
    vars_["safe-area-inset-top"] = "0px";
    vars_["safe-area-inset-right"] = "0px";
    vars_["safe-area-inset-bottom"] = "0px";
    vars_["safe-area-inset-left"] = "0px";
}

void EnvironmentVariables::set(std::string_view name, std::string_view value) {
    vars_[std::string(name)] = std::string(value);
}

bool EnvironmentVariables::get(std::string_view name, std::string* out) const {
    auto it = vars_.find(std::string(name));
    if (it == vars_.end()) return false;
    *out = it->second;
    return true;
}

bool resolve_env(std::string_view value, std::string* resolved) {
    return resolve_env_internal(value, 0, resolved);
}

std::string resolve_attr(std::string_view value, const Element& element) {
    return resolve_attr_internal(value, element, 0);
}

} // namespace weva
