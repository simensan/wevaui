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
#ifdef _WIN32
#include <windows.h>
#include <dbghelp.h>
#else
#include <execinfo.h>
#include <sys/time.h>
#endif
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <memory>
#include <new>
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
// Frames kept per sample. One aggregates by SELF time, which says which
// function is hot; two says which caller chose it. Neither is right for every
// question, so --sample-depth picks.
int g_sample_depth = 2;

#ifndef _WIN32
void record_sample();
void on_sigprof(int) {
    if (!g_sampling) return;
    ++g_samples;
    record_sample();
}
#endif

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__)
__attribute__((noinline))
#endif
void record_site(size_t size) {
    void* frames[kFrames + 2];
#ifdef _WIN32
    const int n = CaptureStackBackTrace(0, kFrames + 2, frames, nullptr);
#else
    const int n = backtrace(frames, kFrames + 2);
#endif
    // This recorder and operator new occupy the first two frames. Keep the
    // recorder out of line so release and symbol builds skip the same frames.
    const int start = n > 2 ? 2 : 0;
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

#ifndef _WIN32
void record_sample() {
    void* frames[kFrames + 3];
    const int n = backtrace(frames, kFrames + 3);
    // Frames 0 and 1 are the handler and the signal trampoline.
    const int start = n > 2 ? 2 : 0;
    // Two frames, not six: a full stack splits one hot function across every
    // path that reaches it, and the first run of this buried the answer under
    // 648 sites whose largest was 0.4%. Two is enough to tell a libc leaf from
    // the caller that chose it.
    const int want = g_sample_depth;
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
#endif

void report_sites(const char* what, size_t total, int top, bool allocations = false) {
    std::printf("\n  %s (top %d of %zu sites), innermost frame first:\n", what, top, g_site_count);
#ifdef _WIN32
    const HANDLE process = GetCurrentProcess();
    SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
    // Local symbols only. RelWithDebInfo provides names for core frames;
    // Release without PDBs still reports the captured instruction addresses.
    const bool symbols = SymInitialize(process, ".", TRUE) != FALSE;
#endif
    std::vector<size_t> order(g_site_count);
    for (size_t i = 0; i < g_site_count; ++i) order[i] = i;
    std::sort(order.begin(), order.end(),
              [](size_t a, size_t b) { return g_sites[a].count > g_sites[b].count; });
    for (size_t rank = 0; rank < order.size() && rank < static_cast<size_t>(top); ++rank) {
        const Site& site = g_sites[order[rank]];
        std::printf("  %6zu (%4.1f%%)\n", site.count, total ? 100.0 * site.count / total : 0.0);
        if (allocations) std::printf("        %zu allocated bytes\n", site.bytes);
#ifdef _WIN32
        for (int f = 0; f < site.depth && f < 4; ++f) {
            alignas(SYMBOL_INFO) unsigned char storage[sizeof(SYMBOL_INFO) + MAX_SYM_NAME]{};
            auto* info = reinterpret_cast<SYMBOL_INFO*>(storage);
            info->SizeOfStruct = sizeof(SYMBOL_INFO);
            info->MaxNameLen = MAX_SYM_NAME;
            DWORD64 offset = 0;
            if (symbols && SymFromAddr(process, reinterpret_cast<DWORD64>(site.frames[f]), &offset, info))
                std::printf("        %s+0x%llx\n", info->Name, static_cast<unsigned long long>(offset));
            else std::printf("        %p\n", site.frames[f]);
        }
#else
        char** names = backtrace_symbols(site.frames, site.depth);
        for (int f = 0; f < site.depth && f < 4; ++f) {
            std::printf("        %s\n", names ? names[f] : "?");
        }
        std::free(names);
#endif
    }
#ifdef _WIN32
    if (symbols) SymCleanup(process);
#endif
}

}   // namespace

void* operator new(size_t size, const std::nothrow_t&) noexcept {
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
    return std::malloc(size ? size : 1);
}
void* operator new(size_t size) {
    // The build disables exceptions, so an allocation failure aborts rather
    // than throwing. A benchmark that cannot allocate has nothing to report.
    if (void* p = ::operator new(size, std::nothrow)) return p;
    std::abort();
}
// libstdc++ stable_sort uses nothrow new for its temporary buffer. It must
// share our allocator and counter, including in an AddressSanitizer build.
void* operator new[](size_t size) { return ::operator new(size); }
void* operator new[](size_t size, const std::nothrow_t&) noexcept { return ::operator new(size, std::nothrow); }
// GCC 13 warns that free() does not match `new`: it cannot see that this
// program replaces operator new with malloc above. The pairing is right.
#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic ignored "-Wmismatched-new-delete"
#endif
void operator delete(void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }
void operator delete(void* p, const std::nothrow_t&) noexcept { std::free(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { std::free(p); }

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
    if (!f) {
        std::fprintf(stderr, "weva_bench: cannot read %s\n", path);
        std::exit(2);
    }
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
    const std::string css = argc > 2 && argv[2][0] ? read_file(argv[2]) : std::string();
    int passes = argc > 3 ? std::atoi(argv[3]) : 200;
    for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "--profile") g_profile = true;
    }
    // Attribution needs only one measured pass, and the capture makes the
    // timings meaningless anyway.
    if (g_profile && passes > 20) passes = 20;
    if (passes <= 0) {
        std::fprintf(stderr, "weva_bench: no passes (argument order?)\n");
        return 2;
    }

    bool full = false;
    bool sample = false;
    bool cold = false;
    bool reopen = false;
    for (int i = 1; i < argc; ++i) {
        if (std::string(argv[i]) == "--full") full = true;
        if (std::string(argv[i]) == "--cold") cold = true;
        if (std::string(argv[i]) == "--reopen") cold = reopen = true;
        if (std::string(argv[i]) == "--sample") sample = true;
    }
    // Rasterized textures outlive their document (weva_set_raster_cache_limit),
    // so every cold pass after the first would be a reopen. --cold measures the
    // first opening; --reopen measures a screen created again.
    if (cold && !reopen) weva_set_raster_cache_limit(0);
    if (sample && g_profile) {
        std::fprintf(stderr, "weva_bench: --sample and --profile must run separately\n");
        return 2;
    }
#ifdef _WIN32
    if (sample) {
        std::fprintf(stderr, "weva_bench: --sample requires POSIX SIGPROF; use --profile for Windows allocation stacks\n");
        return 2;
    }
#endif

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
    // Seconds handed to each timed update. Nonzero is what an ANIMATING
    // document costs: transitions and @keyframes advance, and a page that
    // animates layout relays out every frame.
    double frame_dt = 0;
    std::string target_selector = "*";
    std::string focus_selector;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a.rfind("--target=", 0) == 0) target_selector = a.substr(9);
        if (a.rfind("--focus=", 0) == 0) focus_selector = a.substr(8);
        if (a.rfind("--sample-depth=", 0) == 0) g_sample_depth = std::atoi(a.c_str() + 15);
        if (a.rfind("--dt=", 0) == 0) frame_dt = std::atof(a.c_str() + 5);
    }
    if (g_sample_depth < 1 || g_sample_depth > kFrames) {
        std::fprintf(stderr, "weva_bench: --sample-depth must be between 1 and %d\n", kFrames);
        return 2;
    }
    if (g_profile) std::fprintf(stderr, "weva_bench: allocation profiling enabled; timings include stack capture\n");

    // Arms the profiling timer around the timed region.
    const auto start_sampling = [&] {
#ifndef _WIN32
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
#endif
    };
    const auto stop_sampling = [&] {
#ifndef _WIN32
        if (!sample) return;
        g_sampling = false;
        itimerval off{};
        setitimer(ITIMER_PROF, &off, nullptr);
#endif
    };

    // `--full` times what a HOST actually pays when something changes: the
    // whole of weva_document_update, cascade and paint included. The default
    // mode times box building and layout alone, which is the narrower question
    // of whether a layout data structure got faster.
    //
    // A game redraws a static document for free -- the host only updates when
    // it marks the document dirty -- so this number is the cost of a change,
    // and it is the one that decides whether a health bar can move every frame.
    // `--cold` is the number a user actually looks at: opening a screen.
    //
    // --full measures a change to a document that is already up, which is the
    // steady state and, after the caching work, mostly free. It says nothing
    // about the FIRST frame -- the glyph atlas is empty, every gradient has to
    // be rasterized, every shadow blurred -- and that is the one a game pays
    // when it opens a menu, and the one the gallery's `build` line shows.
    //
    // Everything is rebuilt per pass, parsing included, because a host that
    // swaps a screen re-parses it too.
    if (cold) {
        double best = 1e300, total = 0;
        start_sampling();
        for (int i = 0; i < passes; ++i) {
            if (i == passes - 1) {
                g_allocations = g_bytes = 0;
                g_counting = true;
            }
            if (sample) g_sampling = true;
            const auto t0 = std::chrono::steady_clock::now();
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
            weva_document_update(d, 0);
            const double ms =
                std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0)
                    .count();
            g_counting = false;
            total += ms;
            if (ms < best) best = ms;
            // Match the sampled region to the timer: destruction happens after
            // the cold build has been measured and must not dominate its profile.
            if (sample) g_sampling = false;
            weva_document_destroy(d);
        }
        stop_sampling();
        std::printf("%-20s %-7s %-10s best %8.3f ms  mean %8.3f ms"
                    "  cold allocations %zu (%zu bytes)\n", argv[1], reopen ? "reopen" : "cold", "-", best,
                    total / passes, g_allocations, g_bytes);
        if (sample) report_sites("time samples", g_samples, 16);
        if (g_profile) report_sites("allocation sites", g_allocations, 12, true);
        return 0;
    }

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
        // `--target=last` is the realistic case, and `*` is the worst one.
        //
        // `*` resolves to the first element in the document, which is <html> or
        // <body>: a change there dirties everything, so the cascade restyles the
        // page and no amount of scoping can help. That is worth measuring, but
        // it is not what a running UI does. A health bar moving is a LEAF, and
        // the last element in document order is a leaf on every page in the
        // corpus without having to know its markup.
        if (mutate == "last") mutate = "layout";
        if (target_selector == "last") {
            std::vector<weva_element_t> all(16384);
            const size_t n = weva_document_query_all(d, "*", all.data(), all.size());
            if (n == 0) {
                std::fprintf(stderr, "weva_bench: --target=last found no element\n");
                return 1;
            }
            target = all[std::min(n, all.size()) - 1];
        } else if (mutate != "none" && mutate != "hover") {
            target = weva_document_query(d, target_selector.c_str());
            if (target == WEVA_ELEMENT_NONE) {
                std::fprintf(stderr, "weva_bench: --mutate found no element\n");
                return 1;
            }
        }
        const char* const paint_values[2] = {"background-color:#123456",
                                             "background-color:#123457"};
        const char* const layout_values[2] = {"padding-left:11px", "padding-left:12px"};

        if (!focus_selector.empty() || mutate == "caret") {
            const auto focus = focus_selector.empty() ? target : weva_document_query(d, focus_selector.c_str());
            weva_document_set_focus(d, focus);
            if (weva_document_text_input_target(d) == WEVA_ELEMENT_NONE) {
                std::fprintf(stderr, "weva_bench: text editing needs a focused editable field\n");
                weva_document_destroy(d);
                return 1;
            }
            weva_document_update(d, 0);
        }

        double best = 1e300, total = 0;
        size_t steady_allocations = 0, steady_bytes = 0;
        start_sampling();
        for (int i = 0; i < passes; ++i) {
            // `hover` moves the pointer instead of editing the document: two
            // points far enough apart to land on different elements, so every
            // pass crosses a real boundary and the hover chain actually flips.
            //
            // It is the mouse-move frame, and on a page whose sheet never says
            // `:hover` the honest answer is that it costs nothing. It did not:
            // marking the flipped elements made the cascade re-walk their
            // subtrees to find that no rule matched differently.
            if (mutate == "caret") {
                const int key = (i & 1) ? WEVA_KEY_HOME : WEVA_KEY_END;
                weva_document_key(d, key, 0, 1);
                weva_document_key(d, key, 0, 0);
            } else if (mutate == "hover") {
                const int x = (i & 1) ? 320 : 960;
                const int y = (i & 1) ? 180 : 540;
                weva_document_set_pointer(d, x, y, 0);
            } else if (target != WEVA_ELEMENT_NONE) {
                const char* const* values = mutate == "paint" ? paint_values : layout_values;
                weva_element_set_attribute(d, target, "style", values[i & 1]);
            }
            const bool measure_allocations = i == passes - 1;
            if (measure_allocations) {
                g_allocations = g_bytes = 0;
                g_counting = true;
            }
            const auto t0 = std::chrono::steady_clock::now();
            weva_document_update(d, frame_dt);
            const auto t1 = std::chrono::steady_clock::now();
            if (measure_allocations) {
                g_counting = false;
                steady_allocations = g_allocations;
                steady_bytes = g_bytes;
            }
            const double ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
            total += ms;
            if (ms < best) best = ms;
        }
        stop_sampling();
        size_t draws = 0, textures = 0;
        weva_document_draws(d, &draws);
        weva_document_textures(d, &textures);
        std::printf("%-20s %-7s %-10s best %8.3f ms  mean %8.3f ms  %zu draws  %zu textures"
                    "  steady-state allocations %zu (%zu bytes)\n",
                    argv[1], mutate.c_str(), target_selector.c_str(), best, total / passes, draws, textures,
                    steady_allocations, steady_bytes);
        if (sample) report_sites("time samples", g_samples, 16);
        if (g_profile) report_sites("allocation sites", g_allocations, 12, true);
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
    if (g_profile) report_sites("allocation sites", steady_allocations, 12, true);
    return 0;
}
