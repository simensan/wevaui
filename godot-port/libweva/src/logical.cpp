#include "weva/logical.h"

#include "weva/css_properties.h"

#include <cstdint>
#include <string>
#include <vector>

namespace weva {

namespace {

std::string ascii_lower_trim(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\n' || s[b] == '\r')) ++b;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\n' || s[e - 1] == '\r')) --e;
    std::string o(s.substr(b, e - b));
    for (char& c : o) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return o;
}

// Reads an inherited keyword property for the axis computation. ComputedStyle
// already resolves the inherit chain and the initial value on read; what it
// does NOT do is interpret the CSS-wide keywords, which for `direction` and
// `writing-mode` decide the whole mapping.
std::string axis_keyword(const ComputedStyle& style, std::string_view property) {
    auto& reg = CssPropertyRegistry::instance();
    const int id = reg.id_of(property);
    if (id == kCustomPropertyId) return {};
    std::string v = ascii_lower_trim(style.get(id));
    if (v == "inherit" || v == "unset" || v.empty()) {
        // Both are `inherit` here: direction and writing-mode are inherited
        // properties, so `unset` computes to the inherited value.
        const ComputedStyle* p = style.inherit_parent();
        v = p ? ascii_lower_trim(p->get(id)) : std::string(reg.initial_value(id));
        if (v == "inherit" || v == "unset" || v.empty()) v = std::string(reg.initial_value(id));
    } else if (v == "initial" || v == "revert" || v == "revert-layer") {
        // No user or UA sheet declares either property, so `revert` lands on
        // the initial value too.
        v = std::string(reg.initial_value(id));
    }
    return v;
}

// Copies a logical declaration onto its physical target when the cascade says
// the logical one wins. The synthetic declaration inherits the logical
// winner's full cascade key, so this is an ordering question, not a fixed
// "physical always wins" rule.
struct Table {
    CascadeKey* keys = nullptr;
    int count = 0;
    uint64_t generation = 0;

    // A slot from an earlier element is stale, not set.
    const CascadeKey* at(int id) const {
        if (id < 0 || id >= count) return nullptr;
        return keys[id].generation == generation ? &keys[id] : nullptr;
    }
    void put(int id, const CascadeKey& k) {
        if (id >= 0 && id < count) keys[id] = k;
    }
};

// One logical->physical mapping, resolved to property ids.
struct AliasPair { int logical_id; int physical_id; };

// Assembling the physical names costs ~60 string concatenations and ~120
// registry lookups. Doing that per element measured a 35% cascade regression on
// the demo document, which is the same trap the C# engine's pre-interned name
// tables exist to avoid. The mapping depends only on the resolved axes, and
// there are ten reachable axis configurations, so each table is built once.
//
// Not thread-safe, in common with the property registry and the engine's match
// cache: the cascade is single-threaded by design.
int side_index(const std::string& side) {
    if (side == "top") return 0;
    if (side == "right") return 1;
    if (side == "bottom") return 2;
    return 3;   // "left"
}

int axes_key(const LogicalAxes& ax) {
    return side_index(ax.inline_start) | (side_index(ax.inline_end) << 2) |
           (side_index(ax.block_start) << 4) | (side_index(ax.block_end) << 6) |
           (ax.inline_is_horizontal ? 1 << 8 : 0);
}

void add(std::vector<AliasPair>* out, std::string_view logical, const std::string& physical) {
    auto& reg = CssPropertyRegistry::instance();
    const int lid = reg.id_of(logical);
    const int pid = reg.id_of(physical);
    // A property neither side knows about contributes no mapping rather than a
    // pair that can never fire.
    if (lid == kCustomPropertyId || pid == kCustomPropertyId) return;
    out->push_back({lid, pid});
}

// The two logical corner sides are one block side and one inline side, but in a
// vertical writing mode the block side is left/right and the inline side is
// top/bottom — so the physical corner name has to be assembled by asking which
// of the two is vertical, not by concatenating in argument order.
void add_corner(std::vector<AliasPair>* out, std::string_view logical, const std::string& a,
                const std::string& b) {
    const bool top = a == "top" || b == "top";
    const bool right = a == "right" || b == "right";
    const bool bottom = a == "bottom" || b == "bottom";
    const bool left = a == "left" || b == "left";
    if (top && left) add(out, logical, "border-top-left-radius");
    else if (top && right) add(out, logical, "border-top-right-radius");
    else if (bottom && right) add(out, logical, "border-bottom-right-radius");
    else if (bottom && left) add(out, logical, "border-bottom-left-radius");
}

