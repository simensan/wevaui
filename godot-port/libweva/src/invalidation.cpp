#include "weva/invalidation.h"

#include "weva/css_properties.h"

#include <vector>

namespace weva {

namespace {

// Properties nothing outside paint reads.
//
// Verified by searching every layout source -- block, inline, flex, grid,
// table, positioning and box building -- for a read of each name. Four
// plausible candidates failed that search and are deliberately absent:
//
//   filter       positioning: a filter establishes a containing block
//   transform    positioning: so does a transform
//   z-index      positioning: stacking order
//   visibility   table layout: `collapse` removes a row
//
// A name that is not a registered property resolves to no id and is simply
// not in the table, which costs nothing but the entry.
const char* const kPaintOnly[] = {
    "color", // Text, decorations and currentColor read the retained style during paint.
    "background",       "background-color",    "background-image",
    "background-position", "background-position-x", "background-position-y",
    "background-size",  "background-repeat",   "background-clip",
    "background-origin", "background-attachment", "background-blend-mode",

    "border-color",     "border-top-color",    "border-right-color",
    "border-bottom-color", "border-left-color",

    "border-radius",    "border-top-left-radius", "border-top-right-radius",
    "border-bottom-right-radius", "border-bottom-left-radius",

    "box-shadow",       "text-shadow",

    "outline",          "outline-color",       "outline-style",
    "outline-width",    "outline-offset",

    "opacity",          "backdrop-filter",     "mix-blend-mode",
    "clip-path",

    "cursor",           "pointer-events",      "caret-color",
    "accent-color",     "object-fit",          "object-position",
};

// Indexed by property id: true when a change to it repaints and nothing more.
// Built once, from the registry, so the hot path is an array read.
const std::vector<bool>& paint_only_table() {
    static const std::vector<bool> table = [] {
        const CssPropertyRegistry& reg = CssPropertyRegistry::instance();
        std::vector<bool> t(reg.count(), false);
        for (const char* name : kPaintOnly) {
            const int id = reg.id_of(name);
            if (id >= 0 && id < static_cast<int>(t.size())) t[static_cast<size_t>(id)] = true;
        }
        return t;
    }();
    return table;
}

// Properties box building reads.
//
// Derived the same way as the list above, but from the other end: every
// property name box_builder.cpp reads. A change to one of these can change
// which boxes exist; a change to anything else can only move or repaint the
// boxes already there. The builder includes no layout header and reads styles
// nowhere else, so the list is the whole of it.
const char* const kBoxAffecting[] = {
    "display",  "position", "float",           "content",
    "quotes",   "white-space", "text-transform",
    "column-count", "column-width",
    "counter-reset", "counter-increment", "counter-set",
};

const std::vector<bool>& box_affecting_table() {
    static const std::vector<bool> table = [] {
        const CssPropertyRegistry& reg = CssPropertyRegistry::instance();
        std::vector<bool> t(reg.count(), false);
        for (const char* name : kBoxAffecting) {
            const int id = reg.id_of(name);
            if (id >= 0 && id < static_cast<int>(t.size())) t[static_cast<size_t>(id)] = true;
        }
        return t;
    }();
    return table;
}

}   // namespace

Invalidation invalidation_for_property(int property_id) {
    if (property_id < 0) return Invalidation::Boxes;
    const std::vector<bool>& paint = paint_only_table();
    const size_t i = static_cast<size_t>(property_id);
    if (i >= paint.size()) return Invalidation::Boxes;
    if (paint[i]) return Invalidation::Paint;
    return box_affecting_table()[i] ? Invalidation::Boxes : Invalidation::Layout;
}

const std::vector<int>& paint_only_property_ids() {
    static const std::vector<int> ids = [] {
        const std::vector<bool>& t = paint_only_table();
        std::vector<int> out;
        for (size_t i = 0; i < t.size(); ++i) {
            if (t[i]) out.push_back(static_cast<int>(i));
        }
        return out;
    }();
    return ids;
}

}   // namespace weva
