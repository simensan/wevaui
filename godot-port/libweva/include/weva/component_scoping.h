#pragma once

// Component-scoped stylesheets. A `<style>` inside `<template id="card">`
// applies to what the template renders and to nothing else: the expander
// stamps every element of a clone with the component's scope attribute and
// the host with the host attribute (before slot projection, so slotted
// light-dom keeps its own attributes), and this module rewrites the sheet's
// selectors so the rightmost compound demands the scope attribute and
// `:host` / `:host(...)` become the host attribute. Byte-identical to the
// reference's SelectorScoper / StylesheetScoper output, so a sheet scoped on
// either side reads the same.

#include "weva/css_rule.h"

#include <string>
#include <string_view>
#include <vector>

namespace weva {

constexpr const char* kComponentScopeAttribute = "data-uui-scope";
constexpr const char* kComponentHostAttribute = "data-uui-host";

// Rewrites a selector list (commas at the top level) into one selector per
// output entry. `.a > .b` becomes `.a > .b[data-uui-scope="id"]`; `:host`
// becomes `[data-uui-host="id"]`, `:host(.x)` `[data-uui-host="id"].x`, and
// `:host(.a, .b)` two selectors. The scope attribute goes before the first
// top-level pseudo of the rightmost compound (`a:hover` ->
// `a[data-uui-scope="id"]:hover`) so it tests the element, not the pseudo.
std::vector<std::string> scope_selector_list(std::string_view selector_list,
                                             std::string_view scope_id);

// Rewrites every style rule's selectors in place, descending through @media,
// @supports, @layer, @container and @scope blocks. @keyframes, @font-face,
// @import and @property are left alone. Rules nested inside a style rule are
// relative to their (now scoped) parent and stay as parsed.
void scope_stylesheet(Stylesheet* sheet, std::string_view scope_id);

} // namespace weva
