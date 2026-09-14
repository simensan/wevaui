#pragma once
#include "weva/css_rule.h"

#include <functional>
#include <string>
#include <string_view>

// CSS Cascade 5 §4: `@import`. Ports Runtime/Css/Parsing/AtImportLoader.cs.
//
// The parser leaves an `@import` as a statement at-rule; the cascade cannot
// fetch anything, and nothing else should read one. This splices the
// imported sheet's rules in place of the statement -- under a `@media`,
// `@supports` or `@layer` wrapper when the import carries a condition or a
// layer name -- so the cascade sees a plain sheet. Imports nest; a sheet
// that imports itself, directly or through another, is loaded once, and
// the nesting stops at a small depth so a hostile sheet cannot recurse.

namespace weva {

// Loads the CSS text at `url`, with nested references resolved against their
// importing stylesheet and dot segments removed. A relative result still
// belongs to the document's base path (the asset reader's convention). False
// when the sheet cannot be read; the import is then dropped, which is what a
// browser does with a sheet that fails to load.
using StylesheetLoader = std::function<bool(std::string_view url, std::string* css)>;

// Expands every top-level `@import` in `sheet` in place. Returns how many
// imports were resolved; the ones that failed to load are listed in
// `missing` when it is given.
int expand_imports(Stylesheet* sheet, const StylesheetLoader& load,
                   std::vector<std::string>* missing = nullptr);

} // namespace weva
