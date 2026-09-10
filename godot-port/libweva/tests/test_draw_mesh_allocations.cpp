// Exercise ownership through paint_tree, including a backend that keeps the
// submitted buffers after paint returns. The output digest can also be compared
// with a frozen library; it excludes addresses, padding and texture handles.
#include "weva/paint.h"
#include "weva/dom.h"
#include "weva/glyph_atlas.h"
#include "weva/tessellate.h"
#include <cstdio>
#include <cstdlib>
#include <new>

namespace {
bool counting = false;
size_t allocations = 0;
int checks = 0, failures = 0;
void check(bool ok, const char* label) {
    ++checks;
    if (!ok && failures++ < 20) std::printf("FAIL %s\n", label);
}
template<class F> size_t measure(F call) {
    allocations = 0;
    counting = true;
    call();
    counting = false;
    return allocations;
}
struct Digest {
    uint64_t value = 1469598103934665603ULL;
    template<class T> void add(const T& v) {
        const auto* p = reinterpret_cast<const unsigned char*>(&v);
        for (size_t i = 0; i < sizeof(v); ++i) { value ^= p[i]; value *= 1099511628211ULL; }
    }
    void mesh(const weva::Mesh& m) {
        add(static_cast<uint64_t>(m.vertices.size()));
        add(static_cast<uint64_t>(m.indices.size()));
        for (const auto& v : m.vertices) {
            add(v.position.x); add(v.position.y); add(v.tex_coord.x); add(v.tex_coord.y);
            add(v.color.r); add(v.color.g); add(v.color.b); add(v.color.a);
        }
        for (uint32_t i : m.indices) add(i);
    }
};
struct KeepingBackend : weva::RenderInterface {
    std::vector<weva::Mesh> draws;
    Digest events;
    KeepingBackend() { draws.reserve(512); }
    weva::GeometryHandle compile_geometry(const std::vector<weva::Vertex>&,
                                          const std::vector<uint32_t>&) override {
        check(false, "owned submission bypasses compile"); return {};
    }
    void render_geometry(weva::GeometryHandle, weva::Vec2, weva::TextureHandle) override {}
    void release_geometry(weva::GeometryHandle) override {}
    void render_mesh(std::vector<weva::Vertex> v, std::vector<uint32_t> i,
                     weva::Vec2 translation, weva::TextureHandle texture) override {
        events.add(0); events.add(translation.x); events.add(translation.y);
        events.add(bool(texture));
        weva::Mesh m; m.vertices = std::move(v); m.indices = std::move(i);
        for (uint32_t index : m.indices) check(index < m.vertices.size(), "valid submitted index");
        events.mesh(m);
        draws.push_back(std::move(m));
    }
    void filter_backdrop(const std::vector<weva::Vertex>& v, const std::vector<uint32_t>& i,
                         const weva::BackdropEffect& effect) override {
        events.add(1); events.add(effect.blur_radius);
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) events.add(effect.color.m[r][c]);
            events.add(effect.color.add[r]);
        }
        events.add(effect.color.alpha);
        weva::Mesh m; m.vertices = v; m.indices = i;
        events.mesh(m); draws.push_back(std::move(m));
    }
    void render_rounded_rect(const weva::RoundedRect& shape, const std::vector<weva::Vertex>& v,
                             const std::vector<uint32_t>& i) override {
        render_rounded_rect_owned(shape,v,i);
    }
    void render_rounded_rect_owned(const weva::RoundedRect& shape, std::vector<weva::Vertex> v,
                                   std::vector<uint32_t> i) override {
        events.add(4); events.add(shape.x); events.add(shape.y);
        events.add(shape.width); events.add(shape.height);
        for (const auto& corner : shape.radii) for (double r : corner) events.add(r);
        events.add(shape.color.r); events.add(shape.color.g); events.add(shape.color.b); events.add(shape.color.a);
        weva::Mesh m; m.vertices = std::move(v); m.indices = std::move(i);
        events.mesh(m); draws.push_back(std::move(m));
    }
    weva::TextureHandle load_texture(std::string_view, weva::Vec2i*) override { return {}; }
    weva::TextureHandle generate_texture(const std::vector<uint8_t>& bytes, weva::Vec2i size) override {
        events.add(2); events.add(size.x); events.add(size.y);
        for (uint8_t byte : bytes) events.add(byte);
        return {1};
    }
    void release_texture(weva::TextureHandle) override {}
    void set_scissor(const weva::Recti* rect) override {
        events.add(3); events.add(bool(rect));
        if (rect) { events.add(rect->x); events.add(rect->y); events.add(rect->width); events.add(rect->height); }
    }
    uint64_t geometry() const {
        Digest d;
        for (const auto& m : draws) d.mesh(m);
        return d.value;
    }
};
void size_box(weva::Box& box, double x, double y, double w, double h) {
    box.x = x; box.y = y; box.width = w; box.height = h;
    box.vis_x0 = box.vis_y0 = 0; box.vis_x1 = w; box.vis_y1 = h;
    box.font_size = 16;
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
    Digest output;
    LayoutContext ctx;
    ctx.viewport_width_px = 300; ctx.viewport_height_px = 200;
    // Warm syntax first. The only remaining allocations for a solid quad
    // should be its two buffers, which the backend takes without copying.
    for (const char* opacity : {"1", "0.37", "0"}) {
        ComputedStyle style;
        style.set("background-color", "#b87632"); style.set("opacity", opacity);
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block, nullptr, &style);
        size_box(tree[root], 2.25, 3.5, 80, 40);
        KeepingBackend backend;
        paint_tree(tree, root, ctx, &backend);
        backend.draws.clear();
        const size_t n = measure([&] { paint_tree(tree, root, ctx, &backend); });
        check(n <= 2, "solid quad uses only its original two geometry buffers");
        check(!backend.draws.empty(), "opacity does not change draw availability");
        output.add(backend.geometry());
        output.add(backend.events.value);
        const uint64_t saved = backend.geometry();
        KeepingBackend next;
        style.set("background-color", "#2233ff");
        paint_tree(tree, root, ctx, &next);
        check(saved == backend.geometry(), "previous backend buffers survive a later paint");
    }
    {
        ComputedStyle rounded;
        rounded.set("background-color", "#123456");
        rounded.set("border-top-left-radius", "8px");
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block, nullptr, &rounded);
        size_box(tree[root],0,0,80,30);
        KeepingBackend backend;
        paint_tree(tree,root,ctx,&backend);
        const uint64_t expected = backend.geometry();
        backend.draws.clear();
        const size_t count = measure([&] { paint_tree(tree,root,ctx,&backend); });
        check(backend.geometry() == expected, "rounded geometry stable after warmup");
        check(count <= 3, "rounded submission avoids buffer copies and temporary AA rings");
        std::printf("rounded paint allocations: %zu; hash %016llx\n",count,
            static_cast<unsigned long long>(expected));
    }
    {
        BorderRadii radius;
        radius.top_left.x_radius = radius.top_left.y_radius = 8;
        Mesh direct;
        const size_t count = measure([&] {
            tessellate_rounded_rect(Rect(0,0,80,30),radius,LinearColor::white(),&direct,8,false);
        });
        check(count == 2, "unfeathered rounded fan allocates only its two output buffers");
        check(direct.vertices.size() == 13 && direct.indices.size() == 36, "mixed rounded/square fan topology preserved");
    }
    Mesh positive;
    check(measure([&] { tessellate_rect(Rect(0, 0, 10, 10), LinearColor::white(), &positive); }) > 0,
          "allocation counter sees libweva geometry allocations");

    int cases = 0;
    // Exercise combined filter/transform/opacity paths and nested clips, as
    // well as clipping rejection, whole containment, texture and text draws.
    for (int clipping = 0; clipping < 3; ++clipping)
    for (const char* transform : {"none", "translate(2.125px, -1.75px)", "rotate(17deg)"})
    for (const char* filter : {"none", "brightness(0.73) saturate(0.6) opacity(0.81)"})
    for (const char* opacity : {"1", "0.37", "0"}) {
        ++cases;
        ComputedStyle outer, inner, leaf, text_style;
        outer.set("overflow", clipping ? "hidden" : "visible");
        outer.set("border-top-left-radius", "9px"); outer.set("border-bottom-right-radius", "11px");
        inner.set("clip-path", clipping == 2 ? "polygon(0% 0%, 100% 0%, 76% 45%, 100% 100%, 0% 100%)" : "none");
        leaf.set("background-color", "rgba(71,123,231,0.63)");
        leaf.set("transform", transform); leaf.set("filter", filter); leaf.set("opacity", opacity);
        leaf.set("backdrop-filter", "brightness(0.6)");
        leaf.set("box-shadow", "1px 2px 0px #223344");
        leaf.set("border-top-left-radius", "7px"); leaf.set("outline-width", "1px");
        leaf.set("outline-style", "solid"); leaf.set("outline-color", "#22aabb");
        text_style.set("color", "#987654"); text_style.set("text-decoration-line", "underline line-through");
        text_style.set("text-shadow", "1px 1px 0px #223344");
        StubFont font;
        GlyphAtlas atlas;
        PaintContext paint;
        paint.font = &font; paint.face = font.load_face({}, 0); paint.atlas = &atlas;
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block, nullptr, &outer);
        const BoxId clip = tree.create(BoxKind::Block, nullptr, &inner);
        const BoxId box = tree.create(BoxKind::Block, nullptr, &leaf);
        const BoxId text = tree.create(BoxKind::Text, nullptr, &text_style);
        tree.append_child(root, clip); tree.append_child(clip, box); tree.append_child(box, text);
        size_box(tree[root], 0, 0, 82, 55); size_box(tree[clip], 1.5, 2.75, 76, 48);
        size_box(tree[box], clipping == 2 ? -4.25 : 6.25, 1.125, 75, 39);
        size_box(tree[text], 2.5, 3.25, 62, 20); tree[text].text = "Text probe";
        KeepingBackend backend;
        paint.backend = &backend;
        paint_tree(tree, root, ctx, paint);
        const uint64_t saved = backend.geometry();
        output.add(saved); output.add(backend.events.value);
        KeepingBackend next;
        paint.backend = &next;
        tree[box].x = 200;
        paint_tree(tree, root, ctx, paint);
        output.add(next.geometry()); output.add(next.events.value);
        check(saved == backend.geometry(), "filtered/clipped buffers remain owned by first backend");
    }

    // Inputs measure their decoration width from the untransformed glyphs.
    // Cover the post-submit consumer alongside selection, composition, caret,
    // and a clipped/transformed control. All draws enter the comparison digest.
    for (const char* transform : {"none", "translate(5px,2px) rotate(9deg)"}) {
        ++cases;
        Element input("input"); input.set_attribute("value", "Decorated input");
        ComputedStyle style;
        style.set("color", "#123456"); style.set("font-size", "16px");
        style.set("text-decoration-line", "underline line-through");
        style.set("text-decoration-style", "double"); style.set("transform", transform);
        style.set("filter", "brightness(0.8)"); style.set("opacity", "0.6");
        BoxTree tree;
        const BoxId root = tree.create(BoxKind::Block, &input, &style);
        size_box(tree[root], 1.25, 2.5, 140, 28);
        StubFont font; GlyphAtlas atlas;
        PaintContext paint;
        KeepingBackend backend;
        paint.backend = &backend; paint.font = &font; paint.atlas = &atlas; paint.face = font.load_face({}, 0);
        paint.caret.element = &input; paint.caret.visible = true; paint.caret.index = 3;
        paint.caret.selection_from = 1; paint.caret.selection_to = 4;
        paint.caret.composition_from = 5; paint.caret.composition_to = 8;
        paint_tree(tree, root, ctx, paint);
        check(backend.draws.size() >= 7, "input paints text and double decorations with edit markers");
        output.add(backend.geometry()); output.add(backend.events.value);
    }
    std::printf("draw mesh allocations: %d cases, %d checks, %d failures; geometry hash %016llx\n",
                cases, checks, failures, static_cast<unsigned long long>(output.value));
    return failures ? 1 : 0;
}
