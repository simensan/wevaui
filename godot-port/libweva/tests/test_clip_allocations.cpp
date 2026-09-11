// Budget clip preparation separately from the general runner. The geometry
// digest also permits an exact comparison with a frozen pre-change library.
#include "weva/tessellate.h"
#include <algorithm>
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
struct Hash {
    uint64_t value = 1469598103934665603ULL;
    template<class T> void add(const T& v) {
        const auto* bytes = reinterpret_cast<const unsigned char*>(&v);
        for (size_t i = 0; i < sizeof(v); ++i) { value ^= bytes[i]; value *= 1099511628211ULL; }
    }
    void points(const std::vector<weva::ClipPoint>& points) {
        add(static_cast<uint64_t>(points.size()));
        for (const auto& p : points) { add(p.x); add(p.y); }
    }
};
uint64_t clip_hash(const weva::PreparedClip& clip) {
    Hash h;
    h.points(clip.polygon);
    h.add(static_cast<uint64_t>(clip.pieces.size()));
    for (const auto& piece : clip.pieces)
        for (const auto& p : piece) { h.add(p.x); h.add(p.y); }
    for (const auto& bounds : clip.piece_bounds)
        for (double v : bounds) h.add(v);
    for (double v : {clip.x0, clip.y0, clip.x1, clip.y1, clip.ix0, clip.iy0, clip.ix1, clip.iy1}) h.add(v);
    h.add(static_cast<uint8_t>(clip.convex));
    return h.value;
}
void hash_mesh(const weva::Mesh& mesh, Hash* h) {
    h->add(static_cast<uint64_t>(mesh.vertices.size()));
    for (const auto& v : mesh.vertices) {
        for (float f : {v.position.x, v.position.y, v.tex_coord.x, v.tex_coord.y,
                        v.color.r, v.color.g, v.color.b, v.color.a}) h->add(f);
    }
    h->add(static_cast<uint64_t>(mesh.indices.size()));
    for (uint32_t index : mesh.indices) {
        check(index < mesh.vertices.size(), "valid clipped mesh index");
        h->add(index);
    }
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
    Hash digest;
    Mesh input;
    check(measure([&] { tessellate_rect(Rect(-10, -10, 130, 90), LinearColor::white(), &input); }) > 0,
          "allocation counter positive control in libweva");
    for (size_t i = 0; i < input.vertices.size(); ++i) {
        auto& v = input.vertices[i];
        v.tex_coord = {static_cast<float>(i)*.13f, static_cast<float>(i)*.27f};
        v.color = LinearColor(.1f + static_cast<float>(i)*.2f, .7f, .4f, .2f + static_cast<float>(i)*.17f);
    }
    const auto exercise = [&](const std::vector<ClipPoint>& polygon) {
        PreparedClip clip;
        clip.polygon = polygon;
        Hash original;
        original.points(polygon);
        const size_t cold = measure([&] { clip.prepare(); });
        check(cold <= 4, "at most four clip preparation allocations");
        Hash after;
        after.points(clip.polygon);
        check(original.value == after.value, "preparation preserves caller polygon");
        check(clip.pieces.size() == clip.piece_bounds.size(), "one bound per piece");
        const uint64_t prepared = clip_hash(clip);
        const size_t warm = measure([&] { clip.prepare(); });
        check(warm <= 2, "repeated preparation reuses piece capacity");
        check(clip_hash(clip) == prepared, "repeated preparation is exact");
        // Hash output even when an allocation assertion fails: the baseline
        // must not skip geometry merely because its allocation budget is high.
        digest.add(prepared);
        Mesh out;
        tessellate_rect(Rect(-50, -50, 2, 2), LinearColor::white(), &out);
        clip_triangles_polygon(input.vertices, input.indices, clip, &out);
        hash_mesh(out, &digest);
        // A second clip exercises changed vertex counts, attributes and winding.
        Mesh twice;
        clip_triangles_polygon(out.vertices, out.indices, clip, &twice);
        hash_mesh(twice, &digest);
        clip.polygon.clear();
        clip.prepare();
        check(clip.pieces.empty() && clip.piece_bounds.empty() && !clip.convex,
              "empty reprepare discards previous geometry");
    };
    for (const auto& polygon : std::vector<std::vector<ClipPoint>>{
            {}, {{0,0}}, {{0,0},{1,1}}, {{0,0},{1,1},{2,2},{0,0}},
            {{0,0},{100,0},{100,60},{0,60},{0,0},{0,0}},
            {{0,0},{80,0},{80,20},{30,20},{30,70},{0,70}},
            {{0,0},{80,70},{0,70},{80,0}},
            {{0,0},{0,0},{1e-9,0},{2e-9,0},{100,0},{100,60},{0,60},{0,0}}}) {
        exercise(polygon);
        auto reversed = polygon;
        std::reverse(reversed.begin(), reversed.end());
        exercise(reversed);
    }
    for (int segments : {-1,1,4,8,31,128}) {
        for (const auto& radii : {BorderRadii::zero(), BorderRadii::uniform(.25),
                BorderRadii::uniform(6), BorderRadii::uniform(100),
                BorderRadii(CornerRadius(0,4), CornerRadius(8,2), CornerRadius(3,9), CornerRadius(0,0))}) {
            std::vector<ClipPoint> outline;
            check(measure([&] { outline = rounded_rect_outline(Rect(2.125,-3.75,103.5,41.25), radii, segments); }) <= 1,
                  "rounded clip outline allocates once");
            digest.points(outline);
            exercise(outline);
            std::reverse(outline.begin(), outline.end());
            exercise(outline);
        }
    }
    uint32_t random = 0x9b1735u;
    for (int trial = 0; trial < 1000; ++trial) {
        std::vector<ClipPoint> polygon;
        const int count = 3 + trial%31;
        for (int i = 0; i < count; ++i) {
            random = random*1664525u + 1013904223u;
            const double radius = 1 + (random%2000)/40.0;
            const double angle = 6.28318530717958647692*i/count;
            const ClipPoint point{50 + radius*std::cos(angle), 30 + radius*std::sin(angle)};
            polygon.push_back(point);
            if (i%7 == 0) polygon.push_back(point);
            if (i%11 == 0) polygon.push_back({point.x + 5e-10, point.y - 5e-10});
        }
        polygon.push_back(polygon.front());
        if (trial%2) std::reverse(polygon.begin(), polygon.end());
        exercise(polygon);
    }
    std::printf("clip allocations: %d checks, %d failures; geometry hash %016llx\n", checks, failures,
                static_cast<unsigned long long>(digest.value));
    return failures ? 1 : 0;
}
