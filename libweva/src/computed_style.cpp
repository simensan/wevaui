#include "weva/computed_style.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#if defined(_MSC_VER) && defined(_M_X64)
#include <intrin.h>
#endif

namespace weva {

namespace {
int64_t g_version_counter = 0;
thread_local size_t storage_grows = 0;
thread_local size_t storage_pages = 0;
thread_local double storage_ms = 0;

int64_t next_version() { return ++g_version_counter; }

const std::string kEmpty;

// The caller supplies a nonzero occupancy word.
unsigned first_set_bit(uint64_t word) {
#if defined(__GNUC__) || defined(__clang__)
    return static_cast<unsigned>(__builtin_ctzll(word));
#elif defined(_MSC_VER) && defined(_M_X64)
    unsigned long bit;
    _BitScanForward64(&bit, word);
    return static_cast<unsigned>(bit);
#else
    unsigned bit = 0;
    while (!(word & 1)) { word >>= 1; ++bit; }
    return bit;
#endif
}
}  // namespace

void ComputedStyle::ensure_capacity(int id) {
    auto need = static_cast<std::size_t>(id) + 1;
    if (value_slots_.size() >= need) return;
    static const bool profile = std::getenv("WEVA_CASCADE_LOG") != nullptr;
    using Clock = std::chrono::steady_clock;
    const auto start = profile ? Clock::now() : Clock::time_point{};
    // Size property-indexed metadata to the current registry in one growth.
    // Growing one id at a time made dense cascades repeatedly resize every
    // vector. Raw strings are allocated separately, only as values are set;
    // a sparse style no longer constructs a string for every registered id.
    const auto full = static_cast<std::size_t>(CssPropertyRegistry::instance().count());
    if (full > need) need = full;
    value_slots_.resize(need, 0);
    parsed_.resize(need);
    parsed_ready_.resize(need, false);
    occupied_.resize(need, 0);
    important_.resize(need, false);
    occupied_bits_.resize((need + 63) / 64, 0);
    if (profile) {
        ++storage_grows;
        storage_ms += std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    }
}

void ComputedStyle::report_storage_profile() {
    if (!storage_grows && !storage_pages) return;
    std::fprintf(stderr, "    style allocation: %.3f ms; %zu metadata grows, %zu value pages\n",
                 storage_ms, storage_grows, storage_pages);
    storage_grows = 0;
    storage_pages = 0;
    storage_ms = 0;
}

const std::string& ComputedStyle::own_value(size_t id) const {
    const size_t slot = value_slots_[id] - 1;
    return (*values_[slot / kValuesPerPage])[slot % kValuesPerPage];
}

std::string& ComputedStyle::ensure_value(size_t id) {
    if (!value_slots_[id]) {
        const size_t slot = value_count_++;
        if (slot / kValuesPerPage == values_.size()) {
            static const bool profile = std::getenv("WEVA_CASCADE_LOG") != nullptr;
            using Clock = std::chrono::steady_clock;
            const auto start = profile ? Clock::now() : Clock::time_point{};
            values_.push_back(std::make_unique<ValuePage>());
            if (profile) {
                ++storage_pages;
                storage_ms += std::chrono::duration<double, std::milli>(Clock::now() - start).count();
            }
        }
        value_slots_[id] = static_cast<uint32_t>(slot + 1);
    }
    const size_t slot = value_slots_[id] - 1;
    return (*values_[slot / kValuesPerPage])[slot % kValuesPerPage];
}

bool ComputedStyle::contains(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= occupied_.size()) return false;
    return occupied_[static_cast<std::size_t>(id)] != 0;
}

