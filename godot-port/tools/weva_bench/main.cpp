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

#include <algorithm>
#include <chrono>
#include <execinfo.h>
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

// Optional per-call-site attribution.
//
// The previous attempt at this used hand-placed counters and drew the wrong
// conclusion from them: parse_css_value is recursive, so a depth flag charges
// nested parses to their top-level call and the per-site counts do not add up
// to the total. A backtrace cannot be fooled that way — it records where the
// allocation actually came from, and the totals reconcile by construction.
//
// Off by default: capturing six frames per allocation dominates the timing, so
// the timed numbers and the attribution are never taken from the same run.
bool g_profile = false;
constexpr int kFrames = 6;

struct Site {
    void* frames[kFrames];
    int depth;
    size_t count;
    size_t bytes;
};
Site g_sites[4096];
size_t g_site_count = 0;

void record_site(size_t size) {
    void* frames[kFrames + 2];
    const int n = backtrace(frames, kFrames + 2);
    // Frame 0 is operator new itself; skip it so sites group by their caller.
    const int start = n > 1 ? 1 : 0;
    const int depth = n - start < kFrames ? n - start : kFrames;
    for (size_t i = 0; i < g_site_count; ++i) {
        if (g_sites[i].depth != depth) continue;
        bool same = true;
        for (int f = 0; f < depth; ++f) {
            if (g_sites[i].frames[f] != frames[start + f]) { same = false; break; }
        }
        if (same) {
            ++g_sites[i].count;
            g_sites[i].bytes += size;
            return;
        }
    }
    if (g_site_count >= 4096) return;
    Site& site = g_sites[g_site_count++];
    site.depth = depth;
    for (int f = 0; f < depth; ++f) site.frames[f] = frames[start + f];
    site.count = 1;
    site.bytes = size;
}

}   // namespace

void* operator new(size_t size) {
    if (g_counting) {
        ++g_allocations;
        g_bytes += size;
        if (g_profile) {
            // Re-entrancy guard: backtrace_symbols and the recorder must not
            // count their own allocations.
            g_counting = false;
            record_site(size);
            g_counting = true;
        }
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
    int passes = argc > 3 ? std::atoi(argv[3]) : 200;
    for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "--profile") g_profile = true;
    }
    // Attribution needs only one measured pass, and the capture makes the
    // timings meaningless anyway.
    if (g_profile && passes > 20) passes = 20;

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
    if (g_profile) {
        std::printf("\n  allocation sites (top 12 of %zu), innermost frame first:\n",
                    g_site_count);
        std::vector<size_t> order(g_site_count);
        for (size_t i = 0; i < g_site_count; ++i) order[i] = i;
        std::sort(order.begin(), order.end(), [](size_t a, size_t b) {
            return g_sites[a].count > g_sites[b].count;
        });
        for (size_t rank = 0; rank < order.size() && rank < 12; ++rank) {
            const Site& site = g_sites[order[rank]];
            std::printf("  %6zu allocs %9zu bytes (%.0f%%)\n", site.count, site.bytes,
                        steady_allocations ? 100.0 * site.count / steady_allocations : 0.0);
            char** names = backtrace_symbols(site.frames, site.depth);
            for (int f = 0; f < site.depth && f < 4; ++f) {
                std::printf("        %s\n", names ? names[f] : "?");
            }
            std::free(names);
        }
    }
    return 0;
}
