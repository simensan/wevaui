#include "weva/intern.h"

namespace weva {

SymbolTable::SymbolTable() {
    // Slot 0 is kInvalidSymbol so a default-constructed Symbol is never a
    // valid lookup.
    storage_.emplace_back();
    index_.emplace(std::string_view(storage_[0]), kInvalidSymbol);
}

Symbol SymbolTable::intern(std::string_view text) {
    auto it = index_.find(text);
    if (it != index_.end()) return it->second;

    // Copy into stable storage first: the key must point at storage we own,
    // not at the caller's buffer (which is typically a slice of the document
    // source and will outlive nothing).
    Symbol id = static_cast<Symbol>(storage_.size());
    storage_.emplace_back(text);
    index_.emplace(std::string_view(storage_[id]), id);
    return id;
}

Symbol SymbolTable::find(std::string_view text) const {
    auto it = index_.find(text);
    return it == index_.end() ? kInvalidSymbol : it->second;
}

std::string_view SymbolTable::text(Symbol s) const {
    if (s >= storage_.size()) return {};
    return storage_[s];
}

} // namespace weva