std::string_view ComputedStyle::get(int id) const {
    if (contains(id)) return own_value(static_cast<std::size_t>(id));
    // Not set here: walk the inherit chain for inherited properties, then fall
    // back to the registry's shared initial value. Neither path copies.
    //
    // ITERATIVELY. Recursing into parent_->get() asked the registry whether the
    // property inherits once per ancestor, and reached the singleton through
    // its guard each time -- so reading an inherited property on a leaf ten
    // deep did ten of both. Whether a property inherits is a fact about the
    // property, so it is settled once and the chain is then just a walk.
    const auto& reg = CssPropertyRegistry::instance();
    if (parent_ && reg.is_inherited(id)) {
        // Walked, not memoised. Caching the ancestor that sets each inherited
        // property looked obviously worth it -- the walk is O(depth) and runs
        // for every read of every inherited property on every box -- and it
        // measured 4 to 6 per cent SLOWER on the larger pages under an
        // interleaved A/B. Real chains are one or two links, so the memo's
        // guard costs more than the walk it skips.
        for (const ComputedStyle* p = parent_; p; p = p->parent_) {
            if (p->contains(id)) return p->own_value(static_cast<std::size_t>(id));
        }
    }
    return reg.initial_value(id);
}

bool ComputedStyle::try_get(int id, std::string* out) const {
    if (!contains(id)) return false;
    *out = own_value(static_cast<std::size_t>(id));
    return true;
}

void ComputedStyle::set(int id, std::string_view value) {
    if (id < 0) return;
    ensure_capacity(id);
    auto i = static_cast<std::size_t>(id);
    // A no-op write must not bump the version — the whole invalidation
    // architecture keys caches on version numbers, so a spurious bump costs a
    // re-cascade of everything downstream.
    bool source_changed = false;
    if (font_size_inherited_ && id == CssPropertyRegistry::instance().id_of("font-size")) {
        font_size_inherited_ = false;
        source_changed = true;
    }
    if (line_height_inherited_ && id == CssPropertyRegistry::instance().id_of("line-height")) {
        line_height_inherited_ = false;
        source_changed = true;
    }
    if (occupied_[i] != 0 && own_value(i) == value) {
        if (source_changed) version_ = next_version();
        return;
    }
    if (occupied_[i] == 0) {
        occupied_[i] = 1;
        occupied_bits_[i >> 6] |= 1ULL << (i & 63);
        ++set_count_;
    }
    // assign(), not a constructed temporary: `= std::string(value)` builds a
    // string, allocates for anything past the small-string buffer, moves it
    // in and frees the slot's existing allocation. assign() reuses the
    // capacity the slot already has, which after the first pass is usually
    // enough.
    ensure_value(i).assign(value.data(), value.size());
    // The memo describes the old string.
    parsed_[i].reset();
    parsed_ready_[i] = false;
    version_ = next_version();
}

void ComputedStyle::mark_font_size_inherited() {
    if (font_size_inherited_) return;
    font_size_inherited_ = true;
    version_ = next_version();
}

void ComputedStyle::mark_line_height_inherited() {
    if (line_height_inherited_) return;
    line_height_inherited_ = true;
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
    // Custom properties inherit by default (CSS Custom Properties L1 §2).
    // `@property inherits: false` is honoured by the cascade stamping the
    // descriptor's initial value directly onto every element, so this walk
    // never reaches an ancestor for such a property.
    for (const ComputedStyle* style = this; style; style = style->parent_) {
        const auto it = style->custom_.find(property);
        if (it != style->custom_.end()) return it->second;
    }
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
    return custom_.find(property) != custom_.end();
}

bool ComputedStyle::contains(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    for (const ComputedStyle* style = this; style; style = style->parent_) {
        if (style->custom_.find(property) != style->custom_.end()) return true;
    }
    return false;
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
    occupied_[i] = 0;
    occupied_bits_[i >> 6] &= ~(1ULL << (i & 63));
    ensure_value(i).clear();
    parsed_[i].reset();
    parsed_ready_[i] = false;
    important_[i] = false;
    if (font_size_inherited_ && id == CssPropertyRegistry::instance().id_of("font-size"))
        font_size_inherited_ = false;
    if (line_height_inherited_ && id == CssPropertyRegistry::instance().id_of("line-height"))
        line_height_inherited_ = false;
    --set_count_;
    version_ = next_version();
}

