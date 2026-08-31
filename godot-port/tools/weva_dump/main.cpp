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

#include "weva/block_layout.h"
#include "weva/box.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/css_rule.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/intern.h"
#include "weva/positioning.h"
#include "weva/style_resolver.h"
#include "weva/user_agent_stylesheet.h"

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

struct ElementRect {
    int depth = 0;
    std::string tag, id, cls;
    double x = 0, y = 0, w = 0, h = 0;
};

std::string json_escape(std::string_view v) {
    std::string out;
    out.reserve(v.size());
    for (char c : v) {
        switch (c) {
            case '\\': out += "\\\\"; break;
            case '"':  out += "\\\""; break;
            case '\r': out += "\\r";  break;
            case '\n': out += "\\n";  break;
            default:   out += c;      break;
        }
    }
    return out;
}

// Matches C#'s Math.Round(v, 2, MidpointRounding.AwayFromZero) followed by
// value.ToString("0.##", InvariantCulture).
//
// The explicit round matters. printf("%.2f") rounds half-to-even in glibc,
// while C# Round2 rounds half away from zero, so 0.125 formats as "0.12" here
// and "0.13" there. Every such midpoint would surface as a phantom one-cent
// layout difference in the oracle, and chasing those instead of real bugs is
// exactly how a differential harness loses its credibility. Round explicitly,
// then format a value that is already exact at 2dp.
std::string format_num(double v) {
    double scaled = v * 100.0;
    double rounded = (scaled < 0.0 ? -std::floor(-scaled + 0.5) : std::floor(scaled + 0.5)) / 100.0;
    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.2f", rounded);
    std::string s(buf);
    if (s.find('.') != std::string::npos) {
        while (!s.empty() && s.back() == '0') s.pop_back();
        if (!s.empty() && s.back() == '.') s.pop_back();
    }
    if (s == "-0") s = "0";
    return s;
}

void write_json(const std::string& out_path, const std::string& source,
                int width, int height, const std::vector<ElementRect>& boxes) {
    std::string sb;
    sb += "{\n";
    sb += "  \"source\": \"" + json_escape(source) + "\",\n";
    sb += "  \"width\": " + std::to_string(width) + ",\n";
    sb += "  \"height\": " + std::to_string(height) + ",\n";
    sb += "  \"count\": " + std::to_string(boxes.size()) + ",\n";
    sb += "  \"elements\": [";
    for (std::size_t i = 0; i < boxes.size(); ++i) {
        const ElementRect& b = boxes[i];
        if (i > 0) sb += ",";
        sb += "\n    {";
        sb += "\"i\":" + std::to_string(i) + ",";
        sb += "\"depth\":" + std::to_string(b.depth) + ",";
        sb += "\"tag\":\"" + json_escape(b.tag) + "\",";
        sb += "\"id\":\"" + json_escape(b.id) + "\",";
        sb += "\"cls\":\"" + json_escape(b.cls) + "\",";
        sb += "\"x\":" + format_num(b.x) + ",";
        sb += "\"y\":" + format_num(b.y) + ",";
        sb += "\"w\":" + format_num(b.w) + ",";
        sb += "\"h\":" + format_num(b.h);
        sb += "}";
    }
    sb += "\n  ]\n}\n";

    std::FILE* f = std::fopen(out_path.c_str(), "wb");
    if (!f) {
        std::fprintf(stderr, "weva_dump: cannot write %s\n", out_path.c_str());
        std::exit(2);
    }
    std::fwrite(sb.data(), 1, sb.size(), f);
    std::fclose(f);
}

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

    explicit StyleMap(weva::CascadeEngine& e) : engine(e) {}

    void walk(const weva::Element& e, const weva::ComputedStyle* parent) {
        auto cs = std::make_unique<weva::ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        weva::ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
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
};

void walk(const weva::BoxTree& tree, weva::BoxId id, double parent_x, double parent_y,
          int depth, std::vector<ElementRect>* out, std::set<const weva::Element*>* seen) {
    if (id == weva::kNoBox) return;
    const weva::Box& b = tree[id];
    const double x = parent_x + b.x;
    const double y = parent_y + b.y;

    // `html` and `body` do not appear in the dump but do not consume a level
    // either, so a top-level div is depth 1 on both sides.
    const bool is_wrapper =
        b.element && (b.element->tag_name() == "html" || b.element->tag_name() == "body");
    const bool is_principal = b.element && !is_wrapper && b.kind != weva::BoxKind::Line &&
                              b.kind != weva::BoxKind::AnonymousBlock &&
                              b.kind != weva::BoxKind::AnonymousInline &&
                              b.kind != weva::BoxKind::Text && seen->insert(b.element).second;
    if (is_principal) {
        ElementRect r;
        r.depth = depth;
        r.tag = b.element->tag_name();
        r.id = std::string(b.element->id());
        r.cls = std::string(b.element->class_name());
        r.x = x;
        r.y = y;
        r.w = b.width;
        r.h = b.height;
        out->push_back(r);
    }

    for (weva::BoxId c : tree.children(id)) {
        walk(tree, c, x, y, is_wrapper ? depth : depth + 1, out, seen);
    }
}

} // namespace

int main(int argc, char** argv) {
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

    // The UA sheet first, then the author sheet, in the same order and with the
    // same origins the C# uses — origin ordering is half of the cascade, so a
    // difference here would show up as a divergence in every rule.
    weva::CascadeEngine cascade;
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
    for (const weva::Ref<weva::Node>& child : document->children()) {
        if (child->node_type() == weva::NodeType::Element) {
            styles.walk(static_cast<const weva::Element&>(*child), nullptr);
        }
    }

    // ChromeSansSerif, matching BaselineGen. The C ABI default-constructs
    // MonoFontMetrics instead, which is a different face — the dump has to
    // match the oracle, not the ABI, or every text measurement diverges for a
    // reason that has nothing to do with the engine.
    const weva::MonoFontMetrics metrics = weva::MonoFontMetrics::chrome_sans_serif();
    weva::LayoutContext ctx;
    ctx.viewport_width_px = width;
    ctx.viewport_height_px = height;

    weva::BoxTree tree;
    weva::BoxBuilder builder(&tree, &styles);
    const weva::BoxId root = builder.build_document(*document);
    if (root == weva::kNoBox) {
        std::fprintf(stderr, "weva_dump: no box tree\n");
        return 1;
    }
    weva::BlockLayout block(&tree, ctx, &metrics);
    block.layout_root(root, ctx.viewport_width_px, ctx.viewport_height_px);
    weva::run_positioning(&tree, root, ctx, &block);

    std::set<const weva::Element*> seen;
    walk(tree, root, 0, 0, 0, &boxes, &seen);

    // The basename, not the path: BaselineGen writes Path.GetFileName, and a
    // whole-file diff of the two dumps has to compare equal.
    const std::size_t slash = html_path.find_last_of("/\\");
    const std::string source_name =
        slash == std::string::npos ? html_path : html_path.substr(slash + 1);
    write_json(out_path, source_name, width, height, boxes);
    std::printf("Wrote %zu elements -> %s\n", boxes.size(), out_path.c_str());
    return 0;
}
