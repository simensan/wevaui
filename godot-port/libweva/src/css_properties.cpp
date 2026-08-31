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

void CssPropertyRegistry::rebuild_index() {
    sorted_.clear();
    sorted_.reserve(properties_.size());
    for (const CssProperty& p : properties_) sorted_.emplace_back(p.name, p.id);
    std::sort(sorted_.begin(), sorted_.end(),
              [](const auto& a, const auto& b) { return a.first < b.first; });
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
    auto it = std::lower_bound(sorted_.begin(), sorted_.end(), name,
                               [](const auto& e, std::string_view n) { return e.first < n; });
    if (it == sorted_.end() || it->first != name) return kCustomPropertyId;
    return it->second;
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
