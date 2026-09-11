// Keep the allocation counter outside the general test runner. These budgets
// cover actual mesh building, including a positive control inside libweva.
#include "weva/tessellate.h"
#include "weva/paint.h"
#include "weva/glyph_atlas.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <new>
#include <string>

namespace {
bool counting = false;
size_t allocations = 0;
uint64_t geometry_hash = 1469598103934665603ULL;
void hash_bytes(const void* data, size_t size) {
    const auto* bytes = static_cast<const unsigned char*>(data);
    for (size_t i = 0; i < size; ++i) { geometry_hash ^= bytes[i]; geometry_hash *= 1099511628211ULL; }
}
bool validate(const weva::Mesh& mesh) {
    for (const auto& v : mesh.vertices) {
        const float fields[] = {v.position.x, v.position.y, v.tex_coord.x, v.tex_coord.y,
                                v.color.r, v.color.g, v.color.b, v.color.a};
        hash_bytes(fields, sizeof(fields));
    }
    for (const auto index : mesh.indices) {
        if (index >= mesh.vertices.size()) return false;
        hash_bytes(&index, sizeof(index));
    }
    return mesh.indices.size() % 3 == 0;
}
template<class F> size_t measure(F call) {
    allocations = 0;
    counting = true;
    call();
    counting = false;
    return allocations;
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
    int checks = 0, failures = 0;
    const auto check = [&](bool pass, const char* label, size_t count, size_t limit) {
        ++checks;
        if (!pass) { ++failures; std::printf("FAIL %s: %zu allocations, limit %zu\n", label, count, limit); }
    };
    const Rect rect(2.125, -3.75, 103.5, 41.25);
    const LinearColor colors[] = {{1,.5f,0,1}, {0,1,.2f,.8f}, {0,0,1,.4f}, {1,0,1,0}};
    for (int segments : {1, 4, 8, 31}) {
        for (bool aa : {false, true}) {
            for (double radius : {0., .25, 8., 100.}) {
                Mesh fill;
                const size_t n = measure([&] { tessellate_rounded_rect(rect, BorderRadii::uniform(radius), colors[0], &fill, segments, aa); });
                check(validate(fill) && n > 0 && n <= 5 && !fill.empty(), "rounded fill", n, 5);
                for (double width : {.5, 2., 16.}) {
                    Mesh border;
                    const size_t b = measure([&] { tessellate_border(rect, BorderRadii::uniform(radius), width, width, width, width, colors, &border, segments, aa); });
                    if (b>10) std::printf("  border segments=%d aa=%d radius=%g width=%g\n",segments,aa,radius,width);
                    check(validate(border) && b <= 10 && !border.empty(), "border", b, 10);
                }
            }
        }
    }
    Mesh quad, joined;
    tessellate_rect(rect, colors[0], &quad);
    const size_t append = measure([&] { for (int i = 0; i < 500; ++i) joined.append(quad); });
    check(validate(joined) && append <= 50 && joined.vertices.size() == 2000 && joined.indices.size() == 3000,
          "500 appended quads grow geometrically", append, 50);

    StubFont font;
    const auto face = font.load_face({}, 0);
    GlyphAtlas atlas;
    PaintContext paint;
    paint.font = &font; paint.face = face; paint.atlas = &atlas;
    struct TextCase { std::string text; size_t geometry_allocations; };
    const TextCase text_cases[] = {
        {std::string(100, 'A'), 4}, {std::string(4096, 'A'), 30},
        {std::string(4096, ' '), 0}, {std::string("A") + std::string(4096, ' '), 4},
        {"Size check with spaces", 4},
    };
    for (const auto& sample : text_cases) {
        const auto& text = sample.text;
        Mesh warm;
        build_text_geometry(text, 1.5, 23.25, 16, colors[0], paint, &warm, .125);
        std::vector<ShapedGlyph> shaped;
        const size_t shape_count = measure([&] { font.shape(face, text, 16, &shaped); });
        Mesh text_mesh;
        const size_t n = measure([&] { build_text_geometry(text, 1.5, 23.25, 16, colors[0], paint, &text_mesh, .125); });
        const size_t budget = shape_count + sample.geometry_allocations;
        check(validate(text_mesh) && n <= budget && text_mesh.indices == warm.indices,
              "text mesh allocation budget", n, budget);
        check(text_mesh.vertices.size() == warm.vertices.size(), "text vertex count", n, budget);
        for (size_t i = 0; i < text_mesh.vertices.size() && i < warm.vertices.size(); ++i) {
            const auto& a = text_mesh.vertices[i]; const auto& b = warm.vertices[i];
            if (a.position.x != b.position.x || a.position.y != b.position.y || a.tex_coord.x != b.tex_coord.x ||
                a.tex_coord.y != b.tex_coord.y || a.color != b.color) { ++failures; break; }
        }
    }
    std::printf("geometry allocations: %d checks, %d failures; geometry hash %016llx\n", checks, failures,
                static_cast<unsigned long long>(geometry_hash));
    return failures ? 1 : 0;
}
