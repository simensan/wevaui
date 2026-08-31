#include "weva/variable_resolver.h"

#include <set>
#include <string>

namespace weva {

namespace {

constexpr int kMaxDepth = 32;

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
        else if (c == ')') {
            --depth;
            if (depth == 0) return i;
        }
    }
    return std::string_view::npos;
}

std::string trim(std::string_view s) {
    auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    std::size_t b = 0, e = s.size();
    while (b < e && ws(s[b])) ++b;
    while (e > b && ws(s[e - 1])) --e;
    return std::string(s.substr(b, e - b));
}

// Splits `--name, fallback` at the FIRST top-level comma; the fallback may
// itself contain commas and nested functions.
void split_var_args(std::string_view inside, std::string* name, std::string* fallback,
                    bool* has_fallback) {
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
            *name = std::string(inside.substr(0, i));
            *fallback = std::string(inside.substr(i + 1));
            *has_fallback = true;
            return;
        }
    }
    *name = std::string(inside);
    fallback->clear();
    *has_fallback = false;
}

struct Resolver {
    const ComputedStyle& style;
    std::set<std::string> seen;           // names on the current resolution stack
    std::set<std::string> cycle_members;  // names proven to be in a cycle

    // Returns false to mean "invalid at computed-value time" — the C# uses a
    // reference-equal sentinel string for this; a bool is clearer and cannot
    // leak to a caller as a real value.
    bool resolve(std::string_view value, int depth, std::string* out) {
        if (depth > kMaxDepth) return false;
        // Fast path: nothing to substitute.
        bool has_var = false;
        for (std::size_t i = 0; i + 4 <= value.size(); ++i) {
            if (starts_with_ci(value, i, "var(")) { has_var = true; break; }
        }
        if (!has_var) { out->assign(value); return true; }

        std::string sb;
        sb.reserve(value.size());
        std::size_t i = 0;
        while (i < value.size()) {
            if (starts_with_ci(value, i, "var(")) {
                std::size_t paren = i + 3;
                std::size_t end = find_matching_paren(value, paren);
                if (end == std::string_view::npos) {
                    // Unbalanced: emit the remainder verbatim, matching the C#.
                    sb.append(value.substr(i));
                    break;
                }
                std::string replacement;
                if (!resolve_var_call(value.substr(paren + 1, end - paren - 1), depth,
                                      &replacement)) {
                    // §3: one invalid var() taints the entire declaration.
                    return false;
                }
                sb += replacement;
                i = end + 1;
                continue;
            }
            sb.push_back(value[i]);
            ++i;
        }
        *out = std::move(sb);
        return true;
    }

    bool resolve_var_call(std::string_view inside, int depth, std::string* out) {
        std::string raw_name, fallback;
        bool has_fallback = false;
        split_var_args(inside, &raw_name, &fallback, &has_fallback);
        std::string name = trim(raw_name);
        if (name.empty()) return false;

        if (name.rfind("--", 0) != 0) {
            // First argument is not a custom-property name. The var() is
            // invalid; honour a fallback if present, otherwise propagate.
            if (!has_fallback) return false;
            std::string fb;
            if (!resolve(fallback, depth + 1, &fb)) return false;
            *out = trim(fb);
            return true;
        }

        // §3.1: once a name is known to be in a cycle, every later reference to
        // it is invalid regardless of that reference's own fallback — otherwise
        // a fallback would "rescue" the cycle member through an open frame.
        if (cycle_members.count(name)) return false;

        if (seen.count(name)) {
            // Closing a cycle: every name currently on the stack, plus this
            // one, is a cycle member.
            for (const auto& n : seen) cycle_members.insert(n);
            cycle_members.insert(name);
            return false;
        }

        // Mark seen for the WHOLE call — both the substitution recurse and the
        // fallback recurse. If only the substitution branch marked it, a
        // fallback that re-references the same name would walk to kMaxDepth
        // instead of terminating immediately.
        seen.insert(name);
        struct Unmark {
            std::set<std::string>& s;
            const std::string& n;
            ~Unmark() { s.erase(n); }
        } unmark{seen, name};

        std::string custom;
        if (style.contains(name)) custom = std::string(style.get(name));
        if (!custom.empty()) {
            std::string substituted;
            if (resolve(custom, depth + 1, &substituted)) {
                *out = std::move(substituted);
                return true;
            }
            // The stored value resolved to invalid. If we were just promoted to
            // a cycle member, §3.1 forbids rescuing via our own fallback.
            if (cycle_members.count(name)) return false;
        }

        if (!has_fallback) return false;
        std::string fb;
        if (!resolve(fallback, depth + 1, &fb)) return false;
        *out = trim(fb);
        return true;
    }
};

} // namespace

bool resolve_variables(std::string_view value, const ComputedStyle& style,
                       std::string* resolved) {
    Resolver r{style, {}, {}};
    return r.resolve(value, 0, resolved);
}

} // namespace weva
