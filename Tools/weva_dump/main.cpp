// weva_dump — the C++ side of the differential oracle (see docs/ORACLE.md).
//
// Mirrors Tools/BaselineGen/LayoutDump.cs so the two implementations can be
// diffed element-by-element. Usage matches BaselineGen's:
//
//     weva_dump <html> <width> <height> [out] [css]
//
// The walk mirrors LayoutDump.Walk exactly, and the ways it is allowed to
// differ are none: `html` and `body` are skipped as wrappers but recursed into
// without incrementing depth, anonymous and line boxes are skipped entirely,
// and only the FIRST box for an element is emitted, because a box that
// fragments produces several and the C# keys on the principal one.

#include "weva/image_store.h"
#include "weva/components.h"
#include "weva/block_layout.h"
#include "weva/box.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/container_query_state.h"
#include "weva/css_rule.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/intern.h"
#include "weva/positioning.h"
#include "weva/paint.h"
#include "weva/style_resolver.h"
#include "weva/user_agent_stylesheet.h"
#include "weva/layout_dump.h"

#include <map>
#include <memory>
#include <set>

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace {

using weva::ElementRect;

bool read_file(const std::string& path, std::string* out) {
    std::FILE* f = std::fopen(path.c_str(), "rb");
    if (!f) return false;
    std::fseek(f, 0, SEEK_END);
    long n = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    out->resize(static_cast<std::size_t>(n));
    std::size_t got = std::fread(out->data(), 1, static_cast<std::size_t>(n), f);
    out->resize(got);
    std::fclose(f);
    return true;
}

// The same cascade walk weva_c.cpp performs, kept here rather than shared
// because the ABI's copy is behind an opaque handle and this tool needs the box
// tree itself.

struct StyleMap : weva::StyleProvider {
    weva::CascadeEngine& engine;
    weva::NullStateProvider state;
    std::vector<std::unique_ptr<weva::ComputedStyle>> owned;
    std::map<const weva::Element*, weva::ComputedStyle*> by_element;
    // ::before / ::after, cascaded beside the element (index 0 / 1). Only
    // hosts some rule targets get an entry; the builder reads null as "no
    // pseudo box".
    std::map<std::pair<const weva::Element*, int>, weva::ComputedStyle*> pseudo_by_element;

    explicit StyleMap(weva::CascadeEngine& e) : engine(e) {}

    void walk(const weva::Element& e, const weva::ComputedStyle* parent) {
        auto cs = std::make_unique<weva::ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        weva::ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
        static constexpr std::string_view kPseudos[2] = {"before", "after"};
        for (int i = 0; i < 2; ++i) {
            auto ps = std::make_unique<weva::ComputedStyle>();
            if (!engine.compute_pseudo_element(e, kPseudos[i], state, *raw, ps.get())) continue;
            pseudo_by_element[{&e, i}] = ps.get();
            owned.push_back(std::move(ps));
        }
        for (const weva::Ref<weva::Node>& c : e.children()) {
            if (c->node_type() == weva::NodeType::Element) {
                walk(static_cast<const weva::Element&>(*c), raw);
            }
        }
    }
    const weva::ComputedStyle* style_of(const weva::Element& e) override {
        auto it = by_element.find(&e);
        return it == by_element.end() ? nullptr : it->second;
    }
    const weva::ComputedStyle* pseudo_style_of(const weva::Element& e,
                                               std::string_view name) override {
        const int i = name == "before" ? 0 : name == "after" ? 1 : -1;
        if (i < 0) return nullptr;
        auto it = pseudo_by_element.find({&e, i});
        return it == pseudo_by_element.end() ? nullptr : it->second;
    }
};

// LayoutDump.ResolveTransformTranslation: the dump reports the VISUAL
// position, so translate(), translateX() and translateY() move the box and
// everything under it. Percentages are of the box's own size; `px` and bare
// numbers are pixels; any other unit contributes nothing — the same reading
// as the reference, so a tooltip at `left: 50%; transform: translateX(-50%)`
// lands where it does there.

// getBoundingClientRect includes transforms and unions all inline fragments.
// The legacy reference walk intentionally does neither; keep it separately.
void browser_walk(const weva::BoxTree& tree, weva::BoxId id,
                  const weva::LayoutContext& ctx, const weva::Transform2D& parent,
                  int depth, std::vector<ElementRect>* out,
                  std::map<const weva::Element*, size_t>* seen) {
    const auto& b = tree[id];
    auto local = weva::Transform2D::identity();
    if (b.style && b.kind != weva::BoxKind::Inline && b.kind != weva::BoxKind::Text &&
        b.kind != weva::BoxKind::Line && b.kind != weva::BoxKind::AnonymousBlock &&
        b.kind != weva::BoxKind::AnonymousInline)
        weva::resolve_transform(b.style, ctx, b.font_size, b.width, b.height, &local);
    const auto xf = local.multiply(weva::Transform2D::translate(b.x, b.y)).multiply(parent);
    const bool wrapper = b.element && (b.element->tag_name() == "html" || b.element->tag_name() == "body");
    const bool principal = b.element && !wrapper && b.kind != weva::BoxKind::Line &&
        b.kind != weva::BoxKind::AnonymousBlock && b.kind != weva::BoxKind::AnonymousInline &&
        b.kind != weva::BoxKind::Text;
    if (principal) {
        double left = 1e30, top = 1e30, right = -1e30, bottom = -1e30;
        for (double x : {0.0, b.width}) for (double y : {0.0, b.height}) {
            double px, py; xf.apply(x, y, &px, &py);
            left = std::min(left, px); top = std::min(top, py);
            right = std::max(right, px); bottom = std::max(bottom, py);
        }
        const auto existing = seen->find(b.element);
        if (existing != seen->end()) {
            auto& r = (*out)[existing->second];
            if (b.width != 0 && b.height != 0) {
                if (r.w == 0 || r.h == 0) {
                    r.x = left; r.y = top; r.w = right - left; r.h = bottom - top;
                } else {
                    const double rr = std::max(r.x + r.w, right), bb = std::max(r.y + r.h, bottom);
                    r.x = std::min(r.x, left); r.y = std::min(r.y, top);
                    r.w = rr - r.x; r.h = bb - r.y;
                }
            }
        } else {
            ElementRect r;
            r.tag = b.element->tag_name(); r.id = b.element->id(); r.cls = b.element->class_name();
            const weva::Node* node = b.element;
            while (node && node->is_element()) {
                const auto* e = static_cast<const weva::Element*>(node);
                if (e->tag_name() == "html" || e->tag_name() == "body") break;
                int index = 0;
                if (node->parent()) for (const auto& sibling : node->parent()->children()) {
                    if (sibling->is_element()) ++index;
                    if (sibling.get() == node) break;
                }
                const std::string part = std::string(e->tag_name()) + ":" + std::to_string(index);
                r.path = part + (r.path.empty() ? "" : "/" + r.path);
                node = node->parent();
            }
            r.depth = depth; r.x = left; r.y = top; r.w = right - left; r.h = bottom - top;
            (*seen)[b.element] = out->size(); out->push_back(r);
        }
    }
    if (b.split_inline_owner && b.height != 0) {
        double cx, cy, cw, ch;
        weva::promoted_inline_rect(tree, id, &cx, &cy, &cw, &ch);
        double left = 1e30, top = 1e30, right = -1e30, bottom = -1e30;
        for (double x : {cx, cx + cw}) for (double y : {cy, cy + ch}) {
            double px, py; parent.apply(x, y, &px, &py);
            left = std::min(left, px); top = std::min(top, py);
            right = std::max(right, px); bottom = std::max(bottom, py);
        }
        const weva::Node* node = cw == 0 ? nullptr : (b.element ? b.element->parent() : b.pseudo_host);
        for (; node; node = node->parent()) {
            if (node->is_element()) {
                const auto* element = static_cast<const weva::Element*>(node);
                const auto found = seen->find(element);
                if (found != seen->end() && weva::is_promoted_inline_fragment(b, element)) {
                    auto& r = (*out)[found->second];
                    if (r.w == 0 || r.h == 0) {
                        r.x = left; r.y = top; r.w = right - left; r.h = bottom - top;
                    } else {
                        const double rr = std::max(r.x + r.w, right), bb = std::max(r.y + r.h, bottom);
                        r.x = std::min(r.x, left); r.y = std::min(r.y, top);
                        r.w = rr - r.x; r.h = bb - r.y;
                    }
                }
            }
            if (node == b.split_inline_owner) break;
        }
    }
    const auto child_xf = weva::Transform2D::translate(-b.scroll_x, -b.scroll_y).multiply(xf);
    for (auto child : tree.children(id))
        browser_walk(tree, child, ctx, child_xf, wrapper ? depth : depth + 1, out, seen);
}

} // namespace

