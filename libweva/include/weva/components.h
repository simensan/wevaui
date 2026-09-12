#pragma once

// Weva components: `<template id="card">` declares a component, `<card>` uses
// it, and `<slot>` inside the template is where the host's children land.
// Mirrors the reference's ComponentRegistry + ComponentExpander +
// SlotProjection, which UIDocumentBuilder runs BEFORE the cascade so expanded
// subtrees are present when selectors match.
//
// Component-scoped stylesheets: a `<style>` inside a registered template is
// the component's own sheet. The expander stamps every element of a clone
// with `data-uui-scope="<id>"` BEFORE projection, so slot-projected light-dom
// keeps its own attributes and the component's rules do not match it, and
// the host with `data-uui-host="<id>"`; the sheet's selectors are rewritten
// against those attributes by component_scoping.h. The scope id is the
// template's lowercased id. The reference reaches scoped sheets only through
// ComponentRegistry.Register(tag, template, sheet) from code; here the markup
// carries them.

#include "weva/dom.h"

#include <string>
#include <vector>

namespace weva {

// The `<style>` text found inside `<template id="tag">`, concatenated in
// document order (nested templates excluded), with the scope id the
// expander stamped that component's clones with.
struct ComponentStylesheet {
    std::string tag;
    std::string scope_id;
    std::string css;
};

// Registers every top-level `<template id=...>` in the document and expands
// every host element that names one. Idempotent: a host already carrying the
// expanded marker is left alone, which is also what stops a template whose body
// root repeats the host's tag from recursing into itself.
//
// `max_depth` bounds template cycles. On overflow the offending host is left
// un-expanded rather than throwing, since this runs inside a layout pipeline
// that has no error channel to the author.
void expand_components(Document* doc, int max_depth = 32);

// The same, also reporting each registered template's scoped stylesheet
// (only templates that carry a `<style>` are reported and stamp a scope).
void expand_components(Document* doc, std::vector<ComponentStylesheet>* sheets, int max_depth = 32);

} // namespace weva
