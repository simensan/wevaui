#include "weva/paint.h"
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <new>

namespace {
bool counting = false;
size_t allocations = 0;
int checks = 0, failures = 0;
void check(bool pass, const char* label) {
    ++checks;
    if (!pass && failures++ < 16) std::printf("FAIL %s\n", label);
}
template<class F> size_t measure(F call) {
    allocations = 0;
    counting = true;
    call();
    counting = false;
    return allocations;
}
bool near(double a, double b) { return std::fabs(a - b) < 1e-9; }
void check_corner(weva::CornerRadius r, double x, double y, const char* label) {
    check(near(r.x_radius, x) && near(r.y_radius, y), label);
}
}
void* operator new(size_t size) {
    if (counting) ++allocations;
    if (void* p = std::malloc(size ? size : 1)) return p;
    std::abort();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

int main() {
    using namespace weva;
    LayoutContext ctx;
    ctx.root_font_size_px = 20;
    ctx.root_line_height_px = 30;
    ctx.viewport_width_px = 1000;
    ctx.viewport_height_px = 800;
    const char* properties[] = {"border-top-left-radius", "border-top-right-radius",
        "border-bottom-right-radius", "border-bottom-left-radius"};
    struct Sample { const char* raw; double x, y; };
    const Sample samples[] = {
        {"0",0,0}, {"0px",0,0}, {"12px",12,12}, {"50%",100,50},
        {"2em 1rem",32,20}, {"1vw 2vh",10,16}, {"1in 12pt",96,16},
        {"1rlh 1lh",30,19.2}, {"calc(10% + 2px) calc(25% + 1em)",22,41},
        {"calc(5px - 10px)",0,0}, {" 12px \t 8px ",12,8},
        {"12px\n8px",12,8}, {"12px/* axes */8px",12,8},
        {"not-a-radius",0,0}, {"calc(",0,0}, {"10px,20px",0,0},
    };
    ComputedStyle style;
    for (const auto& sample : samples) {
        for (const char* property : properties) style.set(property, sample.raw);
        resolve_border_radii(&style, 200, 100, ctx, 16);
        const size_t count = measure([&] {
            for (int i = 0; i < 100; ++i) {
                const auto r = resolve_border_radii(&style, 200, 100, ctx, 16);
                check_corner(r.top_left, sample.x, sample.y, sample.raw);
                check_corner(r.top_right, sample.x, sample.y, sample.raw);
                check_corner(r.bottom_right, sample.x, sample.y, sample.raw);
                check_corner(r.bottom_left, sample.x, sample.y, sample.raw);
            }
        });
        check(count == 0, "warm radius syntax needs no allocations");
    }
    // A fresh nonzero style must actually parse through libweva, proving that
    // the counter sees library allocations rather than only this executable.
    ComputedStyle fresh;
    fresh.set(properties[0], "17px 23px");
    check(measure([&] { resolve_border_radii(&fresh, 200, 100, ctx, 16); }) > 0,
          "cold parse allocation positive control");

    style.set(properties[0], "calc(10% + 1em) calc(25% + 1rem)");
    style.set(properties[1], "1vw 2vh");
    style.set(properties[2], "1in 12pt");
    style.set(properties[3], "1rlh 1lh");
    resolve_border_radii(&style, 200, 100, ctx, 16);
    // Vary all used-value inputs without touching the retained style. Caching
    // resolved radii instead of syntax would leave some of these stale.
    check(measure([&] {
        for (int i = 0; i < 100; ++i) {
            const double width = 120 + i, height = 60 + 2*i, font = 12 + i%7;
            ctx.root_font_size_px = 20 + i%5;
            ctx.root_line_height_px = 30 + i%3;
            ctx.viewport_width_px = 800 + i;
            ctx.viewport_height_px = 600 + i;
            ctx.dpi_pixels_per_inch = i%2 ? 144 : 96;
            const auto r = resolve_border_radii(&style, width, height, ctx, font);
            check_corner(r.top_left, .1*width + font, .25*height + ctx.root_font_size_px, "live size and font inputs");
            check_corner(r.top_right, .01*ctx.viewport_width_px, .02*ctx.viewport_height_px, "live viewport inputs");
            check_corner(r.bottom_right, ctx.dpi_pixels_per_inch, ctx.dpi_pixels_per_inch/6, "live DPI input");
            check_corner(r.bottom_left, ctx.root_line_height_px, font*1.2, "live line-height inputs");
        }
    }) == 0, "changed context does not reparse syntax");
    style.set(properties[0], "4px 9px");
    check_corner(resolve_border_radii(&style, 200, 100, ctx, 16).top_left, 4, 9, "set invalidates cached syntax");
    style.unset(CssPropertyRegistry::instance().id_of(properties[0]));
    check_corner(resolve_border_radii(&style, 200, 100, ctx, 16).top_left, 0, 0, "unset restores initial radius");
    ComputedStyle moved = std::move(style);
    style.clear();
    check_corner(resolve_border_radii(&moved, 200, 100, ctx, 16).top_right,
                 .01*ctx.viewport_width_px, .02*ctx.viewport_height_px, "move retains owned parse");
    moved.clear();
    const auto empty = resolve_border_radii(&moved, 200, 100, ctx, 16);
    check(empty == BorderRadii::zero(), "clear discards every cached radius");
    check(resolve_border_radii(nullptr, 200, 100, ctx, 16) == BorderRadii::zero(), "null style");
    std::printf("radius parsing: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
