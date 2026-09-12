// What a long list costs PER FRAME, which is the number that decides whether
// the port needs a virtual list. Building 10k rows once is a load-time cost;
// scrolling them is paid every frame a finger is down.
#include "weva_c.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

static std::string read_file(const char* path) {
    std::string out;
    FILE* f = std::fopen(path, "rb");
    if (!f) return out;
    char buf[65536];
    size_t n = 0;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) out.append(buf, n);
    std::fclose(f);
    return out;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: scrollbench <html> <css> [frames]\n");
        return 2;
    }
    const std::string html = read_file(argv[1]);
    const std::string css = read_file(argv[2]);
    const int frames = argc > 3 ? std::atoi(argv[3]) : 120;

    weva_config c{};
    c.viewport_width = 800;
    c.viewport_height = 600;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    weva_document_add_css(d, css.data(), css.size());
    weva_document_load_html(d, html.data(), html.size());

    const auto now = [] { return std::chrono::steady_clock::now(); };
    const auto ms = [](auto a, auto b) {
        return std::chrono::duration<double, std::milli>(b - a).count();
    };

    auto t0 = now();
    weva_document_update(d, 0);
    const double first = ms(t0, now());

    size_t draw_count = 0;
    weva_document_draws(d, &draw_count);

    // A settled document: nothing moved, so nothing should be recomputed.
    t0 = now();
    for (int i = 0; i < frames; ++i) weva_document_update(d, 0.016);
    const double idle = ms(t0, now()) / frames;

    // Scrolling: the offset moves every frame, as it does under a finger.
    std::vector<double> per_frame;
    per_frame.reserve(frames);
    for (int i = 0; i < frames; ++i) {
        const auto s = now();
        if (getenv("NO_HIT")) {
            weva_element_set_scroll(d, weva_document_query(d, ".list"), 0, i * 20.0);
        } else {
            weva_document_scroll(d, 400, 300, 0, 20);
        }
        weva_document_update(d, 0.016);
        size_t n = 0;
        weva_document_draws(d, &n);
        per_frame.push_back(ms(s, now()));
    }
    double total = 0, worst = 0;
    for (double v : per_frame) {
        total += v;
        if (v > worst) worst = v;
    }
    std::printf("%-22s first %8.2f ms   idle %6.3f ms   scroll mean %6.3f ms  worst %6.3f ms"
                "   draws %zu\n",
                argv[1], first, idle, total / per_frame.size(), worst, draw_count);
    weva_document_destroy(d);
    return 0;
}
