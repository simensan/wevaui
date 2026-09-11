#include "weva/typeahead.h"
#include "unicode/uchar.h"
#include "unicode/ucol.h"
#include "unicode/usearch.h"
#include "unicode/ustring.h"
#include "unicode/utf8.h"
#include <cmath>
#include <limits>
#include <vector>

namespace weva {
bool TypeAheadSession::active(double now) const {
    return last_input_ >= 0 && now >= last_input_ && now - last_input_ <= 1.0;
}
void TypeAheadSession::reset() {
    buffer_.clear(); prefix_.clear(); repeating_.clear(); last_input_ = -1;
}
bool TypeAheadSession::append(std::string_view character, double now) {
    if (!active(now)) buffer_.clear();
    last_input_ = now;
    const bool first = buffer_.empty();
    buffer_.append(character);
    const bool cycle = character == repeating_;
    prefix_ = cycle ? std::string(character) : buffer_;
    if (first) repeating_ = character;
    else if (!cycle) repeating_.clear();
    return first || cycle;
}

namespace {
bool utf16(std::string_view text, std::vector<char16_t>* out) {
    if (text.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max())) return false;
    out->resize(text.size()); // UTF-16 needs at most as many units as UTF-8 bytes.
    int32_t length = 0;
    UErrorCode error = U_ZERO_ERROR;
    u_strFromUTF8WithSub(out->data(), static_cast<int32_t>(out->size()), &length,
                        text.data(), static_cast<int32_t>(text.size()), 0xFFFD, nullptr, &error);
    if (U_FAILURE(error)) return false;
    out->resize(static_cast<size_t>(length));
    return true;
}
std::string_view strip_leading_space(std::string_view text) {
    while (!text.empty()) {
        int32_t at = 0;
        UChar32 cp = 0;
        U8_NEXT(text.data(), at, static_cast<int32_t>(std::min<size_t>(text.size(), 4)), cp);
        if (cp != 0xA0 && !u_isWhitespace(cp)) break;
        text.remove_prefix(static_cast<size_t>(at));
    }
    return text;
}
}
bool typeahead_printable(std::string_view character) {
    if (character.empty() || character.size() > 4) return false;
    int32_t at = 0;
    UChar32 cp = 0;
    U8_NEXT(character.data(), at, static_cast<int32_t>(character.size()), cp);
    return at == static_cast<int32_t>(character.size()) && cp >= 0 && u_isprint(cp);
}

struct UnicodePrefixSearch::Impl {
    std::vector<char16_t> pattern, text;
    UStringSearch* search = nullptr;
    ~Impl() { if (search) usearch_close(search); }
};
UnicodePrefixSearch::UnicodePrefixSearch(std::string_view prefix) : impl_(std::make_unique<Impl>()) {
    if (!utf16(prefix, &impl_->pattern) || impl_->pattern.empty()) return;
    impl_->text = impl_->pattern;
    UErrorCode error = U_ZERO_ERROR;
    impl_->search = usearch_open(impl_->pattern.data(), static_cast<int32_t>(impl_->pattern.size()),
                                impl_->text.data(), static_cast<int32_t>(impl_->text.size()),
                                "en", nullptr, &error);
    if (U_FAILURE(error)) { if (impl_->search) usearch_close(impl_->search); impl_->search = nullptr; return; }
    ucol_setStrength(usearch_getCollator(impl_->search), UCOL_PRIMARY);
    usearch_reset(impl_->search);
}
UnicodePrefixSearch::~UnicodePrefixSearch() = default;
bool UnicodePrefixSearch::valid() const { return impl_->search != nullptr; }
bool UnicodePrefixSearch::matches(std::string_view label) {
    if (!valid()) return false;
    // Vector swap transfers the buffer even for short labels. String's small
    // buffer optimization would leave ICU retaining a temporary stack buffer.
    std::vector<char16_t> next;
    if (!utf16(strip_leading_space(label), &next) || next.empty()) return false;
    UErrorCode error = U_ZERO_ERROR;
    usearch_setText(impl_->search, next.data(), static_cast<int32_t>(next.size()), &error);
    if (U_FAILURE(error)) return false;
    impl_->text.swap(next); // ICU retains the new buffer; the old one can now die.
    const int32_t index = usearch_first(impl_->search, &error);
    return U_SUCCESS(error) && index == 0;
}
} // namespace weva
