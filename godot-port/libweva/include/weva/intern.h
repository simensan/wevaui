#pragma once
#include <cstdint>
#include <deque>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace weva {

// Interned symbol table for identifiers, property names, keywords and class
// names. Compare by id, never by bytes — the C# side already does this via
// CssProperties.GetId.
//
// This is also the escape hatch from the string_view lifetime rule: a slice
// into the document's source buffer is fine during a parse, but anything
// retained past the pass must own its storage or be interned. Interning is
// almost always the right answer.
using Symbol = uint32_t;
constexpr Symbol kInvalidSymbol = 0;

class SymbolTable {
public:
    SymbolTable();

    // Interns a slice. The returned Symbol outlives the source buffer.
    Symbol intern(std::string_view text);

    // Lookup without interning; kInvalidSymbol if unseen.
    Symbol find(std::string_view text) const;

    std::string_view text(Symbol s) const;
    std::size_t size() const { return storage_.size(); }

private:
    // deque, NOT vector: index_ holds string_views pointing into these
    // strings, and vector reallocation moves short (SSO) strings' character
    // data with the object, dangling every key. deque guarantees reference
    // stability across push_back.
    std::deque<std::string> storage_;
    std::unordered_map<std::string_view, Symbol> index_;
};

} // namespace weva