std::vector<AliasPair> build_alias_table(const LogicalAxes& ax) {
    std::vector<AliasPair> t;
    t.reserve(64);

    const std::string h = ax.inline_is_horizontal ? "width" : "height";
    const std::string v = ax.inline_is_horizontal ? "height" : "width";
    add(&t, "inline-size", h);
    add(&t, "block-size", v);
    add(&t, "min-inline-size", "min-" + h);
    add(&t, "min-block-size", "min-" + v);
    add(&t, "max-inline-size", "max-" + h);
    add(&t, "max-block-size", "max-" + v);

    struct Edge { const char* suffix; const std::string& side; };
    const Edge edges[] = {
        {"inline-start", ax.inline_start}, {"inline-end", ax.inline_end},
        {"block-start", ax.block_start},   {"block-end", ax.block_end},
    };
    for (const Edge& e : edges) {
        add(&t, std::string("margin-") + e.suffix, "margin-" + e.side);
        add(&t, std::string("padding-") + e.suffix, "padding-" + e.side);
        add(&t, std::string("overflow-clip-margin-") + e.suffix,
            "overflow-clip-margin-" + e.side);
        // inset-* map to the bare physical side names (top/right/bottom/left).
        add(&t, std::string("inset-") + e.suffix, e.side);
        add(&t, std::string("border-") + e.suffix, "border-" + e.side);
        for (const char* comp : {"width", "style", "color"}) {
            add(&t, std::string("border-") + e.suffix + "-" + comp,
                "border-" + e.side + "-" + comp);
        }
    }

    add_corner(&t, "border-start-start-radius", ax.block_start, ax.inline_start);
    add_corner(&t, "border-start-end-radius", ax.block_start, ax.inline_end);
    add_corner(&t, "border-end-start-radius", ax.block_end, ax.inline_start);
    add_corner(&t, "border-end-end-radius", ax.block_end, ax.inline_end);
    return t;
}

// Bitmask of every logical property id, matched against a style's occupied
// bits. Most elements declare no logical property at all, and most stylesheets
// contain none — testing six words up front skips the axis resolution (two
// inherit-chain walks and two string allocations) and the ~60 slot probes for
// all of them. The set of LOGICAL ids is the same in every axis configuration;
// only the physical targets move.
const std::vector<uint64_t>& logical_id_mask() {
    static const std::vector<uint64_t> mask = [] {
        std::vector<uint64_t> m;
        for (const AliasPair& p : build_alias_table(LogicalAxes{})) {
            const size_t w = static_cast<size_t>(p.logical_id) / 64;
            if (m.size() <= w) m.resize(w + 1, 0);
            m[w] |= uint64_t{1} << (static_cast<size_t>(p.logical_id) % 64);
        }
        return m;
    }();
    return mask;
}

bool declares_any_logical_property(const ComputedStyle& style) {
    const std::vector<uint64_t>& occ = style.occupied_bits();
    const std::vector<uint64_t>& mask = logical_id_mask();
    const size_t n = occ.size() < mask.size() ? occ.size() : mask.size();
    for (size_t i = 0; i < n; ++i) {
        if (occ[i] & mask[i]) return true;
    }
    return false;
}

const std::vector<AliasPair>& alias_table_for(const LogicalAxes& ax) {
    struct Entry { int key; std::vector<AliasPair> table; };
    static std::vector<Entry> cache;
    const int key = axes_key(ax);
    for (const Entry& e : cache) {
        if (e.key == key) return e.table;
    }
    cache.push_back({key, build_alias_table(ax)});
    return cache.back().table;
}

void alias(ComputedStyle* s, Table& w, int lid, int pid) {
    if (!s->contains(lid)) return;
    const CascadeKey* syn = w.at(lid);
    if (!syn) return;
    if (s->contains(pid)) {
        const CascadeKey* cur = w.at(pid);
        // Without a key to compare against, the physical value stands: it was
        // set later in the same pass either way.
        if (!cur) return;
        if (compare_for_cascade(*cur, *syn) > 0) return;
    }
    const CascadeKey k = *syn;
    s->set(pid, s->get(lid));
    s->set_important(pid, s->is_important(lid));
    w.put(pid, k);
}

} // namespace


LogicalAxes LogicalAxes::from(std::string_view direction, std::string_view writing_mode) {
    const std::string dir = ascii_lower_trim(direction);
    const std::string wm = ascii_lower_trim(writing_mode);
    const bool rtl = dir == "rtl";

    // In a vertical mode the inline axis runs top/bottom and the block axis
    // runs left/right, so inline-size maps to height rather than width.
    if (wm == "vertical-rl" || wm == "sideways-rl") {
        return {rtl ? "bottom" : "top", rtl ? "top" : "bottom", "right", "left", false};
    }
    if (wm == "vertical-lr") {
        return {rtl ? "bottom" : "top", rtl ? "top" : "bottom", "left", "right", false};
    }
    // sideways-lr rotates the other way, so the inline direction flips relative
    // to vertical-lr even though the block axis is the same.
    if (wm == "sideways-lr") {
        return {rtl ? "top" : "bottom", rtl ? "bottom" : "top", "left", "right", false};
    }
    return {rtl ? "right" : "left", rtl ? "left" : "right", "top", "bottom", true};
}

void apply_logical_properties(ComputedStyle* style, CascadeKey* winners, int count,
                              uint64_t generation) {
    if (!style || !winners || count <= 0) return;
    if (!declares_any_logical_property(*style)) return;
    Table w{winners, count, generation};

    const LogicalAxes ax = LogicalAxes::from(axis_keyword(*style, "direction"),
                                             axis_keyword(*style, "writing-mode"));
    for (const AliasPair& p : alias_table_for(ax)) {
        alias(style, w, p.logical_id, p.physical_id);
    }
}

} // namespace weva