void ComputedStyle::clear() {
    parent_ = nullptr;
    font_size_inherited_ = false;
    line_height_inherited_ = false;
    // Keep stable pages and their string capacity for the next cascade into
    // this scratch style. Walk owned pages rather than value_count_: default
    // move transfers the pages but leaves scalar counts in the source, which
    // must still support clear() before being reused.
    for (auto& page : values_)
        for (auto& value : *page) value.clear();
    value_slots_.clear();
    value_count_ = 0;
    parsed_.clear();
    parsed_ready_.clear();
    occupied_.clear();
    occupied_bits_.clear();
    important_.clear();
    custom_.clear();
    set_count_ = 0;
    version_ = next_version();
}

bool ComputedStyle::differs_from(const ComputedStyle& other, std::vector<int>* changed_ids,
                                 bool* unattributed) const {
    static const int kFontSize = CssPropertyRegistry::instance().id_of("font-size");
    static const int kLineHeight = CssPropertyRegistry::instance().id_of("line-height");
    bool any = false;
    // Neither of these names a property, so a caller cannot narrow what the
    // difference affects. A custom property reaches whatever var() read it,
    // and those reads were substituted during the cascade -- so a change that
    // mattered also shows up as a changed value below, and one that did not
    // still has to be reported, because nothing here can prove it did not.
    if (parent_ != other.parent_ || custom_ != other.custom_) {
        any = true;
        if (unattributed) *unattributed = true;
    }
    const std::size_t n = std::max(occupied_.size(), other.occupied_.size());
    for (std::size_t i = 0; i < n; ++i) {
        const bool a = i < occupied_.size() && occupied_[i] != 0;
        const bool b = i < other.occupied_.size() && other.occupied_[i] != 0;
        if (!a && !b) continue;
        bool same;
        if (a == b) {
            same = own_value(i) == other.own_value(i) &&
                   (i < important_.size() && important_[i]) ==
                       (i < other.important_.size() && other.important_[i]);
        } else {
            // Set on one side and not the other is not yet a difference: a
            // property set to what it would have been anyway computes the
            // same. Asking for the VALUE settles it, and the question here is
            // whether the computed value changed -- nothing downstream can
            // tell how it was arrived at.
            //
            // Comparing set-ness alone reported `padding: 0` against an unset
            // padding as a change, and one such report costs a relayout of the
            // whole document.
            same = get(static_cast<int>(i)) == other.get(static_cast<int>(i));
        }
        // Font-size stores syntax. An owned 2em and an inherited 2em have
        // different bases even though get() returns identical text.
        if (static_cast<int>(i) == kFontSize &&
            (a != b || font_size_inherited_ != other.font_size_inherited_)) same = false;
        if (static_cast<int>(i) == kLineHeight &&
            (a != b || line_height_inherited_ != other.line_height_inherited_)) same = false;
        if (same) continue;
        any = true;
        if (changed_ids) changed_ids->push_back(static_cast<int>(i));
    }
    return any;
}

std::vector<int> ComputedStyle::set_ids() const {
    std::vector<int> out;
    copy_set_ids(out);
    return out;
}

void ComputedStyle::copy_set_ids(std::vector<int>& out) const {
    out.clear();
    out.reserve(static_cast<size_t>(set_count_));
    // Resolution passes visit only declarations this style owns. Iterate the
    // existing occupancy words, preserving ascending property order, instead
    // of scanning every registered id and repeatedly growing the result.
    for (size_t i = 0; i < occupied_bits_.size(); ++i) {
        uint64_t word = occupied_bits_[i];
        while (word) {
            out.push_back(static_cast<int>(i * 64 + first_set_bit(word)));
            word &= word - 1;
        }
    }
}

} // namespace weva
