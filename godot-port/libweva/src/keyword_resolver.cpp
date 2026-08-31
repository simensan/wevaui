#include "weva/keyword_resolver.h"

#include "weva/css_properties.h"

namespace weva {

namespace {

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

std::string_view trim(std::string_view s) {
    const auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    size_t b = 0, e = s.size();
    while (b < e && ws(s[b])) ++b;
    while (e > b && ws(s[e - 1])) --e;
    return s.substr(b, e - b);
}

// The parent's COMPUTED value. This port resolves inheritance lazily, so the
// parent's get() already walks its own chain and falls back to the initial
// value — which is exactly what the C# reads off a materialised parent style.
std::string inherited_value(int property_id, const ComputedStyle* parent) {
    if (parent) return std::string(parent->get(property_id));
    return std::string(CssPropertyRegistry::instance().initial_value(property_id));
}

// Finds the value text of the highest-priority match for `property` at an
// origin strictly below the winner's. `expanded` is in ascending cascade order,
// so scanning backwards makes the first hit the answer.
const MatchedDeclaration* rollback_revert(std::string_view property,
                                          const std::vector<MatchedDeclaration>& expanded,
                                          DeclarationOrigin winner_origin) {
    for (size_t i = expanded.size(); i-- > 0;) {
        const MatchedDeclaration& m = expanded[i];
        if (m.declaration->property != property) continue;
        if (static_cast<int>(m.origin) >= static_cast<int>(winner_origin)) continue;
        return &m;
    }
    return nullptr;
}

// `revert-layer` rolls back to the same origin but the nearest lower layer;
// with no lower layer at that origin it degrades to `revert` and drops the
// origin entirely.
const MatchedDeclaration* rollback_revert_layer(std::string_view property,
                                                const std::vector<MatchedDeclaration>& expanded,
                                                DeclarationOrigin winner_origin,
                                                int winner_layer) {
    int max_lower = -1;
    bool found = false;
    for (const MatchedDeclaration& m : expanded) {
        if (m.declaration->property != property) continue;
        if (m.origin != winner_origin) continue;
        if (m.layer_ordinal >= winner_layer) continue;
        if (!found || m.layer_ordinal > max_lower) {
            max_lower = m.layer_ordinal;
            found = true;
        }
    }
    if (!found) return rollback_revert(property, expanded, winner_origin);

    const MatchedDeclaration* best = nullptr;
    for (const MatchedDeclaration& m : expanded) {
        if (m.declaration->property != property) continue;
        if (m.origin != winner_origin) continue;
        if (m.layer_ordinal != max_lower) continue;
        best = &m;   // ascending cascade order, so the last hit is the winner
    }
    return best;
}

} // namespace

bool is_css_wide_keyword(std::string_view value) {
    // Length-gated so the common case — an ordinary value — costs one compare.
    if (value.size() < 5 || value.size() > 12) return false;
    return iequals(value, "inherit") || iequals(value, "initial") ||
           iequals(value, "unset") || iequals(value, "revert") ||
           iequals(value, "revert-layer");
}

bool resolve_css_wide_keyword(int property_id, std::string_view value,
                              const ComputedStyle* parent, std::string* out) {
    const std::string_view t = trim(value);
    if (!is_css_wide_keyword(t)) return false;
    auto& reg = CssPropertyRegistry::instance();

    if (iequals(t, "inherit")) {
        *out = inherited_value(property_id, parent);
    } else if (iequals(t, "unset")) {
        *out = reg.is_inherited(property_id) ? inherited_value(property_id, parent)
                                             : std::string(reg.initial_value(property_id));
    } else {
        // initial, revert and revert-layer. The two rollback keywords only
        // reach here when no rollback target existed.
        *out = std::string(reg.initial_value(property_id));
    }
    return true;
}

bool resolve_css_wide_keyword_custom(std::string_view name, std::string_view value,
                                     const ComputedStyle* parent, std::string* out) {
    const std::string_view t = trim(value);
    if (!is_css_wide_keyword(t)) return false;

    // `inherit` and `unset` coincide: every custom property is inherited.
    if (iequals(t, "inherit") || iequals(t, "unset")) {
        if (parent && parent->contains(name)) {
            *out = std::string(parent->get(name));
        } else {
            out->clear();
        }
        return true;
    }
    // The initial value of a custom property is the empty string, even for one
    // registered via `@property` — the C# reads the initial value off a
    // synthesised descriptor that knows nothing about the registry.
    out->clear();
    return true;
}

std::string pre_resolve_rollback(std::string_view property, std::string_view value,
                                 const std::vector<MatchedDeclaration>& expanded,
                                 const CascadeKey& winner) {
    std::string current(value);
    DeclarationOrigin origin = winner.origin;
    int layer = winner.layer_ordinal;

    // A rolled-back value may itself be `revert`, which rolls back again.
    // Capped at four hops; deeper chains are pathological.
    for (int depth = 0; depth < 4; ++depth) {
        const std::string_view t = trim(current);
        const bool is_revert = iequals(t, "revert");
        const bool is_revert_layer = iequals(t, "revert-layer");
        if (!is_revert && !is_revert_layer) return current;

        const MatchedDeclaration* rb =
            is_revert ? rollback_revert(property, expanded, origin)
                      : rollback_revert_layer(property, expanded, origin, layer);
        // No target: leave the keyword in place so the resolver maps it to
        // the initial value.
        if (!rb) return current;
        current = rb->declaration->value_text;
        origin = rb->origin;
        layer = rb->layer_ordinal;
    }
    return current;
}

} // namespace weva
