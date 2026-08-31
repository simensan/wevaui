#pragma once
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/Shorthands — expanding a shorthand declaration
// into its longhands.
//
// This happens inside the cascade, and the shorthand declaration is DROPPED, so
// each emitted longhand carries the shorthand's cascade key and ordinary source
// order settles a shorthand-versus-longhand conflict. `{ padding: 5px;
// padding-left: 20px }` is 5/5/5/20, not 0/0/0/20.
//
// A malformed value expands to nothing and the shorthand is still dropped, so
// the affected longhands keep whatever the previous declaration gave them —
// which is what "the declaration is invalid and ignored" means here.

namespace weva {

struct ShorthandLonghand {
    std::string_view property;   // always a static name
    std::string value;
};

bool is_shorthand(std::string_view name);

// Returns false when `name` is not a shorthand, leaving `out` untouched.
// Returns true with an EMPTY `out` when the value is malformed for this
// shorthand.
bool expand_shorthand(std::string_view name, std::string_view value,
                      std::vector<ShorthandLonghand>* out);

// Splits a shorthand value into top-level tokens: a parenthesised group is one
// token, a quoted string is one token including its quotes, and `,` and `/` are
// tokens of their own.
std::vector<std::string_view> tokenize_shorthand(std::string_view value);

// CSS Values L4 §6.2/§6.3: expansion has to wait until var() and attr() are
// substituted, because the expander cannot tokenise an unresolved reference.
// The reference engine never revisits such a declaration, so it stays a
// shorthand for good — which is why the layout side still reads shorthands.
bool contains_substitution(std::string_view value);

} // namespace weva
