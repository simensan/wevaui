#pragma once

// Weva components: `<template id="card">` declares a component, `<card>` uses
// it, and `<slot>` inside the template is where the host's children land.
// Mirrors the reference's ComponentRegistry + ComponentExpander +
// SlotProjection, which UIDocumentBuilder runs BEFORE the cascade so expanded
// subtrees are present when selectors match.
//
// Not implemented here: the scope stamping the reference does for
// component-scoped stylesheets (`ScopeMarkers`). Nothing in the port consumes
// a component stylesheet yet, and the attributes are otherwise inert; when
// scoped sheets arrive, the stamp has to go on the CLONE before projection, so
// that slot-projected light-dom keeps its original attributes and the
// component's own rules do not match it.

#include "weva/dom.h"

namespace weva {

// Registers every top-level `<template id=...>` in the document and expands
// every host element that names one. Idempotent: a host already carrying the
// expanded marker is left alone, which is also what stops a template whose body
// root repeats the host's tag from recursing into itself.
//
// `max_depth` bounds template cycles. On overflow the offending host is left
// un-expanded rather than throwing, since this runs inside a layout pipeline
// that has no error channel to the author.
void expand_components(Document* doc, int max_depth = 32);

} // namespace weva
