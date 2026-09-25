#pragma once
#include <cstdint>
#include <string>
#include <string_view>

namespace weva {
bool is_temporal_type(std::string_view type);
// Milliseconds from the epoch (time: midnight); month uses months from 1970-01.
// Parsing is allocation-free and independent of the process locale/time zone.
bool parse_temporal_value(std::string_view type, std::string_view value, int64_t* number);
std::string sanitize_temporal_value(std::string_view type, std::string_view value);
} // namespace weva