int main(int argc, char** argv) {
    bool chrome_metrics = false;
    if (argc > 1 && std::string_view(argv[argc - 1]) == "--chrome-metrics") {
        chrome_metrics = true;
        --argc;
    }
    if (argc < 4) {
        std::fprintf(stderr,
            "Usage: weva_dump <htmlPath> <width> <height> [outPath] [cssPath]\n");
        return 1;
    }

    std::string html_path = argv[1];
    int width  = std::atoi(argv[2]);
    int height = std::atoi(argv[3]);
    std::string out_path = argc > 4 ? argv[4] : "dump.json";
    std::string css_path = argc > 5 ? argv[5] : "";

    if (width <= 0 || height <= 0) {
        std::fprintf(stderr, "Viewport must be positive integer width/height.\n");
        return 1;
    }

    // The document source buffer is owned here and outlives every parse slice
    // that will eventually point into it (docs/CONVENTIONS.md).
    std::string html, css;
    if (!read_file(html_path, &html)) {
        std::fprintf(stderr, "HTML not found: %s\n", html_path.c_str());
        return 1;
    }
    if (!css_path.empty() && !read_file(css_path, &css)) {
        std::fprintf(stderr, "CSS not found: %s\n", css_path.c_str());
        return 1;
    }

    weva::SymbolTable symbols;
    std::vector<ElementRect> boxes;

    weva::ParseOptions html_options;
    html_options.strict = false;
    weva::HtmlParseError html_error;
    weva::Ref<weva::Document> document =
        weva::parse_html(html, &symbols, html_options, &html_error);
    if (!document) {
        std::fprintf(stderr, "weva_dump: html did not parse\n");
        return 1;
    }

    // Components (`<template id="card">` + `<card>` + `<slot>`) expand BEFORE
    // the cascade, as UIDocumentBuilder does, so selectors match the expanded
    // subtree and not the un-rendered host.
    weva::expand_components(document.get());

    // The UA sheet first, then the author sheet, in the same order and with the
    // same origins the C# uses — origin ordering is half of the cascade, so a
    // difference here would show up as a divergence in every rule.
    weva::CascadeEngine cascade;
    weva::MediaContext media;
    media.viewport_width_px = width;
    media.viewport_height_px = height;
    cascade.set_media_context(media);
    weva::Stylesheet ua_sheet;
    weva::CssParseError css_error;
    if (weva::parse_stylesheet(weva::user_agent_stylesheet_source(), false, &ua_sheet,
                               &css_error)) {
        cascade.add_stylesheet(&ua_sheet, weva::DeclarationOrigin::UserAgent);
    }
    weva::Stylesheet author_sheet;
    if (!css.empty()) {
        if (!weva::parse_stylesheet(css, false, &author_sheet, &css_error)) {
            std::fprintf(stderr, "weva_dump: css did not parse\n");
            return 1;
        }
        cascade.add_stylesheet(&author_sheet, weva::DeclarationOrigin::Author);
    }

    StyleMap styles{cascade};
    weva::ContainerQueryState container_queries;
    container_queries.attach(&cascade);
    for (const weva::Ref<weva::Node>& child : document->children()) {
        if (child->node_type() == weva::NodeType::Element) {
            styles.walk(static_cast<const weva::Element&>(*child), nullptr);
        }
    }

    // ChromeSansSerif, matching BaselineGen. The C ABI default-constructs
    // MonoFontMetrics instead, which is a different face — the dump has to
    // match the oracle, not the ABI, or every text measurement diverges for a
    // reason that has nothing to do with the engine.
    struct BrowserMetrics : weva::MonoFontMetrics {
        bool round_extents;
        BrowserMetrics(double advance, bool round)
            : MonoFontMetrics(advance, 1.143, .85, .293), round_extents(round) {}
        double leading_above(double height, double fs) const override {
            const double half = MonoFontMetrics::leading_above(height, fs);
            return round_extents ? std::floor(half) : half;
        }
        double ascent(double fs) const override {
            return round_extents ? std::round(fs * .85) : MonoFontMetrics::ascent(fs);
        }
        double descent(double fs) const override {
            return round_extents ? std::round(fs * .293) : MonoFontMetrics::descent(fs);
        }
    };
    const BrowserMetrics metrics(.45, chrome_metrics);
    // BaselineGen also registers ChromeMonospace under `monospace`, so a
    // <code> run measures at 0.6em per glyph there; without the same
    // registration every code snippet on a page was 25% narrower here.
    const BrowserMetrics monospace(.6, chrome_metrics);
    // Images, resolved against the DOCUMENT's directory the way a browser
    // resolves them. Without this the dump cannot load one, an <img> has no
    // intrinsic size and lays out at zero -- and since the C# reference reads
    // the same corpus, both sides agree on the wrong answer and the oracle
    // reports a case that measures nothing.
    weva::ImageStore images;
    images.set_base_path_from_file(html_path);

    weva::LayoutContext ctx;
    ctx.images = &images;
    ctx.viewport_width_px = width;
    ctx.viewport_height_px = height;
    ctx.register_font("monospace", &monospace);

    weva::BoxTree tree;
    weva::BoxBuilder builder(&tree, &styles);
    weva::BoxId root = builder.build_document(*document);
    if (root == weva::kNoBox) {
        std::fprintf(stderr, "weva_dump: no box tree\n");
        return 1;
    }
    weva::BlockLayout block(&tree, ctx, &metrics);
    block.layout_root(root, ctx.viewport_width_px, ctx.viewport_height_px);
    weva::run_positioning(&tree, root, ctx, &block);
    size_t remaining=styles.by_element.size()+1;
    while (!cascade.container_queries().empty() &&
           container_queries.refresh(*document,tree,root,styles,ctx)) {
        if (!remaining--) {
            std::fprintf(stderr,"weva_dump: container size queries did not settle\n");
            return 1;
        }
        // The dump has no incremental state. Clear both normal and pseudo
        // styles so a conditional pseudo that disappeared cannot survive.
        tree.reset();
        styles.owned.clear(); styles.by_element.clear(); styles.pseudo_by_element.clear();
        for (const auto& child:document->children())
            if (child->is_element()) styles.walk(static_cast<const weva::Element&>(*child),nullptr);
        root=builder.build_document(*document);
        if (root==weva::kNoBox) return 1;
        weva::BlockLayout query_layout(&tree,ctx,&metrics);
        query_layout.layout_root(root,ctx.viewport_width_px,ctx.viewport_height_px);
        weva::run_positioning(&tree,root,ctx,&query_layout);
    }

    std::set<const weva::Element*> seen;
    if (chrome_metrics) {
        std::map<const weva::Element*, size_t> browser_seen;
        browser_walk(tree, root, ctx, weva::Transform2D::identity(), 0, &boxes, &browser_seen);
    } else weva::collect_layout_dump(tree, root, 0, 0, 0, &boxes, &seen);

    // The basename, not the path: BaselineGen writes Path.GetFileName, and a
    // whole-file diff of the two dumps has to compare equal.
    const std::size_t slash = html_path.find_last_of("/\\");
    const std::string source_name =
        slash == std::string::npos ? html_path : html_path.substr(slash + 1);
    const std::string json = weva::layout_dump_json(source_name, width, height, boxes);
    std::FILE* f = std::fopen(out_path.c_str(), "wb");
    if (!f) {
        std::fprintf(stderr, "weva_dump: cannot write %s\n", out_path.c_str());
        return 2;
    }
    std::fwrite(json.data(), 1, json.size(), f);
    std::fclose(f);
    std::printf("Wrote %zu elements -> %s\n", boxes.size(), out_path.c_str());
    return 0;
}
