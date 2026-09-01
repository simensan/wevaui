// weva_bench — times a full layout pass and counts the heap allocations it makes.
//
// Two numbers, both of which PORT_PLAN.md makes claims about and neither of
// which had ever been measured:
//
//   * milliseconds per pass, so a change to a hot data structure can be judged
//     rather than argued about;
//   * heap allocations per pass in the steady state, which the plan sets as a
//     hard target ("zero heap allocations per frame") and which nothing was
//     checking.
//
//     weva_bench <html> [css] [passes]
//
// The allocation counter replaces global operator new/delete, so it sees
// everything the layout does, including anything the standard library does on
// its behalf. It counts only what happens INSIDE a timed pass — setup and
// parsing are excluded, since those are per-document rather than per-frame.

#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/css_rule.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/positioning.h"
#include "weva/style_resolver.h"
#include "weva/user_agent_stylesheet.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <memory>
#include <sstream>
#include <string>
#include <vector>

namespace {
bool g_counting = false;
size_t g_allocations = 0;
size_t g_bytes = 0;
}   // namespace

void* operator new(size_t size) {
    if (g_counting) {
        ++g_allocations;
        g_bytes += size;
    }
    void* p = std::malloc(size ? size : 1);
    // The build disables exceptions, so an allocation failure aborts rather
    // than throwing. A benchmark that cannot allocate has nothing to report.
    if (!p) std::abort();
    return p;
}
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }

using namespace weva;

namespace {

struct Styles : StyleProvider {
    CascadeEngine& engine;
    NullStateProvider state;
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    std::map<const Element*, ComputedStyle*> by_element;

    explicit Styles(CascadeEngine& e) : engine(e) {}
    void walk(const Element& e, const ComputedStyle* parent) {
        auto cs = std::make_unique<ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element) {
                walk(static_cast<const Element&>(*c), raw);
            }
        }
    }
    const ComputedStyle* style_of(const Element& e) override {
        auto it = by_element.find(&e);
        return it == by_element.end() ? nullptr : it->second;
    }
};

std::string read_file(const char* path) {
    std::ifstream f(path);
    std::ostringstream s;
    s << f.rdbuf();
    return s.str();
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: weva_bench <html> [css] [passes]\n");
        return 2;
    }
    const std::string html = read_file(argv[1]);
    const std::string css = argc > 2 ? read_file(argv[2]) : std::string();
    const int passes = argc > 3 ? std::atoi(argv[3]) : 200;

    SymbolTable symbols;
    ParseOptions opts;
    opts.strict = false;
    HtmlParseError herr;
    Ref<Document> doc = parse_html(html, &symbols, opts, &herr);
    if (!doc) {
        std::fprintf(stderr, "weva_bench: html did not parse\n");
        return 1;
    }

    CascadeEngine cascade;
    Stylesheet ua, author;
    CssParseError cerr;
    if (parse_stylesheet(user_agent_stylesheet_source(), false, &ua, &cerr)) {
        cascade.add_stylesheet(&ua, DeclarationOrigin::UserAgent);
    }
    if (!css.empty() && parse_stylesheet(css, false, &author, &cerr)) {
        cascade.add_stylesheet(&author, DeclarationOrigin::Author);
    }

    const MonoFontMetrics metrics = MonoFontMetrics::chrome_sans_serif();
    LayoutContext ctx;
    ctx.viewport_width_px = 1280;
    ctx.viewport_height_px = 720;

    // The style map is rebuilt per pass in a real frame only when styles are
    // dirty, so it is built once here and the timed region is layout alone.
    Styles styles(cascade);
    for (const Ref<Node>& c : doc->children()) {
        if (c->node_type() == NodeType::Element) {
            styles.walk(static_cast<const Element&>(*c), nullptr);
        }
    }

    BoxTree tree;
    double best_ms = 1e300;
    double total_ms = 0;
    int boxes = 0;
    size_t steady_allocations = 0;
    size_t steady_bytes = 0;

    for (int i = 0; i < passes; ++i) {
        // The first passes grow the arena; the steady-state numbers are what
        // the target is about, so counting starts once it has settled.
        const bool measure_allocations = i == passes - 1;
        tree.reset();
        if (measure_allocations) {
            g_allocations = 0;
            g_bytes = 0;
            g_counting = true;
        }
        const auto t0 = std::chrono::steady_clock::now();

        BoxBuilder builder(&tree, &styles);
        const BoxId root = builder.build_document(*doc);
        BlockLayout block(&tree, ctx, &metrics);
        block.layout_root(root, ctx.viewport_width_px, ctx.viewport_height_px);
        run_positioning(&tree, root, ctx, &block);

        const auto t1 = std::chrono::steady_clock::now();
        if (measure_allocations) {
            g_counting = false;
            steady_allocations = g_allocations;
            steady_bytes = g_bytes;
        }
        const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
        total_ms += ms;
        if (ms < best_ms) best_ms = ms;
        boxes = tree.size();
    }

    std::printf("%-28s %6d boxes  best %7.3f ms  mean %7.3f ms  "
                "steady-state allocations %zu (%zu bytes)\n",
                argv[1], boxes, best_ms, total_ms / passes, steady_allocations, steady_bytes);
    return 0;
}
