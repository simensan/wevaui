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

#include "weva/components.h"
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
#include "weva_c.h"

#include <algorithm>
#include <chrono>
#include <execinfo.h>
#include <sys/time.h>
#include <csignal>
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

// ---- sampling profiler ---------------------------------------------------
//
// Where a pass spends its TIME, which the allocation counter cannot answer and
// which no profiler on this machine can either: neither perf, gdb nor valgrind
// is installed, and the allocation work showed why the question matters --
// cutting 83% of a layout pass's allocations moved its wall clock by 5%.
//
// A profiling timer signals at 1 kHz of CPU time and the handler records where
// it landed, into the same site table the allocation counter uses. backtrace()
// is called once before the timer starts so its lazy initialisation does not
// happen inside the handler.
bool g_sampling = false;
size_t g_samples = 0;

void record_sample();

void on_sigprof(int) {
    if (!g_sampling) return;
    ++g_samples;
    record_sample();
}

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

void record_sample() {
    void* frames[kFrames + 3];
    const int n = backtrace(frames, kFrames + 3);
    // Frames 0 and 1 are the handler and the signal trampoline.
    const int start = n > 2 ? 2 : 0;
    // Two frames, not six: a full stack splits one hot function across every
    // path that reaches it, and the first run of this buried the answer under
    // 648 sites whose largest was 0.4%. Two is enough to tell a libc leaf from
    // the caller that chose it.
    const int want = 2;
    const int depth = n - start < want ? n - start : want;
    for (size_t i = 0; i < g_site_count; ++i) {
        if (g_sites[i].depth != depth) continue;
        bool same = true;
        for (int f = 0; f < depth; ++f) {
            if (g_sites[i].frames[f] != frames[start + f]) { same = false; break; }
        }
        if (same) {
            ++g_sites[i].count;
            return;
        }
    }
    if (g_site_count >= 4096) return;
    Site& site = g_sites[g_site_count++];
    site.depth = depth;
    for (int f = 0; f < depth; ++f) site.frames[f] = frames[start + f];
    site.count = 1;
    site.bytes = 0;
}

