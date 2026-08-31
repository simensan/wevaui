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
    occupied_.resize(need, false);
    important_.resize(need, false);
    occupied_bits_.resize((need + 63) / 64, 0);
}

bool ComputedStyle::contains(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= occupied_.size()) return false;
    return occupied_[static_cast<std::size_t>(id)];
}

std::string_view ComputedStyle::get(int id) const {
    if (!contains(id)) return {};
    return values_[static_cast<std::size_t>(id)];
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
    return it == custom_.end() ? std::string_view() : std::string_view(it->second);
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

bool ComputedStyle::contains(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    return custom_.find(std::string(property)) != custom_.end();
}

void ComputedStyle::clear() {
    values_.clear();
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
