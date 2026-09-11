#include "../src/godot_font.h"
#include <godot_cpp/classes/font.hpp>
#include <godot_cpp/classes/font_file.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/ref_counted.hpp>
#include <godot_cpp/classes/text_server.hpp>
#include <godot_cpp/classes/text_server_manager.hpp>
#include <godot_cpp/classes/theme_db.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/godot.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/transform2d.hpp>
#include <godot_cpp/variant/utility_functions.hpp>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

using namespace godot;

class WevaFontBackendTests : public RefCounted {
    GDCLASS(WevaFontBackendTests, RefCounted)
    int checks_ = 0, failures_ = 0;
    void check(bool ok, const char* message) {
        ++checks_;
        if (!ok) {
            ++failures_;
            std::fprintf(stderr, "FAIL font backend: %s\n", message);
        }
    }
    static bool near(double a, double b) { return std::abs(a-b) < 0.000001; }

    struct PositionedSnapshot {
        weva_shaped_glyph placement{};
        double advance = 0, bx = 0, by = 0;
        int32_t width = 0, height = 0, bitmap_width = 0, bitmap_height = 0;
        bool raster_ok = false;
        std::vector<uint8_t> alpha, rgba;
        bool operator==(const PositionedSnapshot& o) const {
            const bool equal = placement.glyph == o.placement.glyph && placement.cluster == o.placement.cluster &&
                placement.x_advance == o.placement.x_advance && placement.y_advance == o.placement.y_advance &&
                placement.x_offset == o.placement.x_offset && placement.y_offset == o.placement.y_offset &&
                advance == o.advance && bx == o.bx && by == o.by && width == o.width && height == o.height &&
                bitmap_width == o.bitmap_width && bitmap_height == o.bitmap_height &&
                raster_ok == o.raster_ok && alpha == o.alpha && rgba == o.rgba;
            if (!equal) std::fprintf(stderr,
                "glyph difference: cluster %u/%u advance %.17g/%.17g offset %.17g,%.17g/%.17g,%.17g metrics %.17g/%.17g box %dx%d/%dx%d bitmap %dx%d/%dx%d alpha %zu/%zu rgba %zu/%zu\n",
                placement.cluster, o.placement.cluster, placement.x_advance, o.placement.x_advance,
                placement.x_offset, placement.y_offset, o.placement.x_offset, o.placement.y_offset,
                advance, o.advance, width, height, o.width, o.height, bitmap_width, bitmap_height,
                o.bitmap_width, o.bitmap_height, alpha.size(), o.alpha.size(), rgba.size(), o.rgba.size());
            return equal;
        }
    };

    std::vector<PositionedSnapshot> snapshot_run(weva_godot::GodotFontBackend& backend,
                                                uint64_t face, const char* text, double px) {
        weva_font_backend table{};
        weva_shape_glyphs_fn shape = nullptr;
        backend.fill(&table, &shape);
        const size_t length = std::char_traits<char>::length(text);
        const size_t count = shape(table.user_data, face, text, length, px, nullptr, 0);
        check(count > 0, "native run supplies glyphs");
        std::vector<weva_shaped_glyph> glyphs(count);
        check(shape(table.user_data, face, text, length, px, glyphs.data(), count) == count,
              "reused run preserves the sizing/fill protocol");
        std::vector<PositionedSnapshot> result;
        for (const auto& glyph : glyphs) {
            PositionedSnapshot saved;
            saved.placement = glyph;
            // Handles are local to each backend. Compare the native metrics
            // and bitmap they resolve to, including the invisible-glyph case.
            saved.placement.glyph = glyph.glyph ? 1 : 0;
            if (glyph.glyph) {
                weva_glyph_bitmap bitmap{};
                saved.raster_ok = table.rasterize(table.user_data, face, glyph.glyph, px, &bitmap) != 0;
                // Initialize both sides' raster caches before comparing their
                // metrics. Native bitmap-font misses can replace provisional
                // metrics with zeros on the first raster request.
                check(table.glyph_metrics(table.user_data, face, glyph.glyph, px,
                      &saved.advance, &saved.bx, &saved.by, &saved.width, &saved.height),
                      "shaped handle resolves through its receiving backend");
                saved.bitmap_width = bitmap.width;
                saved.bitmap_height = bitmap.height;
                const size_t bytes = static_cast<size_t>(bitmap.width) * bitmap.height;
                if (bitmap.alpha) saved.alpha.assign(bitmap.alpha, bitmap.alpha + bytes);
                if (bitmap.rgba) saved.rgba.assign(bitmap.rgba, bitmap.rgba + 4 * bytes);
            }
            result.push_back(std::move(saved));
        }
        return result;
    }

