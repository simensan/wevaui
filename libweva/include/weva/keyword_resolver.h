#pragma once
#include "weva/cascade.h"
#include "weva/computed_style.h"

#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/KeywordResolver.cs — CSS Cascade L4 §7.5 and L5
// §7.4/§7.5: `inherit`, `initial`, `unset`, `revert`, `revert-layer`.
//
// Resolution runs LAST, after var()/env()/attr() substitution, so `inherit`
// yields the parent's already-substituted computed value.

namespace weva {

// `value` is matched case-insensitively but is expected already trimmed.
bool is_css_wide_keyword(std::string_view value);

// Resolves a CSS-wide keyword for a registered property. Returns false when
// `value` is not one, leaving `out` untouched.
//
// `unset` is `inherit` for an inherited property and `initial` otherwise, and
// `revert`/`revert-layer` reaching here means the rollback below found no
// lower-priority match — the spec collapses both to `initial`.
bool resolve_css_wide_keyword(int property_id, std::string_view value,
                              const ComputedStyle* parent, std::string* out);

// Same, for a custom property. Every custom property is inherited and its
// initial value is the empty string, including one registered via `@property`:
// the descriptor's initial-value is applied afterwards, by the syntax check
// rejecting the empty string.
bool resolve_css_wide_keyword_custom(std::string_view name, std::string_view value,
                                     const ComputedStyle* parent, std::string* out);

// CSS Cascade L5 §7.4/§7.5 rollback. Substitutes the value text of the
// appropriate lower-priority match before the keyword resolver runs, so a
// rolled-back value that is itself `inherit` (or another `revert`) resolves
// correctly.
//
// `expanded` is the element's full match list in cascade order. Returns `value`
// unchanged when it is not a rollback keyword or when no target exists — the
// resolver then maps it to `initial`.
std::string pre_resolve_rollback(std::string_view property, std::string_view value,
                                 const std::vector<MatchedDeclaration>& expanded,
                                 const CascadeKey& winner);

} // namespace weva
