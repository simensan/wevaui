#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include "weva/positioning.h"

#include <optional>
#include <string_view>

// CSS Anchor Positioning L1 — the `anchor()` and `anchor-size()` halves.
//
// An element names itself an anchor with `anchor-name: --tip`. An absolutely
// positioned element picks one with `position-anchor: --tip`, and can then
// write its insets and its size in terms of that element's border box:
//
//     .tip { position: absolute; position-anchor: --tip;
//            left: anchor(left); top: anchor(bottom);
//            width: anchor-size(--tip width); }
//
// Only the parts the corpus exercises are here: the side keywords, a
// percentage along the axis, and `width`/`height` for the size form. Absent so
// far are `position-area`, `anchor-center`, and arithmetic around the function
// (`anchor(bottom) + 4px`), which needs the value to reach calc() rather than
// be resolved as a whole declaration.

namespace weva {

// Every `anchor-name` in the tree, resolved to the box that carries it.
//
// Built once per positioning pass rather than looked up per query: an anchor
// name can be declared anywhere in the document, including after the element
// that references it, so there is no walk from the referrer that finds it.
struct AnchorRegistry {
    struct Entry {
        std::string_view name;
        BoxId box = kNoBox;
    };
    std::vector<Entry> entries;

    // The LAST element with this name wins, which is what the spec's
    // "anchor element" resolution comes to for a flat document: later in tree
    // order shadows earlier.
    BoxId find(std::string_view name) const {
        BoxId found = kNoBox;
        for (const Entry& e : entries) {
            if (e.name == name) found = e.box;
        }
        return found;
    }
    bool empty() const { return entries.empty(); }
};

AnchorRegistry collect_anchors(const BoxTree& tree, BoxId root);

// Opens and closes an anchor pass. Between them, the resolvers below can
// answer; outside them they always decline.
//
// The registry is built LAZILY, on the first query that actually names an
// anchor, so a document that uses none never pays for the walk. It is built as
// a WHOLE when it is built, because an `anchor-name` can be declared on an
// element that comes after the one referring to it.
//
// This state, and the two resolvers, live in anchor.cpp rather than in
// positioning.cpp on purpose: every function in positioning.cpp runs per box
// per pass, and simply adding sixty lines to that translation unit moved the
// whole corpus 2 to 5 per cent with the hot paths untouched. The same code in
// its own translation unit costs nothing measurable.
void begin_anchor_pass(const BoxTree& tree, BoxId root);
void end_anchor_pass();

// Whether a declaration could possibly be an `anchor()` or `anchor-size()`
// function, cheaply enough to ask of every inset of every out-of-flow box.
//
// This is what keeps anchor positioning off the bill for documents that do
// not use it. Detecting anchors at BUILD time instead -- one presence check
// per element box -- cost 1 to 2 per cent across the corpus, because the
// benchmark times box building as well as layout and there are far more
// boxes than out-of-flow ones. Asking here costs a leading-character test on
// values that have already been fetched.
inline bool looks_like_anchor_function(std::string_view raw) {
    if (raw.size() < 8) return false;
    const char c = raw[0];
    return (c == 'a' || c == 'A') && (raw[1] == 'n' || raw[1] == 'N');
}

// The anchor `box` refers to through `position-anchor`, or an explicit name
// inside the function. kNoBox when there is none, which makes the function
// invalid and the property fall back to `auto` (CSS Anchor Positioning §3.3).
BoxId anchor_for(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                 std::string_view explicit_name);

// Resolves `raw` when it is an `anchor()` function, writing the pixel value
// for `property` (one of left/right/top/bottom) and returning true. Returns
// false — leaving `*out` untouched — when `raw` is not an anchor function or
// cannot be resolved, so the caller falls through to its ordinary path.
//
// The value is measured the way the spec defines it: from the containing
// block's edge that the PROPERTY starts from, to the named edge of the
// anchor's border box. `left: anchor(right)` is therefore the distance from
// the containing block's left edge to the anchor's right edge.
bool resolve_anchor_offset(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                           std::string_view property, std::string_view raw,
                           const ContainingBlock& cb, double* out);

// Writes every anchor-derived value onto `box`: its four insets, its width and
// its height, each only where the declaration is an anchor function that
// resolves. Returns true when the WIDTH was one of them, which the caller must
// follow with a relayout at that width.
//
// ONE entry point, called once per out-of-flow box, rather than a query per
// property. Six call sites inside `apply_absolute` cost 2 per cent on the
// samples with out-of-flow content even with every one of them disabled --
// their presence alone was enough to change how that function was compiled.
// Marked cold so it stays out of the caller's hot path (GCC and Clang; MSVC
// has no such attribute and warns about the unknown one, C5030).
#if defined(__GNUC__) || defined(__clang__)
#define WEVA_COLD [[gnu::cold]]
#else
#define WEVA_COLD
#endif
WEVA_COLD bool apply_anchor_overrides(BoxTree* tree, BoxId box, const ContainingBlock& cb,
                            bool* width_auto = nullptr);

// The same for `anchor-size(<name>? width|height)`, which resolves to that
// extent of the anchor's border box.
bool resolve_anchor_size(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                         std::string_view raw, double* out);

} // namespace weva