void report_sites(const char* what, size_t total, int top) {
    std::printf("\n  %s (top %d of %zu sites), innermost frame first:\n", what, top, g_site_count);
    std::vector<size_t> order(g_site_count);
    for (size_t i = 0; i < g_site_count; ++i) order[i] = i;
    std::sort(order.begin(), order.end(),
              [](size_t a, size_t b) { return g_sites[a].count > g_sites[b].count; });
    for (size_t rank = 0; rank < order.size() && rank < static_cast<size_t>(top); ++rank) {
        const Site& site = g_sites[order[rank]];
        std::printf("  %6zu (%4.1f%%)\n", site.count, total ? 100.0 * site.count / total : 0.0);
        char** names = backtrace_symbols(site.frames, site.depth);
        for (int f = 0; f < site.depth && f < 4; ++f) {
            std::printf("        %s\n", names ? names[f] : "?");
        }
        std::free(names);
    }
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
    std::map<std::pair<const Element*, int>, ComputedStyle*> pseudo_by_element;

    explicit Styles(CascadeEngine& e) : engine(e) {}
    void walk(const Element& e, const ComputedStyle* parent) {
        auto cs = std::make_unique<ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
        static constexpr std::string_view kPseudos[2] = {"before", "after"};
        for (int i = 0; i < 2; ++i) {
            auto ps = std::make_unique<ComputedStyle>();
            if (!engine.compute_pseudo_element(e, kPseudos[i], state, *raw, ps.get())) continue;
            pseudo_by_element[{&e, i}] = ps.get();
            owned.push_back(std::move(ps));
        }
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
    const ComputedStyle* pseudo_style_of(const Element& e, std::string_view name) override {
        const int i = name == "before" ? 0 : name == "after" ? 1 : -1;
        if (i < 0) return nullptr;
        auto it = pseudo_by_element.find({&e, i});
        return it == pseudo_by_element.end() ? nullptr : it->second;
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

    bool full = false;
    bool sample = false;
    for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "--full") full = true;
        if (std::string(argv[i]) == "--sample") sample = true;
    }

    // What each timed pass changes about the document.
    //
    // Without this, --full now measures nothing: an update that finds no change
    // publishes the frame it already had, so a loop over an untouched document
    // times the cascade and an early return. That is the honest number for a
    // static screen and useless for anything else, so the cases are named.
    std::string mutate = "none";
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a.rfind("--mutate=", 0) == 0) mutate = a.substr(9);
    }
    // Which element the mutation lands on. It matters more than it looks: a
    // restyle is confined to what the change can reach, so touching the
    // outermost element restyles the document and touching a leaf restyles a
    // leaf. `*` is the honest worst case; a real host moves a health bar.
    std::string target_selector = "*";
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a.rfind("--target=", 0) == 0) target_selector = a.substr(9);
    }

    // Arms the profiling timer around the timed region.
    const auto start_sampling = [&] {
        if (!sample) return;
        void* warm[4];
        backtrace(warm, 4);   // force the lazy init out of the handler
        struct sigaction sa{};
        sa.sa_handler = on_sigprof;
        sigemptyset(&sa.sa_mask);
        sa.sa_flags = SA_RESTART;
        sigaction(SIGPROF, &sa, nullptr);
        itimerval t{};
        t.it_interval.tv_usec = 1000;
        t.it_value.tv_usec = 1000;
        setitimer(ITIMER_PROF, &t, nullptr);
        g_site_count = 0;
        g_samples = 0;
        g_sampling = true;
    };
    const auto stop_sampling = [&] {
        if (!sample) return;
        g_sampling = false;
        itimerval off{};
        setitimer(ITIMER_PROF, &off, nullptr);
    };

    // `--full` times what a HOST actually pays when something changes: the
    // whole of weva_document_update, cascade and paint included. The default
    // mode times box building and layout alone, which is the narrower question
    // of whether a layout data structure got faster.
    //
    // A game redraws a static document for free -- the host only updates when
    // it marks the document dirty -- so this number is the cost of a change,
    // and it is the one that decides whether a health bar can move every frame.
    if (full) {
        weva_config cfg{};
        cfg.viewport_width = 1280;
        cfg.viewport_height = 720;
        cfg.use_user_agent_stylesheet = 1;
        weva_document_t d = weva_document_create(&cfg);
        if (!css.empty() && weva_document_add_css(d, css.data(), css.size()) != WEVA_OK) {
            std::fprintf(stderr, "weva_bench: css rejected\n");
            return 1;
        }
        if (weva_document_load_html(d, html.data(), html.size()) != WEVA_OK) {
            std::fprintf(stderr, "weva_bench: html rejected\n");
            return 1;
        }
        weva_document_update(d, 0);   // warm the atlas and the arenas

        // The element a mutation lands on: the first the document has, so any
        // sample works without knowing its markup.
        weva_element_t target = WEVA_ELEMENT_NONE;
        if (mutate != "none") {
            target = weva_document_query(d, target_selector.c_str());
            if (target == WEVA_ELEMENT_NONE) {
                std::fprintf(stderr, "weva_bench: --mutate found no element\n");
                return 1;
            }
        }
        const char* const paint_values[2] = {"background-color:#123456",
                                             "background-color:#123457"};
        const char* const layout_values[2] = {"padding-left:11px", "padding-left:12px"};

        double best = 1e300, total = 0;
        start_sampling();
        for (int i = 0; i < passes; ++i) {
            if (target != WEVA_ELEMENT_NONE) {
                const char* const* values = mutate == "paint" ? paint_values : layout_values;
                weva_element_set_attribute(d, target, "style", values[i & 1]);
            }
            const auto t0 = std::chrono::steady_clock::now();
            weva_document_update(d, 0);
            const auto t1 = std::chrono::steady_clock::now();
            const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
            total += ms;
            if (ms < best) best = ms;
        }
        stop_sampling();
        size_t draws = 0, textures = 0;
        weva_document_draws(d, &draws);
        weva_document_textures(d, &textures);
        std::printf("%-20s %-7s %-10s best %8.3f ms  mean %8.3f ms  %zu draws  %zu textures\n",
                    argv[1], mutate.c_str(), target_selector.c_str(), best, total / passes, draws, textures);
        if (sample) report_sites("time samples", g_samples, 16);
        weva_document_destroy(d);
        return 0;
    }

    SymbolTable symbols;
    ParseOptions opts;
    opts.strict = false;
    HtmlParseError herr;
    Ref<Document> doc = parse_html(html, &symbols, opts, &herr);
    if (!doc) {
        std::fprintf(stderr, "weva_bench: html did not parse\n");
        return 1;
    }

    // Components (`<template id="card">` + `<card>` + `<slot>`) expand BEFORE
    // the cascade, as UIDocumentBuilder does, so selectors match the expanded
    // subtree and not the un-rendered host.
    expand_components(doc.get());

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

    start_sampling();
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

    stop_sampling();
    std::printf("%-28s %6d boxes  best %7.3f ms  mean %7.3f ms  "
                "steady-state allocations %zu (%zu bytes)\n",
                argv[1], boxes, best_ms, total_ms / passes, steady_allocations, steady_bytes);
    if (sample) report_sites("time samples", g_samples, 16);
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
