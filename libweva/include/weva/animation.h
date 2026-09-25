#pragma once
#include <string>
#include <string_view>

// The two things a transition needs that CSS does not give it for free: a
// curve to run along, and a way to get from one declared value to another.
//
// The properties (`transition-*`, `animation-*`) have always been registered,
// so they cascade and read back; nothing consumed them. This is what consumes
// them.

namespace weva {

// CSS Easing L1 §2. Everything is expressed as a cubic bezier except `steps`,
// which is a staircase, and `linear`, which is the identity.
struct Easing {
    enum class Kind { CubicBezier, Steps } kind = Kind::CubicBezier;
    // Control points; the curve runs (0,0) to (1,1) through (x1,y1),(x2,y2).
    double x1 = 0, y1 = 0, x2 = 1, y2 = 1;
    int steps = 1;
    enum class StepPosition { Start, End, JumpNone, JumpBoth } step_position = StepPosition::End;

    // Progress at `t`, both in 0..1.
    double operator()(double t) const;
};

// `ease`, `linear`, `ease-in`, `ease-out`, `ease-in-out`, `step-start`,
// `step-end`, `cubic-bezier(...)`, `steps(...)`. Anything unreadable leaves
// `out` alone and returns false, so a bad declaration takes the initial curve
// rather than the whole rule.
bool parse_easing(std::string_view raw, Easing* out);

// A duration in seconds from `1.5s` / `300ms`. Negative is legal for a delay
// (it starts the animation part-way through) and not for a duration.
bool parse_time_seconds(std::string_view raw, double* out);

// The value `t` of the way from `from` to `to`, formatted as CSS.
//
// Covers what UI actually transitions: colours, lengths and angles that share
// a unit, plain numbers and percentages, and space- or comma-separated lists
// of those -- which is what carries `padding: 4px 8px` and a two-stop
// `box-shadow`. Anything else is DISCRETE: CSS Transitions L1 §4 says such a
// property flips at the halfway point, and that is what happens here.
//
// Returns false when neither side parses at all, so a caller can leave the
// declared value in place rather than write something worse than it.
bool interpolate_css(std::string_view from, std::string_view to, double t, std::string* out);

}   // namespace weva
