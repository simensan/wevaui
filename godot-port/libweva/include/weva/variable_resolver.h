#pragma once
#include "weva/computed_style.h"

#include <string>
#include <string_view>

// Ports Runtime/Css/Cascade/VariableResolver.cs — var() substitution with
// fallbacks and CSS Custom Properties L1 §3.1 cycle handling.

namespace weva {

// Substitutes every var() in `value` against `style`'s custom properties.
//
// Returns false when the declaration becomes "invalid at computed-value time"
// (§3): a var() that resolves to nothing and has no usable fallback taints the
// WHOLE declaration, and the cascade must then drop it so the property falls
// back to its inherited or initial value. That is why this reports a bool
// rather than just returning a best-effort string — silently substituting an
// empty string would leave a syntactically broken declaration in place.
bool resolve_variables(std::string_view value, const ComputedStyle& style,
                       std::string* resolved);

} // namespace weva
