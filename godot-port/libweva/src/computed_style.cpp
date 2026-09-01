#include "weva/computed_style.h"

namespace weva {

namespace {
int64_t next_version() {
    static int64_t counter = 0;
    return ++counter;
}
const std::string kEmpty;
}  // namespace

void ComputedStyle::ensure_capacity(int id) {
    auto need = static_cast<std::size_t>(id) + 1;
    if (values_.size() >= need) return;
    values_.resize(need);
    parsed_.resize(need);
    parsed_ready_.resize(need, false);
    occupied_.resize(need, false);
    important_.resize(need, false);
    occupied_bits_.resize((need + 63) / 64, 0);
}

bool ComputedStyle::contains(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= occupied_.size()) return false;
    return occupied_[static_cast<std::size_t>(id)];
}

std::string_view ComputedStyle::get(int id) const {
    if (contains(id)) return values_[static_cast<std::size_t>(id)];
    // Not set here: walk the inherit chain for inherited properties, then fall
    // back to the registry's shared initial value. Neither path copies.
    const auto& reg = CssPropertyRegistry::instance();
    if (parent_ && reg.is_inherited(id)) return parent_->get(id);
    return reg.initial_value(id);
}

bool ComputedStyle::try_get(int id, std::string* out) const {
    if (!contains(id)) return false;
    *out = values_[static_cast<std::size_t>(id)];
    return true;
}

void ComputedStyle::set(int id, std::string_view value) {
    if (id < 0) return;
    ensure_capacity(id);
    auto i = static_cast<std::size_t>(id);
    // A no-op write must not bump the version — the whole invalidation
    // architecture keys caches on version numbers, so a spurious bump costs a
    // re-cascade of everything downstream.
    if (occupied_[i] && values_[i] == value) return;
    if (!occupied_[i]) {
        occupied_[i] = true;
        occupied_bits_[i >> 6] |= 1ULL << (i & 63);
        ++set_count_;
    }
    values_[i] = std::string(value);
    // The memo describes the old string.
    parsed_[i].reset();
    parsed_ready_[i] = false;
    version_ = next_version();
}

bool ComputedStyle::is_important(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= important_.size()) return false;
    return important_[static_cast<std::size_t>(id)];
}

void ComputedStyle::set_important(int id, bool important) {
    if (id < 0) return;
    ensure_capacity(id);
    important_[static_cast<std::size_t>(id)] = important;
}

std::string_view ComputedStyle::get(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return get(id);
    auto it = custom_.find(std::string(property));
    if (it != custom_.end()) return it->second;
    // Custom properties inherit by default (CSS Custom Properties L1 §2).
    // `@property inherits: false` is honoured by the cascade stamping the
    // descriptor's initial value directly onto every element, so this walk
    // never reaches an ancestor for such a property.
    if (parent_) return parent_->get(property);
    return {};
}

void ComputedStyle::set(std::string_view property, std::string_view value) {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) { set(id, value); return; }
    std::string key(property);
    auto it = custom_.find(key);
    if (it != custom_.end() && it->second == value) return;   // no-op, no bump
    custom_[key] = std::string(value);
    version_ = next_version();
}

bool ComputedStyle::contains_own(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    return custom_.find(std::string(property)) != custom_.end();
}

bool ComputedStyle::contains(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    if (custom_.find(std::string(property)) != custom_.end()) return true;
    return parent_ && parent_->contains(property);
}

const CssValue* ComputedStyle::parsed(int id) const {
    if (id < 0) return nullptr;
    const auto& reg = CssPropertyRegistry::instance();
    if (!contains(id)) {
        // Share the ancestor's entry rather than parsing the same inherited
        // string again at every level of the subtree.
        if (parent_ && reg.is_inherited(id)) return parent_->parsed(id);
        // An initial value is shared and immutable, but the memo lives on the
        // style, so it is cached here like any other slot. The registry has no
        // storage of its own to hang it from.
    }
    const auto i = static_cast<std::size_t>(id);
    if (i >= parsed_ready_.size()) {
        // ensure_capacity is non-const; the memo is the only mutable state, so
        // it is grown directly.
        const std::size_t need = i + 1;
        parsed_.resize(need);
        parsed_ready_.resize(need, false);
    }
    if (!parsed_ready_[i]) {
        CssParseError err;
        const std::string_view raw = get(id);
        parsed_[i] = raw.empty() ? nullptr : parse_css_value(raw, &err);
        parsed_ready_[i] = true;
    }
    return parsed_[i].get();
}

const CssValue* ComputedStyle::parsed(std::string_view property) const {
    const int id = CssPropertyRegistry::instance().id_of(property);
    // A custom property has no slot to memoise against; it is parsed on demand
    // by the variable resolver rather than here.
    if (id == kCustomPropertyId) return nullptr;
    return parsed(id);
}

void ComputedStyle::unset(int id) {
    if (!contains(id)) return;
    auto i = static_cast<std::size_t>(id);
    occupied_[i] = false;
    occupied_bits_[i >> 6] &= ~(1ULL << (i & 63));
    values_[i].clear();
    parsed_[i].reset();
    parsed_ready_[i] = false;
    important_[i] = false;
    --set_count_;
    version_ = next_version();
}

void ComputedStyle::clear() {
    parent_ = nullptr;
    values_.clear();
    parsed_.clear();
    parsed_ready_.clear();
    occupied_.clear();
    occupied_bits_.clear();
    important_.clear();
    custom_.clear();
    set_count_ = 0;
    version_ = next_version();
}

std::vector<int> ComputedStyle::set_ids() const {
    std::vector<int> out;
    for (std::size_t i = 0; i < occupied_.size(); ++i) {
        if (occupied_[i]) out.push_back(static_cast<int>(i));
    }
    return out;
}

} // namespace weva
