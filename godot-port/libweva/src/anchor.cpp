#include "weva/anchor.h"

#include "weva/css_properties.h"

#include <algorithm>
#include <cstdlib>
#include <string>

namespace weva {

namespace {

const int kId_anchor_name = CssPropertyRegistry::instance().id_of("anchor-name");
const int kId_position_anchor = CssPropertyRegistry::instance().id_of("position-anchor");
const int kId_width = CssPropertyRegistry::instance().id_of("width");
const int kId_height = CssPropertyRegistry::instance().id_of("height");

std::string_view get(const ComputedStyle* s, int id) {
    return s ? s->get(id) : std::string_view();
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (x != b[i]) return false;
    }
    return true;
}

std::string_view trim(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\n' || s[b] == '\r')) ++b;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\n' || s[e - 1] == '\r')) --e;
    return s.substr(b, e - b);
}

// The inside of `name(...)`, or empty when `raw` is not exactly that call.
//
// Exactly: a trailing `+ 4px` makes this fail rather than silently dropping the
// arithmetic, because a tooltip 4px off its anchor is a bug you have to look
// closely to see, and no answer is better than a quietly wrong one.
std::string_view function_argument(std::string_view raw, std::string_view name) {
    const std::string_view s = trim(raw);
    if (s.size() < name.size() + 2) return {};
    if (s.back() != ')') return {};
    for (size_t i = 0; i < name.size(); ++i) {
        char c = s[i];
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        if (c != name[i]) return {};
    }
    if (s[name.size()] != '(') return {};
    const std::string_view inner = s.substr(name.size() + 1, s.size() - name.size() - 2);
    // One call and nothing else: a nested paren means something this does not
    // understand.
    if (inner.find('(') != std::string_view::npos) return {};
    return trim(inner);
}

// Splits the argument into an optional `--name` and the keyword after it.
void split_argument(std::string_view arg, std::string_view* name, std::string_view* keyword) {
    *name = {};
    *keyword = arg;
    if (arg.size() > 2 && arg[0] == '-' && arg[1] == '-') {
        const size_t sp = arg.find(' ');
        if (sp == std::string_view::npos) {
            // `anchor(--tip)` names an anchor and no side, which is not a
            // valid function.
            *name = arg;
            *keyword = {};
            return;
        }
        *name = trim(arg.substr(0, sp));
        *keyword = trim(arg.substr(sp + 1));
    }
}

// The border-box rectangle of `box` in the same root-relative space the
// containing block is expressed in.
void border_box_of(const BoxTree& tree, BoxId box, double* x, double* y, double* w, double* h) {
    absolute_position(tree, box, x, y);
    *w = tree[box].width;
    *h = tree[box].height;
}

// The open pass, if any.
struct PassAnchors {
    const BoxTree* tree = nullptr;
    BoxId root = kNoBox;
    AnchorRegistry registry;
    bool built = false;

    const AnchorRegistry& get() {
        if (!built) {
            registry = collect_anchors(*tree, root);
            built = true;
        }
        return registry;
    }
};
thread_local PassAnchors g_pass;
thread_local bool g_open = false;

} // namespace

void begin_anchor_pass(const BoxTree& tree, BoxId root) {
    g_pass = PassAnchors{};
    g_pass.tree = &tree;
    g_pass.root = root;
    g_open = true;
}

void end_anchor_pass() {
    g_open = false;
    g_pass = PassAnchors{};
}

bool apply_anchor_overrides(BoxTree* tree, BoxId box, const ContainingBlock& cb) {
    if (!g_open) return false;
    const ComputedStyle* style = (*tree)[box].style;
    if (!style) return false;

    // Every one of these is a value the caller has already resolved the
    // ordinary way; an anchor function replaces it, anything else leaves it.
    struct Inset { const char* name; std::optional<double> Box::*slot; };
    static const Inset kInsets[] = {
        {"left", &Box::offset_left},
        {"right", &Box::offset_right},
        {"top", &Box::offset_top},
        {"bottom", &Box::offset_bottom},
    };
    for (const Inset& in : kInsets) {
        const std::string_view raw = style->get(in.name);
        if (!looks_like_anchor_function(raw)) continue;
        double px = 0;
        if (resolve_anchor_offset(*tree, g_pass.get(), box, in.name, raw, cb, &px)) {
            (*tree)[box].*in.slot = px;
        }
    }

    bool width_set = false;
    double px = 0;
    const std::string_view w_raw = style->get(kId_width);
    if (looks_like_anchor_function(w_raw) &&
        resolve_anchor_size(*tree, g_pass.get(), box, w_raw, &px)) {
        (*tree)[box].width = px;
        width_set = true;
    }
    const std::string_view h_raw = style->get(kId_height);
    if (looks_like_anchor_function(h_raw) &&
        resolve_anchor_size(*tree, g_pass.get(), box, h_raw, &px)) {
        (*tree)[box].height = px;
        // The auto-height rule would otherwise collapse it back to the
        // content, which for an empty tooltip is zero.
        (*tree)[box].cross_size_imposed = true;
    }
    return width_set;
}

