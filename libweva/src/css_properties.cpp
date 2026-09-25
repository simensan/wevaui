#include "weva/css_properties.h"

#include <algorithm>

namespace weva {

namespace {
struct PropertyDef { const char* name; bool inherited; const char* initial; };
#include "generated/css_properties.inc"
}  // namespace

// The built-in list holds no duplicate names, so the table is filled directly
// and indexed ONCE. Going through register_property rebuilt the whole index
// after every one of the 334 names -- 334 index builds, each hashing every
// name so far -- and that construction was a quarter of the instructions in
// the first cold load of layout-stress.
CssPropertyRegistry::CssPropertyRegistry() {
    properties_.reserve(sizeof(kProperties) / sizeof(kProperties[0]));
    for (const PropertyDef& d : kProperties) {
        CssProperty p;
        p.name = d.name;
        p.is_inherited = d.inherited;
        p.initial_value = d.initial;
        p.id = static_cast<int>(properties_.size());
        properties_.push_back(std::move(p));
    }
    rebuild_index();
}

CssPropertyRegistry& CssPropertyRegistry::construct_instance() {
    static CssPropertyRegistry r;
    instance_.store(&r, std::memory_order_release);
    return r;
}

std::atomic<CssPropertyRegistry*> CssPropertyRegistry::instance_{nullptr};

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
    size_t cap = 16;
    while (cap < properties_.size() * 4) cap *= 2;
    inherited_.assign(properties_.size(), 0);
    initial_views_.assign(properties_.size(), std::string_view());
    for (const CssProperty& p : properties_) {
        if (p.id < 0 || p.id >= static_cast<int>(inherited_.size())) continue;
        inherited_[static_cast<std::size_t>(p.id)] = p.is_inherited ? 1 : 0;
        initial_views_[static_cast<std::size_t>(p.id)] = p.initial_value;
    }
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
        const auto i = static_cast<std::size_t>(existing);
        properties_[i].is_inherited = inherited;
        properties_[i].initial_value = std::string(initial);
        // The side tables have to follow. They did not: re-registering a
        // property with a different `inherits` left is_inherited() answering
        // from the flag captured when the property was FIRST registered, which
        // is precisely what `@property { inherits: false }` redefining an
        // already-registered custom property does. The initial-value view also
        // has to be refreshed, because assigning the string above can move its
        // buffer out from under the old one.
        if (i < inherited_.size()) {
            inherited_[i] = inherited ? 1 : 0;
            initial_views_[i] = properties_[i].initial_value;
        }
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

} // namespace weva
