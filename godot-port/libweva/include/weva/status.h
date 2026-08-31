#pragma once

namespace weva {

// The C# core throws in 509 places. Throwing across a GDExtension boundary is
// undefined, so errors return instead. Most translations are mechanical: the
// C# side already has 813 TryParse-family call sites that lean this way.
//
// Allocation failure is deliberately absent. Arenas abort on exhaustion rather
// than threading a recoverable error through every layout call site.
enum class Status {
    Ok = 0,
    Syntax,       // malformed input; the parser could not proceed
    Unsupported,  // well-formed but not implemented (a CSS property we skip)
    OutOfRange,   // parsed, but outside the value's legal domain
    NotFound,     // lookup miss (unknown property id, missing element)
};

inline bool ok(Status s) { return s == Status::Ok; }

} // namespace weva