    std::vector<PositionedSnapshot> synthetic_oracle(TextServer* ts, const RID& painted,
            const PackedByteArray& data, const char* text, double px) {
        // Primary synthesis preserves regular advances; rasterization and
        // automatic fallback selection remain those of the styled native face.
        // Neither oracle face uses the adapter's variant or shared-run paths.
        const RID regular = ts->create_font();
        ts->font_set_data(regular, data);
        ts->font_set_subpixel_positioning(regular, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
        std::vector<PositionedSnapshot> result;
        {
            weva_godot::GodotFontBackend raster, metrics;
            result = snapshot_run(raster, raster.adopt(painted), text, px);
            const auto positions = snapshot_run(metrics, metrics.adopt(regular), text, px);
            const RID shaped = ts->create_shaped_text();
            TypedArray<RID> chain; chain.push_back(painted);
            ts->shaped_text_add_string(shaped, String::utf8(text), chain, static_cast<int64_t>(std::lround(px)));
            std::vector<bool> primary;
            for (const Dictionary glyph : ts->shaped_text_get_glyphs(shaped)) {
                const RID from = glyph["font_rid"];
                const int64_t repeat = glyph["repeat"];
                for (int64_t i = 0; i < repeat; ++i) primary.push_back(from == painted);
            }
            ts->free_rid(shaped);
            check(result.size() == positions.size(), "synthesis preserves native glyph count");
            check(result.size() == primary.size(), "native oracle identifies primary and fallback glyphs");
            for (size_t i = 0; i < std::min(result.size(), positions.size()); ++i) {
                check(result[i].placement.cluster == positions[i].placement.cluster,
                      "synthesis preserves native source clusters");
                if (i < primary.size() && primary[i]) {
                    result[i].placement.x_advance = positions[i].placement.x_advance;
                    result[i].advance = positions[i].advance;
                }
            }
        }
        ts->free_rid(regular);
        return result;
    }

    void check_shared_runs(TextServer* ts, const Ref<Font>& primary) {
        const Ref<FontFile> file = primary;
        check(file.is_valid() && !file->get_data().is_empty(), "shared-shaping fixture has outline data");
        if (file.is_null() || file->get_data().is_empty()) return;
        const PackedByteArray data = file->get_data();
        const TypedArray<RID> base_fonts = primary->get_rids().duplicate();
        for (int weight : {700, 900}) for (int italic : {0, 1}) {
            const RID native = ts->create_font();
            ts->font_set_data(native, data);
            ts->font_set_subpixel_positioning(native, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
            ts->font_set_embolden(native, weight == 900 ? 0.9 : 0.6);
            if (italic) ts->font_set_transform(native, Transform2D(1.0, 0.0, 0.2, 1.0, 0.0, 0.0));
            {
                // A borrowed independently configured font never uses shared
                // synthesis storage, so it is a separate native oracle.
                weva_godot::GodotFontBackend producer;
                weva_font_backend producer_table{};
                weva_shape_glyphs_fn shape = nullptr;
                producer.fill(&producer_table, &shape);
                const auto base = producer.adopt(base_fonts, data);
                const auto face = producer_table.variant(producer_table.user_data, base, weight, italic);
                for (double px : {13.0, 13.49, 13.51, 32.0}) for (const char* text : {"office AV ffi", "éA", "q\u0323\u0301",
                                                               "سَلَام", "👩‍🚀A", "\u200bA",
                                                               "AV سَلَام ffi 👩‍🚀 q\u0323\u0301",
                                                               "q\u0301\u0301 q\u0323\u0301"}) {
                    const int failures_before = failures_;
                    const auto oracle = synthetic_oracle(ts, native, data, text, px);
                    check(snapshot_run(producer, face, text, px) == oracle,
                          "first synthesized run matches native positions, clusters and pixels");
                    {
                        weva_godot::GodotFontBackend consumer;
                        weva_font_backend table{};
                        consumer.fill(&table);
                        const auto other_base = consumer.adopt(base_fonts, data);
                        const auto other_face = table.variant(table.user_data, other_base, weight, italic);
                        uint32_t unrelated = 0;
                        check(table.glyph_index(table.user_data, other_face, 'Z', &unrelated),
                              "consumer starts with a different local glyph-handle mapping");
                        check(snapshot_run(consumer, other_face, text, px) == oracle,
                              "cross-document reuse remaps glyphs and preserves native output");
                        consumer.clear();
                        const auto replacement = consumer.adopt(base_fonts, data);
                        const auto replacement_face = table.variant(table.user_data, replacement, weight, italic);
                        check(snapshot_run(consumer, replacement_face, text, px) == oracle,
                              "clear and re-adoption preserve shared shaping output");
                    }
                    check(snapshot_run(producer, face, text, px) == oracle,
                          "freeing another document preserves the producer's glyphs");
                    if (failures_ != failures_before) std::fprintf(stderr, "shared case: weight %d italic %d px %.2f text %s\n", weight, italic, px, text);
                }
                const auto saved = synthetic_oracle(ts, native, data, "office AV ffi", 13);
                // Exceed both entry and glyph budgets; the original output
                // remains valid whether its shared entry survives or is evicted.
                for (int i = 0; i < 240; ++i) {
                    const std::string text = (i < 160 ? std::string("entry") : std::string(64, 'A')) + std::to_string(i);
                    check(shape(producer_table.user_data, face, text.data(), text.size(), 13, nullptr, 0) > 0,
                          "bounded cache accepts a changing label");
                }
                check(snapshot_run(producer, face, "office AV ffi", 13) == saved,
                      "entry and glyph eviction preserve native output");
                weva_godot::release_shared_font_variants();
                check(snapshot_run(producer, face, "office AV ffi", 13) == saved,
                      "clearing idle font storage preserves a live shaped font");
                // More than the per-run byte/glyph bounds must also work.
                const std::string long_text(600, 'A');
                check(snapshot_run(producer, face, long_text.c_str(), 13) ==
                      synthetic_oracle(ts, native, data, long_text.c_str(), 13),
                      "uncached long runs preserve complete positions and pixels");
            }
            ts->free_rid(native);
        }
        const PackedByteArray alternate = FileAccess::get_file_as_bytes("res://outline_fixture.ttf");
        check(!alternate.is_empty(), "shared-shaping replacement has different outline data");
        if (alternate.is_empty()) return;
        weva_godot::GodotFontBackend replaced;
        weva_font_backend table{};
        replaced.fill(&table);
        for (const PackedByteArray& bytes : {data, alternate, data}) {
            const RID native = ts->create_font();
            ts->font_set_data(native, bytes);
            ts->font_set_embolden(native, 0.6);
            const RID fallback = ts->create_font();
            ts->font_set_data(fallback, alternate);
            {
                const auto oracle = synthetic_oracle(ts, native, bytes, "office AV ffi", 13);
                replaced.clear();
                const auto base = replaced.adopt(base_fonts, bytes);
                const auto face = table.variant(table.user_data, base, 700, 0);
                check(snapshot_run(replaced, face, "office AV ffi", 13) == oracle,
                      "file replacement and returning to prior data use the exact synthesis inputs");
                // These fallback fonts are private to this scope and never
                // change. Different ordered lists must preserve native output.
                for (bool immutable : {false, true}) {
                    for (int copies : {1, 2}) {
                        TypedArray<RID> chain = base_fonts.duplicate();
                        for (int i = 0; i < copies; ++i) chain.push_back(fallback);
                        weva_godot::GodotFontBackend consumer;
                        weva_font_backend other{};
                        consumer.fill(&other);
                        const auto other_base = consumer.adopt(chain, bytes, immutable);
                        const auto other_face = other.variant(other.user_data, other_base, 700, 0);
                        check(snapshot_run(consumer, other_face, "office AV ffi", 13) == oracle,
                              "borrowed and immutable fallback chains preserve primary glyphs");
                    }
                }
            }
            ts->free_rid(fallback);
            ts->free_rid(native);
        }
    }

    struct FontSnapshot {
        double ascent = 0, descent = 0, gap = 0, advance = 0, bx = 0, by = 0;
        int32_t width = 0, height = 0;
        std::vector<uint8_t> alpha;
        bool operator==(const FontSnapshot& o) const {
            return ascent == o.ascent && descent == o.descent && gap == o.gap &&
                advance == o.advance && bx == o.bx && by == o.by &&
                width == o.width && height == o.height && alpha == o.alpha;
        }
    };

    FontSnapshot snapshot(weva_godot::GodotFontBackend& backend, uint64_t face) {
        weva_font_backend table{};
        backend.fill(&table);
        FontSnapshot result;
        uint32_t glyph = 0;
        check(table.face_metrics(table.user_data, face, 32, &result.ascent, &result.descent, &result.gap),
              "variant face metrics remain live");
        check(table.glyph_index(table.user_data, face, 'A', &glyph), "variant resolves a native glyph");
        check(table.glyph_metrics(table.user_data, face, glyph, 32, &result.advance, &result.bx,
                                  &result.by, &result.width, &result.height), "variant glyph metrics remain live");
        weva_glyph_bitmap bitmap{};
        check(table.rasterize(table.user_data, face, glyph, 32, &bitmap), "variant raster remains live");
        if (bitmap.alpha) result.alpha.assign(bitmap.alpha, bitmap.alpha + bitmap.width * bitmap.height);
        return result;
    }

    FontSnapshot synthetic_font_oracle(TextServer* ts, const RID& painted, const PackedByteArray& data) {
        FontSnapshot result;
        const RID regular = ts->create_font();
        ts->font_set_data(regular, data);
        ts->font_set_subpixel_positioning(regular, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
        {
            weva_godot::GodotFontBackend raster, metrics;
            result = snapshot(raster, raster.adopt(painted));
            result.advance = snapshot(metrics, metrics.adopt(regular)).advance;
        }
        ts->free_rid(regular);
        return result;
    }

    void check_variants(TextServer* ts, const Ref<Font>& primary, const TypedArray<RID>& fonts) {
        const Ref<FontFile> file = primary;
        check(file.is_valid() && !file->get_data().is_empty(), "variant fixture has native outline data");
        if (file.is_null()) return;
        PackedByteArray data = file->get_data();
        const PackedByteArray fixture_data = FileAccess::get_file_as_bytes("res://outline_fixture.ttf");
        check(!fixture_data.is_empty(), "eviction fixture has independent TrueType data");
        if (fixture_data.is_empty()) return;
        const RID fixture_font = ts->create_font();
        ts->font_set_data(fixture_font, fixture_data);
        ts->font_set_subpixel_positioning(fixture_font, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
        ts->font_set_embolden(fixture_font, 0.6);
        FontSnapshot fixture_expected;
        {
            fixture_expected = synthetic_font_oracle(ts, fixture_font, fixture_data);
        }
        ts->free_rid(fixture_font);
        for (bool reverse : {false, true}) {
            weva_godot::GodotFontBackend backend;
            const auto base = backend.adopt(fonts, data);
            weva_font_backend table{};
            backend.fill(&table);
            const auto regular = snapshot(backend, base);
            uint64_t faces[2]{};
            FontSnapshot saved[2];
            for (int pass = 0; pass < 2; ++pass) {
                const int slot = reverse ? 1 - pass : pass;
                const int weight = slot ? 900 : 700;
                faces[slot] = table.variant(table.user_data, base, weight, 0);
                saved[slot] = snapshot(backend, faces[slot]);
                const RID expected_font = ts->create_font();
                ts->font_set_data(expected_font, data);
                ts->font_set_subpixel_positioning(expected_font, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
                ts->font_set_embolden(expected_font, slot ? 0.9 : 0.6);
                {
                    check(saved[slot] == synthetic_font_oracle(ts, expected_font, data),
                          "700 and 900 match independently configured native fonts in either request order");
                }
                ts->free_rid(expected_font);
            }
            check(faces[0] != faces[1] && !(saved[0] == saved[1]), "different synthesis strengths remain distinct");
            for (int weight : {700, 900}) {
                const auto face = table.variant(table.user_data, base, weight, 1);
                const auto actual = snapshot(backend, face);
                const RID expected_font = ts->create_font();
                ts->font_set_data(expected_font, data);
                ts->font_set_subpixel_positioning(expected_font, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
                ts->font_set_embolden(expected_font, weight == 900 ? 0.9 : 0.6);
                ts->font_set_transform(expected_font, Transform2D(1.0, 0.0, 0.2, 1.0, 0.0, 0.0));
                {
                    check(actual == synthetic_font_oracle(ts, expected_font, data),
                          "italic and emboldening strengths combine without aliasing upright variants");
                }
                ts->free_rid(expected_font);
            }
            check(snapshot(backend, base) == regular, "synthesis never changes the borrowed regular font");
            check(table.variant(table.user_data, base, 800, 0) == faces[1], "equal synthesis inputs share a local face");
            // Clear another owner of the same immutable font, then evict the
            // pool's oldest entries while the first backend is still drawing.
            {
                weva_godot::GodotFontBackend other;
                weva_font_backend other_table{};
                other.fill(&other_table);
                const auto other_base = other.adopt(fonts, data);
                const auto face = other_table.variant(other_table.user_data, other_base, 700, 0);
                check(snapshot(other, face) == saved[0], "separate documents get the same synthetic font pixels");
            }
            for (int i = 0; i < 10; ++i) {
                // OpenType ignores trailing bytes, giving distinct valid file
                // inputs without making the test depend on installed fonts.
                PackedByteArray changed = fixture_data;
                changed.append(static_cast<uint8_t>(i));
                weva_godot::GodotFontBackend other;
                weva_font_backend other_table{};
                other.fill(&other_table);
                const auto face = other.adopt(fonts, changed);
                const auto variant = other_table.variant(other_table.user_data, face, 700, 0);
                check(snapshot(other, variant) == fixture_expected, "changed file data is accepted after pool eviction");
            }
            check(snapshot(backend, faces[0]) == saved[0] && snapshot(backend, faces[1]) == saved[1],
                  "eviction and other-document destruction preserve active font owners");
            weva_godot::release_shared_font_variants();
            check(snapshot(backend, faces[0]) == saved[0], "clearing shared idle storage preserves a live backend");
            backend.clear();
            const auto replacement = backend.adopt(fonts, data);
            check(replacement != base, "resource replacement does not reuse a stale local face ID");
            const auto replacement_variant = table.variant(table.user_data, replacement, 700, 0);
            check(snapshot(backend, replacement_variant) == saved[0], "replacement reconstructs equivalent native font pixels");
        }
    }

    void check_document(TextServer* ts, const weva_font_backend& table,
                        weva_shape_glyphs_fn positioned, uint64_t face,
                        const CharString& text, const std::vector<Dictionary>& expected, int px) {
        struct Quad { double x, y, width, height; };
        std::vector<Quad> quads;
        double pen = 0;
        for (const auto& glyph : expected) {
            const RID font = glyph["font_rid"];
            const int64_t index = glyph["index"];
            if (!font.is_valid()) return; // no native bitmap to compare on this platform
            if (index) {
                const Vector2i size(px, 0);
                const Rect2 uv = ts->font_get_glyph_uv_rect(font, size, index);
                if (uv.size.x > 0 && uv.size.y > 0) {
                    const Vector2 offset = glyph["offset"];
                    const Vector2 bearing = ts->font_get_glyph_offset(font, size, index);
                    quads.push_back({pen + offset.x + bearing.x, offset.y + bearing.y,
                                     uv.size.x, uv.size.y});
                }
            }
            pen += static_cast<double>(glyph["advance"]);
        }
        if (quads.empty()) return;
        weva_config config{};
        config.viewport_width = 800;
        config.viewport_height = 200;
        config.use_user_agent_stylesheet = 1;
        const auto doc = weva_document_create(&config);
        weva_document_set_font_backend(doc, &table, face);
        check(weva_document_set_font_shaper(doc, positioned) == WEVA_OK, "document accepts native positioned shaper");
        const std::string html = std::string("<body><span id=run>") + text.get_data() + "</span></body>";
        // Padding keeps negative ink bearings inside the UA body's clip.
        const std::string css = "body{margin:32px}#run{display:inline-block;padding:24px;line-height:96px;font-size:" +
                                std::to_string(px) + "px}";
        check(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK &&
              weva_document_add_css(doc, css.data(), css.size()) == WEVA_OK &&
              weva_document_update(doc, 0) == WEVA_OK, "native font renders through the C ABI document pipeline");
        size_t count = 0;
        const auto draws = weva_document_draws(doc, &count);
        std::vector<weva_vertex> vertices;
        for (size_t i = 0; i < count; ++i)
            if (draws[i].texture_id)
                vertices.insert(vertices.end(), draws[i].vertices, draws[i].vertices + draws[i].vertex_count);
        check(vertices.size() == quads.size() * 4, "document emits one unclipped quad per native bitmap");
        if (vertices.size() != quads.size() * 4)
            std::fprintf(stderr, "  text=%s px=%d vertices=%zu expected=%zu\n",
                         text.get_data(), px, vertices.size(), quads.size() * 4);
        if (vertices.size() == quads.size() * 4) {
            // The document chooses its line baseline. All glyphs must share it,
            // retaining native relative positions after the core's pixel snap.
            const double baseline = vertices[0].y - quads[0].y;
            for (size_t i = 0; i < quads.size(); ++i) {
                const auto& q = quads[i];
                const auto& tl = vertices[i * 4];
                const auto& br = vertices[i * 4 + 2];
                check(near(tl.x, std::round(56 + q.x)) && near(tl.y, std::round(baseline + q.y)) &&
                      near(br.x - tl.x, q.width) && near(br.y - tl.y, q.height),
                      "document bitmap quad preserves native mark placement and fallback metrics");
            }
        }
        weva_document_destroy(doc);
    }

    // Stock Godot 4.7 corrupts glyph ranges past 32 emoji sub-runs in one
    // script run, crashes on the script run after that, and mishandles more
    // than 128 open brackets the same way. The adapter shapes such text in
    // pieces. The reference is the same unit tiled three times plus the tail,
    // which the engine's starter stacks hold: its first unit has only a
    // following neighbour, its middle unit both, its last unit the tail. The
    // long text must read as first, middle repeated, last, tail, so every
    // kerning pair across a unit edge or a piece edge is checked on every
    // engine, patched or not.
    void check_engine_stack_limits(const weva_font_backend& table, weva_shape_glyphs_fn positioned,
                                   uint64_t face) {
        const auto shape_text = [&](const String& text) {
            const CharString utf8 = text.utf8();
            const size_t count = positioned(table.user_data, face, utf8.get_data(), utf8.length(), 32, nullptr, 0);
            std::vector<weva_shaped_glyph> glyphs(count);
            positioned(table.user_data, face, utf8.get_data(), utf8.length(), 32, glyphs.data(), count);
            return glyphs;
        };
        struct Case {
            const char* unit;
            int repeat;
            const char* tail;
            bool rtl;
            const char* label;
        };
        const Case cases[] = {
            {"á😀b", 33, "", false, "33 emoji sub-runs"},
            {"á😀b", 65, "Ж", false, "65 emoji sub-runs then a second script"},
            {"á😀b", 33, "Ж😀б", false, "emoji sub-runs continuing in a second script"},
            {"( ", 129, "", false, "129 open brackets"},
            {"ب😀", 33, "", true, "33 emoji sub-runs in a right-to-left run"},
        };
        for (const Case& c : cases) {
            const String unit = String::utf8(c.unit);
            const String tail = String::utf8(c.tail);
            String whole, reference_text;
            for (int i = 0; i < c.repeat; ++i) whole += unit;
            for (int i = 0; i < 3; ++i) reference_text += unit;
            whole += tail;
            reference_text += tail;
            const auto reference_glyphs = shape_text(reference_text);
            const auto whole_glyphs = shape_text(whole);
            const uint32_t unit_bytes = static_cast<uint32_t>(unit.utf8().length());
            // Groups in visual order: unit 0, 1, 2 and the tail (3), each
            // keeping its own internal visual order.
            std::vector<std::pair<int, std::vector<weva_shaped_glyph>>> groups;
            for (const auto& g : reference_glyphs) {
                const int k = std::min<int>(3, static_cast<int>(g.cluster / unit_bytes));
                if (groups.empty() || groups.back().first != k) groups.push_back({k, {}});
                groups.back().second.push_back(g);
            }
            std::vector<weva_shaped_glyph> expected;
            const int middle = c.repeat - 2;
            for (const auto& group : groups) {
                const auto emit = [&](uint32_t shift) {
                    for (auto g : group.second) {
                        g.cluster += shift;
                        expected.push_back(g);
                    }
                };
                if (group.first == 1) {
                    for (int i = 0; i < middle; ++i) emit(static_cast<uint32_t>(c.rtl ? middle - 1 - i : i) * unit_bytes);
                } else if (group.first >= 2) {
                    emit(static_cast<uint32_t>(middle - 1) * unit_bytes);
                } else {
                    emit(0);
                }
            }
            check(!reference_glyphs.empty() && whole_glyphs.size() == expected.size(),
                  "long text keeps every glyph past the engine stack limits");
            if (whole_glyphs.size() != expected.size()) {
                std::fprintf(stderr, "  %s: %zu glyphs, expected %zu\n", c.label, whole_glyphs.size(), expected.size());
                continue;
            }
            bool same = true;
            for (size_t i = 0; i < expected.size() && same; ++i) {
                const auto& a = whole_glyphs[i];
                const auto& b = expected[i];
                same = a.glyph == b.glyph && a.cluster == b.cluster && near(a.x_advance, b.x_advance) &&
                       near(a.y_advance, b.y_advance) && near(a.x_offset, b.x_offset) && near(a.y_offset, b.y_offset);
                if (!same)
                    std::fprintf(stderr, "  %s: glyph %zu differs: id %u/%u cluster %u/%u advance %.17g/%.17g offset %.17g,%.17g/%.17g,%.17g\n",
                                 c.label, i, a.glyph, b.glyph, a.cluster, b.cluster, a.x_advance, b.x_advance,
                                 a.x_offset, a.y_offset, b.x_offset, b.y_offset);
            }
            check(same, "long text shapes exactly as its units do past the engine stack limits");
        }
    }

    // A face adopted with its bytes measures through a private quarter-pixel
    // copy: above 20 px its advances are fractional where the resource's own
    // automatic mode snaps them, and equal to a reference font with the same
    // setting. The resource itself keeps its mode.
    void check_fractional_positioning(TextServer* ts, const Ref<Font>& primary) {
        const Ref<FontFile> file = primary;
        if (file.is_null() || file->get_data().is_empty()) return;
        const PackedByteArray data = file->get_data();
        const RID borrowed = primary->get_rids()[0];
        const auto saved = ts->font_get_subpixel_positioning(borrowed);
        ts->font_set_subpixel_positioning(borrowed, TextServer::SUBPIXEL_POSITIONING_AUTO);
        const auto width_of = [&](const RID& font, const String& text, int px) {
            const RID shaped = ts->create_shaped_text();
            TypedArray<RID> list;
            list.push_back(font);
            ts->shaped_text_add_string(shaped, text, list, px);
            ts->shaped_text_shape(shaped);
            double width = 0;
            for (const Dictionary g : ts->shaped_text_get_glyphs(shaped))
                width += static_cast<double>(g["advance"]) * static_cast<int64_t>(g["repeat"]);
            ts->free_rid(shaped);
            return width;
        };
        const RID reference = ts->create_font();
        ts->font_set_data(reference, data);
        ts->font_set_subpixel_positioning(reference, TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
        weva_godot::GodotFontBackend backend;
        TypedArray<RID> only_primary;
        only_primary.push_back(borrowed);
        const uint64_t face = backend.adopt(only_primary, data, true);
        weva_font_backend table{};
        weva_shape_glyphs_fn positioned = nullptr;
        backend.fill(&table, &positioned);
        const String text = "DUST & IRON Wide Words 0123";
        const CharString utf8 = text.utf8();
        bool fractional = false, matches = true;
        for (int px : {32, 25, 14}) {
            const size_t count = positioned(table.user_data, face, utf8.get_data(), utf8.length(), px, nullptr, 0);
            std::vector<weva_shaped_glyph> glyphs(count);
            positioned(table.user_data, face, utf8.get_data(), utf8.length(), px, glyphs.data(), count);
            double width = 0;
            for (const auto& g : glyphs) width += g.x_advance;
            matches = matches && near(width, width_of(reference, text, px));
            fractional = fractional || std::abs(width - std::round(width)) > 0.01;
        }
        check(matches, "an adopted face measures like a quarter-pixel reference at every size");
        check(fractional, "large text keeps fractional advances instead of whole-pixel snapping");
        check(ts->font_get_subpixel_positioning(borrowed) == TextServer::SUBPIXEL_POSITIONING_AUTO,
              "the game's own font resource keeps its positioning mode");
        ts->font_set_subpixel_positioning(borrowed, saved);
        ts->free_rid(reference);
    }

protected:
    static void _bind_methods() {
        ClassDB::bind_method(D_METHOD("run_checks"), &WevaFontBackendTests::run_checks);
    }

public:
    int run_checks() {
        checks_ = failures_ = 0;
        TextServer* ts = TextServerManager::get_singleton()->get_primary_interface().ptr();
        const Ref<Font> primary = ThemeDB::get_singleton()->get_fallback_font();
        check(ts && primary.is_valid(), "engine font and TextServer are available");
        if (!ts || primary.is_null()) return 1;
        const TypedArray<RID> fonts = primary->get_rids();
        // The adapter positions its fonts at quarter pixels; references must too.
        for (int64_t i = 0; i < fonts.size(); ++i)
            ts->font_set_subpixel_positioning(fonts[i], TextServer::SUBPIXEL_POSITIONING_ONE_QUARTER);
        weva_godot::GodotFontBackend backend;
        const uint64_t face = backend.adopt(fonts);
        weva_font_backend table{};
        weva_shape_glyphs_fn positioned = nullptr;
        backend.fill(&table, &positioned);
        check(face && positioned, "adapter supplies positioned and legacy shaping");
        if (!face || !positioned) return 1;
        bool saw_offset = false, saw_fallback = false, saw_byte_cluster = false;
        for (const char* raw : {"éA", "x\u0301", "q\u0323\u0301", "سَلَام", "किताब", "👩‍🚀A", "\u200bA"}) {
            const String text = String::utf8(raw);
            const CharString utf8 = text.utf8();
            for (int px : {32, 13}) {
                const RID shaped = ts->create_shaped_text();
                ts->shaped_text_add_string(shaped, text, fonts, px);
                ts->shaped_text_shape(shaped);
                const TypedArray<Dictionary> source = ts->shaped_text_get_glyphs(shaped);
                std::vector<Dictionary> expected;
                for (int64_t i = 0; i < source.size(); ++i) {
                    const Dictionary g = source[i];
                    for (int64_t r = 0; r < static_cast<int64_t>(g["repeat"]); ++r)
                        expected.push_back(g);
                }
                const size_t count = positioned(table.user_data, face, utf8.get_data(), utf8.length(), px, nullptr, 0);
                check(count == expected.size(), "sizing includes all repeated glyphs");
                std::vector<weva_shaped_glyph> out(count+1);
                out.back().glyph = 0xdeadbeef;
                const size_t got = positioned(table.user_data, face, utf8.get_data(), utf8.length(), px, out.data(), count);
                check(got == count && out.back().glyph == 0xdeadbeef, "fill respects capacity");
                weva_shaped_glyph prefix[2]{};
                prefix[1].glyph = 0xdeadbeef;
                check(positioned(table.user_data, face, utf8.get_data(), utf8.length(), px, prefix, 1) == count &&
                      prefix[1].glyph == 0xdeadbeef, "truncated fill returns complete count without overrunning");
                if (count) check(prefix[0].glyph == out[0].glyph, "glyph identity survives repeated shaping");
                std::vector<uint32_t> ids(count), clusters(count);
                std::vector<double> advances(count);
                check(table.shape(table.user_data, face, utf8.get_data(), utf8.length(), px,
                                  ids.data(), advances.data(), clusters.data(), count) == count,
                      "legacy shape callback retains its sizing protocol");
                for (size_t i = 0; i < std::min(count, expected.size()); ++i) {
                    const auto& g = expected[i];
                    const auto& actual = out[i];
                    const int64_t start = g["start"];
                    const uint32_t byte = static_cast<uint32_t>(text.substr(0, start).utf8().length());
                    const Vector2 offset = g["offset"];
                    check(actual.cluster == byte, "cluster points at the source UTF-8 byte boundary");
                    check(near(actual.x_advance, g["advance"]) && actual.y_advance == 0,
                          "horizontal advances match TextServer");
                    check(near(actual.x_offset, offset.x) && near(actual.y_offset, -offset.y),
                          "positioning preserves offsets with upward core y coordinates");
                    check(ids[i] == actual.glyph && clusters[i] == byte && advances[i] == actual.x_advance,
                          "legacy callback agrees on IDs, advances and UTF-8 clusters");
                    saw_offset |= offset != Vector2();
                    saw_byte_cluster |= byte != static_cast<uint32_t>(start);
                    const RID from = g["font_rid"];
                    const int64_t index = g["index"];
                    if (from.is_valid() && index == 0) {
                        check(actual.glyph == 0, "invisible control glyph emits no bitmap");
                        continue;
                    }
                    if (!from.is_valid()) continue; // native engine has no font for this character
                    saw_fallback |= !fonts.has(from);
                    double advance = 0, bx = 0, by = 0;
                    int32_t width = 0, height = 0;
                    check(table.glyph_metrics(table.user_data, face, actual.glyph, px,
                                              &advance, &bx, &by, &width, &height) != 0,
                          "shaped glyph resolves to a real font");
                    const Vector2i size(px, 0);
                    const Vector2 expected_offset = ts->font_get_glyph_offset(from, size, index);
                    const Vector2 expected_size = ts->font_get_glyph_size(from, size, index);
                    const Vector2 expected_advance = ts->font_get_glyph_advance(from, px, index);
                    check(near(advance, expected_advance.x) && near(bx, expected_offset.x) && near(by, -expected_offset.y) &&
                          width == static_cast<int32_t>(expected_size.x) && height == static_cast<int32_t>(expected_size.y),
                          "metrics use the exact font chosen by TextServer, including automatic fallbacks");
                    weva_glyph_bitmap bitmap{};
                    const bool rendered = table.rasterize(table.user_data, face, actual.glyph, px, &bitmap) != 0;
                    ts->font_render_glyph(from, size, index);
                    const Rect2 uv = ts->font_get_glyph_uv_rect(from, size, index);
                    check(rendered == (uv.size.x > 0 && uv.size.y > 0), "bitmap presence matches native glyph");
                    if (rendered) {
                        check(bitmap.width == static_cast<int32_t>(uv.size.x) &&
                              bitmap.height == static_cast<int32_t>(uv.size.y), "bitmap dimensions match native glyph");
                        const int64_t texture = ts->font_get_glyph_texture_idx(from, size, index);
                        const Ref<Image> image = ts->font_get_texture_image(from, size, texture);
                        bool same = image.is_valid();
                        if (same) {
                            for (int y = 0; y < bitmap.height; ++y)
                                for (int x = 0; x < bitmap.width; ++x)
                                    same &= bitmap.alpha[y*bitmap.width+x] == static_cast<uint8_t>(std::lround(
                                        image->get_pixel(static_cast<int>(uv.position.x)+x,
                                                         static_cast<int>(uv.position.y)+y).a*255));
                        }
                        check(same, "coverage pixels come from the chosen native font");
                    }
                }
                check_document(ts, table, positioned, face, utf8, expected, px);
                ts->free_rid(shaped);
            }
        }
        check(saw_offset, "fixture exercised nonzero mark placement");
        check(saw_fallback, "fixture exercised automatic system-font fallback");
        check(saw_byte_cluster, "fixture distinguished UTF-8 bytes from character indices");
        check_variants(ts, primary, fonts);
        check_shared_runs(ts, primary);
        check_engine_stack_limits(table, positioned, face);
        check_fractional_positioning(ts, primary);
        UtilityFunctions::print("godot font backend: ", checks_, " checks, ", failures_, " failures");
        return failures_ ? 1 : 0;
    }
};

void initialize_font_tests(ModuleInitializationLevel level) {
    if (level == MODULE_INITIALIZATION_LEVEL_SCENE) GDREGISTER_CLASS(WevaFontBackendTests);
}
void uninitialize_font_tests(ModuleInitializationLevel level) {
    if (level == MODULE_INITIALIZATION_LEVEL_SCENE) weva_godot::release_shared_font_variants();
}
extern "C" GDExtensionBool GDE_EXPORT weva_font_tests_init(
    GDExtensionInterfaceGetProcAddress get_proc, GDExtensionClassLibraryPtr library,
    GDExtensionInitialization* initialization) {
    GDExtensionBinding::InitObject init(get_proc, library, initialization);
    init.register_initializer(initialize_font_tests);
    init.register_terminator(uninitialize_font_tests);
    init.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);
    return init.init();
}
