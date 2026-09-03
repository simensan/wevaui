// What a FRAME costs on a real page, which is the number a game feels.
//
// Three cases, because they are three different amounts of work:
//   idle       nothing moved; the document should discover that and stop
//   hover      the pointer moved to a new element: a restyle and a repaint
//   animate    the clock advanced on a page with transitions running
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
        std::fprintf(stderr, "usage: framebench <html> <css> [frames]\n");
        return 2;
    }
    const std::string html = read_file(argv[1]);
    const std::string css = read_file(argv[2]);
    const int frames = argc > 3 ? std::atoi(argv[3]) : 120;

    weva_config c{};
    c.viewport_width = 1280;
    c.viewport_height = 720;
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

    t0 = now();
    for (int i = 0; i < frames; ++i) weva_document_update(d, 0.016);
    const double idle = ms(t0, now()) / frames;

    // The pointer walks the page, so each frame hovers something new.
    t0 = now();
    for (int i = 0; i < frames; ++i) {
        const double x = 40 + (i * 37) % 1200;
        const double y = 40 + (i * 53) % 640;
        weva_document_set_pointer(d, x, y, 0);
        weva_document_update(d, 0.016);
        size_t n = 0;
        weva_document_draws(d, &n);
    }
    const double hover = ms(t0, now()) / frames;

    size_t draws = 0;
    weva_document_draws(d, &draws);
    std::printf("%-22s first %7.2f   idle %6.3f   hover %6.3f   draws %zu\n", argv[1], first, idle,
                hover, draws);
    weva_document_destroy(d);
    return 0;
}