AnchorRegistry collect_anchors(const BoxTree& tree, BoxId root) {
    AnchorRegistry reg;
    if (root == kNoBox) return reg;
    // An explicit stack rather than recursion: the walk is over every box in
    // the document and runs once per positioning pass.
    std::vector<BoxId> stack{root};
    while (!stack.empty()) {
        const BoxId id = stack.back();
        stack.pop_back();
        const Box& b = tree[id];
        const std::string_view name = trim(get(b.style, kId_anchor_name));
        // Only an element's own box: an anonymous box shares its style with
        // the element that made it, and would register the same name twice.
        if (!name.empty() && !iequals(name, "none") && b.element) {
            reg.entries.push_back({name, id});
        }
        for (BoxId c : tree.children(id)) stack.push_back(c);
    }
    // The walk above pushes children in order and pops from the back, so
    // entries come out reversed. find() takes the last match as the winner,
    // which has to mean last in TREE order.
    std::reverse(reg.entries.begin(), reg.entries.end());
    return reg;
}

BoxId anchor_for(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                 std::string_view explicit_name) {
    if (anchors.empty() || box == kNoBox) return kNoBox;
    if (!explicit_name.empty()) return anchors.find(explicit_name);
    const std::string_view named = trim(get(tree[box].style, kId_position_anchor));
    if (named.empty() || iequals(named, "auto")) return kNoBox;
    return anchors.find(named);
}

bool resolve_anchor_offset(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                           std::string_view property, std::string_view raw,
                           const ContainingBlock& cb, double* out) {
    const std::string_view arg = function_argument(raw, "anchor");
    if (arg.empty()) return false;
    std::string_view name, keyword;
    split_argument(arg, &name, &keyword);
    if (keyword.empty()) return false;

    const BoxId anchor = anchor_for(tree, anchors, box, name);
    if (anchor == kNoBox) return false;
    double ax = 0, ay = 0, aw = 0, ah = 0;
    border_box_of(tree, anchor, &ax, &ay, &aw, &ah);

    const bool horizontal = iequals(property, "left") || iequals(property, "right");
    const double near_edge = horizontal ? ax : ay;
    const double extent = horizontal ? aw : ah;

    // Where on the anchor the value points, in root-relative coordinates.
    double at = 0;
    if (iequals(keyword, "center")) {
        at = near_edge + extent * 0.5;
    } else if (!keyword.empty() && keyword.back() == '%') {
        // A percentage runs from the axis's start edge to its end edge.
        const std::string text(keyword.substr(0, keyword.size() - 1));
        char* end = nullptr;
        const double pct = std::strtod(text.c_str(), &end);
        if (end == text.c_str()) return false;
        at = near_edge + extent * (pct / 100.0);
    } else {
        // `start`/`end` are the writing mode's, and this engine lays out
        // horizontal-tb LTR, so they are the physical near and far edges.
        // `self-start`/`self-end` are the positioned element's own writing
        // mode, which is the same one.
        const bool far = iequals(keyword, "right") || iequals(keyword, "bottom") ||
                         iequals(keyword, "end") || iequals(keyword, "self-end");
        const bool near = iequals(keyword, "left") || iequals(keyword, "top") ||
                          iequals(keyword, "start") || iequals(keyword, "self-start");
        if (!far && !near) return false;
        // A side belonging to the other axis is invalid for this property --
        // `left: anchor(top)` names no position at all.
        if (horizontal && (iequals(keyword, "top") || iequals(keyword, "bottom"))) return false;
        if (!horizontal && (iequals(keyword, "left") || iequals(keyword, "right"))) return false;
        at = far ? near_edge + extent : near_edge;
    }

    // Measured from the containing block edge the property itself starts from,
    // so `right: anchor(left)` counts leftwards from the right edge.
    const bool from_far_edge = iequals(property, "right") || iequals(property, "bottom");
    if (from_far_edge) {
        const double cb_far = horizontal ? cb.x + cb.width : cb.y + cb.height;
        *out = cb_far - at;
    } else {
        *out = at - (horizontal ? cb.x : cb.y);
    }
    return true;
}

bool resolve_anchor_size(const BoxTree& tree, const AnchorRegistry& anchors, BoxId box,
                         std::string_view raw, double* out) {
    const std::string_view arg = function_argument(raw, "anchor-size");
    if (arg.empty()) return false;
    std::string_view name, keyword;
    split_argument(arg, &name, &keyword);
    // `anchor-size(--tip)` with no extent means the extent of the property's
    // own axis, which the caller knows and this does not; the corpus always
    // spells it out, so an unspelled one is left unresolved rather than
    // guessed.
    if (keyword.empty()) return false;

    const BoxId anchor = anchor_for(tree, anchors, box, name);
    if (anchor == kNoBox) return false;
    double ax = 0, ay = 0, aw = 0, ah = 0;
    border_box_of(tree, anchor, &ax, &ay, &aw, &ah);

    // `inline`/`block` are the writing mode's; horizontal-tb makes them width
    // and height.
    if (iequals(keyword, "width") || iequals(keyword, "inline") ||
        iequals(keyword, "self-inline")) {
        *out = aw;
        return true;
    }
    if (iequals(keyword, "height") || iequals(keyword, "block") ||
        iequals(keyword, "self-block")) {
        *out = ah;
        return true;
    }
    return false;
}

} // namespace weva
