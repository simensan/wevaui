#pragma once
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

namespace weva {
// Input time is supplied by the caller, independently of animation time. This
// keeps timeout behavior deterministic in tests and active in paused scenes.
class TypeAheadSession {
public:
    bool active(double now) const;
    void reset();
    // Returns true when a search should begin after the current option.
    bool append(std::string_view character, double now);
    std::string_view prefix() const { return prefix_; }
private:
    std::string buffer_, prefix_, repeating_;
    double last_input_ = -1;
};

// Unicode root/en collation at primary strength. Searches only the beginning
// of a label after its leading whitespace, without splitting expansions.
// Owns ICU's referenced text buffers; no ICU types cross a public boundary.
class UnicodePrefixSearch {
public:
    explicit UnicodePrefixSearch(std::string_view prefix);
    ~UnicodePrefixSearch();
    bool matches(std::string_view label);
    bool valid() const;
    UnicodePrefixSearch(const UnicodePrefixSearch&) = delete;
    UnicodePrefixSearch& operator=(const UnicodePrefixSearch&) = delete;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
bool typeahead_printable(std::string_view character);
} // namespace weva
