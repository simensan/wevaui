#include "weva/css_properties.h"

#include <algorithm>

namespace weva {

namespace {
struct PropertyDef { const char* name; bool inherited; const char* initial; };
#include "generated/css_properties.inc"
}  // namespace

CssPropertyRegistry::CssPropertyRegistry() {
    properties_.reserve(sizeof(kProperties) / sizeof(kProperties[0]));
    for (const PropertyDef& d : kProperties) {
        register_property(d.name, d.inherited, d.initial);
    }
}

CssPropertyRegistry& CssPropertyRegistry::instance() {
    static CssPropertyRegistry r;
    return r;
}

// Constant-time, not a pass over the name.
//
// A byte-at-a-time FNV replaced the binary search and was still a third of a
// layout pass: `border-top-width` is sixteen bytes and the loop ran for every
// property access of every box. Correctness does not depend on the hash --
// a hit is confirmed by comparing the whole name, and a collision only costs
// another probe -- so this samples the length and four bytes instead. The
// spread is checked: max_probe() reports the longest chain the table holds.
size_t CssPropertyRegistry::hash_name(std::string_view name) {
    const size_t n = name.size();
    if (n == 0) return 0;
    const auto at = [&](size_t i) { return static_cast<unsigned char>(name[i]); };
    size_t h = n * 0x9E3779B97F4A7C15ULL;
    h ^= static_cast<size_t>(at(0)) << 8;
    h ^= static_cast<size_t>(at(n - 1)) << 16;
    h ^= static_cast<size_t>(at(n / 2)) << 24;
    h ^= static_cast<size_t>(at(n / 4)) << 32;
    h *= 0xFF51AFD7ED558CCDULL;
    return h ^ (h >> 29);
}

// The longest probe chain in the table, so a hash that spreads badly shows up
// as a number rather than as a mysterious slowdown.
int CssPropertyRegistry::max_probe() const {
    int worst = 0;
    for (const CssProperty& p : properties_) {
        size_t i = hash_name(p.name) & hash_mask_;
        int steps = 1;
        while (hash_slots_[i] != p.id) {
            i = (i + 1) & hash_mask_;
            ++steps;
        }
        if (steps > worst) worst = steps;
    }
    return worst;
}

void CssPropertyRegistry::rebuild_index() {
    sorted_.clear();
    sorted_.reserve(properties_.size());
    for (const CssProperty& p : properties_) sorted_.emplace_back(p.name, p.id);
    std::sort(sorted_.begin(), sorted_.end(),
              [](const auto& a, const auto& b) { return a.first < b.first; });

    size_t cap = 16;
    while (cap < properties_.size() * 4) cap *= 2;
    hash_slots_.assign(cap, -1);
    hash_mask_ = cap - 1;
    for (const CssProperty& p : properties_) {
        size_t i = hash_name(p.name) & hash_mask_;
        while (hash_slots_[i] != -1) i = (i + 1) & hash_mask_;
        hash_slots_[i] = p.id;
    }
}

int CssPropertyRegistry::register_property(std::string_view name, bool inherited,
                                           std::string_view initial) {
    // Re-registration must keep the existing id: call sites cache ids at
    // startup, and @property can redefine a custom property's initial value
    // while the document is live.
    if (int existing = id_of(name); existing != kCustomPropertyId) {
        properties_[static_cast<std::size_t>(existing)].is_inherited = inherited;
        properties_[static_cast<std::size_t>(existing)].initial_value = std::string(initial);
        return existing;
    }
    CssProperty p;
    p.name = std::string(name);
    p.is_inherited = inherited;
    p.initial_value = std::string(initial);
    p.id = static_cast<int>(properties_.size());
    properties_.push_back(std::move(p));
    rebuild_index();
    return properties_.back().id;
}

int CssPropertyRegistry::id_of(std::string_view name) const {
    if (hash_slots_.empty()) return kCustomPropertyId;
    size_t i = hash_name(name) & hash_mask_;
    while (true) {
        const int id = hash_slots_[i];
        if (id == -1) return kCustomPropertyId;
        if (properties_[static_cast<size_t>(id)].name == name) return id;
        i = (i + 1) & hash_mask_;
    }
}

const CssProperty* CssPropertyRegistry::by_id(int id) const {
    if (id < 0 || id >= static_cast<int>(properties_.size())) return nullptr;
    return &properties_[static_cast<std::size_t>(id)];
}

const CssProperty* CssPropertyRegistry::by_name(std::string_view name) const {
    return by_id(id_of(name));
}

std::string_view CssPropertyRegistry::name_of(int id) const {
    const CssProperty* p = by_id(id);
    return p ? std::string_view(p->name) : std::string_view();
}

bool CssPropertyRegistry::is_inherited(int id) const {
    const CssProperty* p = by_id(id);
    return p && p->is_inherited;
}

std::string_view CssPropertyRegistry::initial_value(int id) const {
    const CssProperty* p = by_id(id);
    return p ? std::string_view(p->initial_value) : std::string_view();
}

} // namespace weva
