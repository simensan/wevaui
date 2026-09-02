#pragma once
#include "weva/css_rule.h"

#include <map>
#include <string>
#include <vector>

// @keyframes, promoted from the generic at-rule the parser leaves them as.
//
// Nothing had to change in the parser: a deferred at-rule keeps its prelude
// and its body, and a keyframes body is exactly a list of style rules whose
// "selectors" are offsets. So this reads what is already there.

namespace weva {

struct Keyframe {
    double offset = 0;   // 0..1; `from` is 0 and `to` is 1
    std::vector<Declaration> declarations;
};

struct KeyframeAnimation {
    std::string name;
    // Ascending by offset, so a lookup is a walk to the first one past `t`.
    std::vector<Keyframe> frames;
    // Every property any frame mentions, so a running animation knows what it
    // is responsible for without re-scanning.
    std::vector<std::string> properties;
};

// Collects every @keyframes rule in a sheet. A later definition of the same
// name replaces an earlier one, as the cascade of the rule itself would.
void collect_keyframes(const Stylesheet& sheet,
                       std::map<std::string, KeyframeAnimation>* out);

// The value of `property` at `t` (0..1) through `animation`, or false when the
// animation does not touch that property at all. Interpolates between the
// surrounding frames; before the first and after the last it holds the nearest.
bool keyframe_value_at(const KeyframeAnimation& animation, std::string_view property, double t,
                       std::string* out);

}   // namespace weva
