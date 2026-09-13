#include "weva_node.h"

#include <godot_cpp/classes/viewport.hpp>
#include <godot_cpp/classes/input.hpp>
#include <godot_cpp/classes/input_event_joypad_button.hpp>
#include <godot_cpp/classes/input_event_joypad_motion.hpp>
#include <godot_cpp/classes/input_map.hpp>
#include <godot_cpp/classes/display_server.hpp>
#include <godot_cpp/classes/engine.hpp>
#include <godot_cpp/classes/main_loop.hpp>
#include <godot_cpp/classes/material.hpp>
#include <godot_cpp/classes/time.hpp>
#include <godot_cpp/classes/window.hpp>

#include <godot_cpp/classes/font.hpp>
#include <godot_cpp/classes/font_file.hpp>
#include <godot_cpp/classes/file_access.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/resource_loader.hpp>
#include <godot_cpp/classes/texture2d.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/theme_db.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/callable_method_pointer.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/typed_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/rect2.hpp>
#include <godot_cpp/variant/vector3.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

using namespace godot;

namespace weva_godot {

namespace {
struct GodotDrawProfile {
    bool enabled = false;
    double packing_ms = 0, submit_ms = 0;
    size_t vertices = 0, submissions = 0, max_batch_vertices = 0;
    size_t packed_vertices = 0, reused_batches = 0;
};
thread_local GodotDrawProfile draw_profile;
using DrawClock = std::chrono::steady_clock;
double draw_elapsed(DrawClock::time_point start) {
    return std::chrono::duration<double, std::milli>(DrawClock::now()-start).count();
}
struct GodotDrawScope {
    DrawClock::time_point start{};
    uint64_t frame = 0;
    GodotDrawScope() {
        static const bool enabled = std::getenv("WEVA_GODOT_DRAW_LOG") != nullptr;
        draw_profile = {};
        draw_profile.enabled = enabled;
        if (enabled) {
            frame = Engine::get_singleton()->get_process_frames();
            start = DrawClock::now();
        }
    }
    ~GodotDrawScope() {
        if (draw_profile.enabled)
            std::fprintf(stderr,"godot draw: %.3f ms; packing %.3f, submit %.3f ms; %zu vertices; %zu submissions, largest %zu vertices; frame %llu; packed %zu vertices, %zu batches reused\n",
                         draw_elapsed(start),draw_profile.packing_ms,draw_profile.submit_ms,draw_profile.vertices,
                         draw_profile.submissions,draw_profile.max_batch_vertices,
                         static_cast<unsigned long long>(frame),draw_profile.packed_vertices,draw_profile.reused_batches);
    }
};
} // namespace

WevaDocument::WevaDocument() {
    set_mouse_filter(MOUSE_FILTER_PASS);
    set_focus_mode(FOCUS_ALL);
    // The ABI is versioned so a host can refuse a library it was not built
    // against. Checking the major here rather than at first use means a
    // mismatch is a loud failure at construction, not a subtle one later.
    const uint32_t v = weva_abi_version();
    if ((v >> 16) != WEVA_ABI_VERSION_MAJOR) {
        UtilityFunctions::push_error("libweva ABI major mismatch: host expects ",
                                     WEVA_ABI_VERSION_MAJOR, ", library reports ", (v >> 16));
        return;
    }
    weva_config cfg{};
    cfg.viewport_width = 1920;
    cfg.viewport_height = 1080;
    cfg.use_user_agent_stylesheet = 1;
    doc_ = weva_document_create(&cfg);
    weva_document_set_popover_request_events(doc_, 1);
    if (dark_color_scheme_) weva_document_set_color_scheme(doc_, 1);
    if (safe_area_[0] || safe_area_[1] || safe_area_[2] || safe_area_[3]) {
        weva_document_set_safe_area_insets(doc_, safe_area_[0], safe_area_[1], safe_area_[2], safe_area_[3]);
    }
    // Godot resolves imported textures and PCK paths; the core consumes PNG
    // bytes through its existing decoder and document-scoped image cache.
    weva_document_set_asset_reader(doc_, &WevaDocument::read_asset, this);
}

namespace {
PackedByteArray image_bytes(String path) {
    if (!path.begins_with("res://") && !path.begins_with("user://") && !path.is_absolute_path())
        path = "res://" + path;

    // An exported PNG normally exists as a remapped CompressedTexture2D,
    // with no original PNG file in the pack. Resolve through the importer in
    // both editor and export so resizing/compression settings agree too.
    ResourceLoader* loader = ResourceLoader::get_singleton();
    if (path.begins_with("res://") && loader && loader->exists(path, "Texture2D")) {
        const Ref<Texture2D> texture = loader->load(path, "Texture2D");
        if (texture.is_valid()) {
            Ref<Image> image = texture->get_image();
            if (image.is_valid() && !image->is_empty()) {
                if (image->is_compressed() && image->decompress() != OK) return {};
                image->convert(Image::FORMAT_RGBA8);
                return image->save_png_to_buffer();
            }
        }
    }
    // Raw PNGs outside the importer (user://, absolute paths, Keep File)
    // retain the same core decoder path as the standalone renderer.
    Ref<FileAccess> file = FileAccess::open(path, FileAccess::READ);
    if (file.is_null()) return {};
    return file->get_buffer(file->get_length());
}
}

// Cache only across this synchronous size/read pair: imported images should
// not be loaded and PNG-encoded twice, or retained twice after decoding.
size_t WevaDocument::read_asset(void* user_data, const char* path, uint8_t* buffer,
                                size_t capacity) {
    if (!path || !user_data) return 0;
    auto* self = static_cast<WevaDocument*>(user_data);
    const String p = String::utf8(path);
    if (!buffer || capacity == 0 || self->asset_read_path_ != p) {
        self->asset_read_path_ = p;
        self->asset_read_bytes_ = image_bytes(p);
    }
    const size_t length = static_cast<size_t>(self->asset_read_bytes_.size());
    if (!buffer || capacity == 0) return length;
    if (capacity < length) return 0;
    if (length) std::memcpy(buffer, self->asset_read_bytes_.ptr(), length);
    self->asset_read_bytes_ = PackedByteArray();
    self->asset_read_path_ = String();
    return length;
}

namespace {
// One font per `@font-face` source, by the same path convention as images:
// an imported font resource when the importer knows the path, otherwise the
// raw file (user://, absolute paths, Keep File, headless runs).
Ref<Font> load_css_font(String path) {
    if (!path.begins_with("res://") && !path.begins_with("user://") && !path.is_absolute_path())
        path = "res://" + path;
    ResourceLoader* loader = ResourceLoader::get_singleton();
    if (path.begins_with("res://") && loader && loader->exists(path, "Font")) {
        const Ref<Font> font = loader->load(path, "Font");
        if (font.is_valid()) return font;
    }
    if (!FileAccess::file_exists(path)) return Ref<Font>();
    Ref<FontFile> file;
    file.instantiate();
    if (file->load_dynamic_font(path) != OK) return Ref<Font>();
    return file;
}

// The ordered source list of a @font-face line ("url:<path>|local:<name>"):
// the first entry that loads wins. A local() name is an installed font,
// found the way Godot's own SystemFont finds one.
Ref<Font> load_css_sources(const String& sources) {
    for (const String& entry : sources.split("|", false)) {
        if (entry.begins_with("url:")) {
            const Ref<Font> font = load_css_font(entry.substr(4));
            if (font.is_valid()) return font;
        } else if (entry.begins_with("local:")) {
            OS* os = OS::get_singleton();
            if (!os) continue;
            const String path = os->get_system_font_path(entry.substr(6));
            if (path.is_empty()) continue;
            const Ref<Font> font = load_css_font(path);
            if (font.is_valid()) return font;
        }
    }
    return Ref<Font>();
}

// The CSS weight a stored strength stands for (see register_font_face).
int css_weight(int strength) { return strength == 2 ? 800 : strength == 1 ? 700 : 400; }

bool normal_face(const String& weight, const String& style) {
    const String w = weight.strip_edges().to_lower();
    const String s = style.strip_edges().to_lower();
    return (w.is_empty() || w == "normal" || w == "400") && (s.is_empty() || s == "normal");
}
} // namespace

// The core lists `@font-face` rules; this loads their sources and registers
// the families so `font-family: "Camp Display"` selects them. The rule with
// normal weight and style (else the first) is the family's regular face; a
// rule for a bold weight or italic style becomes a real variant file, and
// what no file covers is synthesized. Families the game registered through
// register_font_family keep their font; the game's own faces likewise.
void WevaDocument::sync_css_font_faces() {
    if (!doc_) return;
    const size_t bytes = weva_document_font_faces(doc_, nullptr, 0);
    std::vector<char> text(bytes + 1);
    weva_document_font_faces(doc_, text.data(), text.size());
    struct Wanted {
        String family, path;
        bool normal = false;
        std::map<std::pair<int, bool>, String> variants;
    };
    std::map<String, Wanted> wanted;
    for (const String& line : String::utf8(text.data()).split("\n", false)) {
        const PackedStringArray fields = line.split("\t");
        if (fields.size() < 2 || fields[0].is_empty()) continue;
        // The ordered source list (ABI minor 37); an older core lists the
        // first url() only.
        const String sources = fields.size() > 4 && !fields[4].is_empty() ? fields[4]
                               : fields[1].is_empty() ? String() : "url:" + fields[1];
        if (sources.is_empty()) continue;
        const String key = fields[0].strip_edges().to_lower();
        if (key.is_empty() || key.contains(",") || key.contains("\"") || key.contains("'")) continue;
        const String weight = fields.size() > 2 ? fields[2].strip_edges().to_lower() : String();
        const String style = fields.size() > 3 ? fields[3].strip_edges().to_lower() : String();
        const bool normal = normal_face(weight, style);
        auto it = wanted.find(key);
        if (it == wanted.end()) it = wanted.emplace(key, Wanted{fields[0].strip_edges(), sources, normal, {}}).first;
        else if (normal && !it->second.normal) {
            it->second.path = sources;
            it->second.normal = true;
        }
        if (!normal) {
            // "bold"/"bolder" and numbers; a range keeps its first number.
            const int number = weight == "bold" || weight == "bolder" ? 700 : weight.is_empty() || weight == "normal" ? 400 : weight.to_int();
            const int strength = number >= 800 ? 2 : number >= 600 ? 1 : 0;
            const bool italic = style.begins_with("italic") || style.begins_with("oblique");
            if (strength || italic) it->second.variants.emplace(std::make_pair(strength, italic), sources);
        }
    }
    // Drop CSS registrations the stylesheet no longer declares, or that the
    // game has since replaced with its own font.
    for (auto it = css_font_faces_.begin(); it != css_font_faces_.end();) {
        const auto current = family_fonts_.find(it->first);
        const bool ours = current != family_fonts_.end() && current->second == it->second.font;
        const auto want = wanted.find(it->first);
        if (!ours) {
            it = css_font_faces_.erase(it);
            continue;
        }
        if (want == wanted.end()) {
            for (const auto& variant : it->second.variants)
                register_font_face(it->first, Ref<Font>(), css_weight(variant.first.first), variant.first.second);
            register_font_family(it->first, Ref<Font>());
            it = css_font_faces_.erase(it);
            continue;
        }
        for (auto variant = it->second.variants.begin(); variant != it->second.variants.end();) {
            const auto still = want->second.variants.find(variant->first);
            if (still != want->second.variants.end() && still->second == variant->second.first) {
                ++variant;
                continue;
            }
            register_font_face(it->first, Ref<Font>(), css_weight(variant->first.first), variant->first.second);
            variant = it->second.variants.erase(variant);
        }
        ++it;
    }
    for (const auto& entry : wanted) {
        auto existing = css_font_faces_.find(entry.first);
        if (existing == css_font_faces_.end() && family_fonts_.count(entry.first)) continue; // the game's own registration wins
        if (existing == css_font_faces_.end() || existing->second.path != entry.second.path) {
            const Ref<Font> font = load_css_sources(entry.second.path);
            if (font.is_null()) {
                UtilityFunctions::push_warning("Weva CSS: @font-face ", entry.second.family, " could not load ", entry.second.path);
                continue;
            }
            if (!register_font_family(entry.first, font)) continue;
            if (existing == css_font_faces_.end()) existing = css_font_faces_.emplace(entry.first, CssFontFace{}).first;
            existing->second.path = entry.second.path;
            existing->second.font = font;
        }
        for (const auto& variant : entry.second.variants) {
            if (existing->second.variants.count(variant.first)) continue;
            const auto owned = family_variants_.find(entry.first);
            if (owned != family_variants_.end() && owned->second.count(variant.first)) continue; // the game's own face wins
            const Ref<Font> font = load_css_sources(variant.second);
            if (font.is_null()) {
                UtilityFunctions::push_warning("Weva CSS: @font-face ", entry.second.family, " could not load ", variant.second);
                continue;
            }
            if (register_font_face(entry.first, font, css_weight(variant.first.first), variant.first.second))
                existing->second.variants[variant.first] = {variant.second, font};
        }
    }
}

void WevaDocument::set_base_path(const String& path) {
    base_path_ = path;
    if (doc_) {
        const CharString utf8 = path.utf8();
        weva_document_set_base_path(doc_, utf8.get_data());
        sync_css_font_faces();
        dirty_ = true;
        queue_redraw();
    }
}

String WevaDocument::get_base_path() const { return base_path_; }

bool WevaDocument::set_element_style(const String& selector, const String& property,
                                     const String& value) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString p = property.utf8();
    const CharString v = value.utf8();
    // An empty value REMOVES the declaration rather than setting it empty, so
    // a script can hand a property back to the stylesheet.
    if (weva_element_set_style(doc_, e, p.get_data(), value.is_empty() ? nullptr : v.get_data()) !=
        WEVA_OK) {
        return false;
    }
    dirty_ = true;
    queue_redraw();
    return true;
}

String WevaDocument::get_element_style(const String& selector, const String& property) {
    if (!doc_) return String();
    ensure_updated();
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const CharString p = property.utf8();
    const size_t n = weva_element_style(doc_, e, p.get_data(), nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_element_style(doc_, e, p.get_data(), buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

// Document coordinates through this node's own transform, so the answer is in
// the space a sibling Node2D lives in.
//
// The document's box is not it: the gallery scales its stage to fit and offsets
// it by a scroll pan, and a caller anchoring a portrait or a particle emitter
// over a panel would land it somewhere else entirely. Composing that transform
// by hand is what a script would otherwise have to do, and it is the same
// mistake this node already makes internally for hit testing.
Rect2 WevaDocument::get_element_screen_rect(const String& selector) {
    const Rect2 local = query_bounds(selector);
    if (local.size == Vector2()) return local;
    return get_global_transform().xform(local);
}

PackedStringArray WevaDocument::get_missing_assets() {
    PackedStringArray out;
    if (!doc_) return out;
    ensure_updated();
    const size_t bytes = weva_document_missing_assets(doc_, nullptr, 0);
    if (bytes == 0) return out;
    std::vector<char> names(bytes + 1, 0);
    weva_document_missing_assets(doc_, names.data(), names.size());
    for (const String& line : String::utf8(names.data()).split("\n", false)) {
        if (!line.is_empty()) out.push_back(line);
    }
    return out;
}

Array WevaDocument::get_changed_elements() const {
    Array out;
    if (!doc_) return out;
    std::vector<weva_element_change> changes(weva_document_changed_elements(doc_, nullptr, 0));
    const size_t n = weva_document_changed_elements(doc_, changes.data(), changes.size());
    for (size_t i = 0; i < n; ++i) {
        Dictionary d;
        d["element"] = static_cast<int64_t>(changes[i].element);
        d["kind"] = changes[i].kind == WEVA_CHANGE_BOXES ? "boxes" : changes[i].kind == WEVA_CHANGE_LAYOUT ? "layout" : "paint";
        out.push_back(d);
    }
    return out;
}

int64_t WevaDocument::get_structure_version() const {
    return doc_ ? static_cast<int64_t>(weva_document_structure_version(doc_)) : 0;
}

PackedStringArray WevaDocument::get_html_diagnostics() const {
    if (!doc_) return {};
    const size_t bytes = weva_document_html_diagnostics(doc_, nullptr, 0);
    if (!bytes) return {};
    std::vector<char> text(bytes + 1);
    weva_document_html_diagnostics(doc_, text.data(), text.size());
    return String::utf8(text.data()).split("\n", false);
}

PackedStringArray WevaDocument::get_css_diagnostics() const {
    if (!doc_) return {};
    const size_t bytes = weva_document_css_diagnostics(doc_, nullptr, 0);
    if (!bytes) return {};
    std::vector<char> text(bytes + 1);
    weva_document_css_diagnostics(doc_, text.data(), text.size());
    return String::utf8(text.data()).split("\n", false);
}

// The system symbol faces, loaded ONCE for the whole extension.
//
// They used to be per document, and that quietly corrupted any application
// that shows more than one. A SystemFont hands out TextServer RIDs that do not
// belong to it alone, so releasing one document's faces invalidated RIDs a
// LATER document was still drawing with: the errors start at the second
// document, and by the fourth a page came out with no text at all. Cycling the
// 35 samples produced 59,664 "font is null" errors and several empty pages;
// shared, it produces none.
//
// Loading eight system fonts per document was also simply wasteful — the faces
// are immutable and identical every time.
struct SymbolFontCache {
    std::vector<Ref<SystemFont>> fonts;
    std::vector<String> pending;
    size_t next = 0;
    bool initialized = false;
};
static SymbolFontCache& symbol_font_cache() {
    static SymbolFontCache cache;
    return cache;
}

bool WevaDocument::warmup_fonts_step() {
    SymbolFontCache& cache = symbol_font_cache();
    const bool profile = std::getenv("WEVA_STAGE_LOG") != nullptr;
    auto started = profile ? DrawClock::now() : DrawClock::time_point{};
    if (!cache.initialized) {
        cache.initialized = true;
        PackedStringArray installed;
        if (OS* os = OS::get_singleton()) installed = os->get_system_fonts();
        if (profile) std::fprintf(stderr, "godot system fonts: enumeration %.6f ms\n", draw_elapsed(started));
        // Symbols and emoji first, then CJK. The order only decides who wins when
        // two faces both have a character, and the primary theme font is ahead of
        // all of them -- so Latin never comes from a fallback.
        //
        // CJK is the same problem emoji are: a codepoint the theme font has no
        // glyph for. Without these names a Japanese paragraph lays out correctly
        // and draws NOTHING, which is what it did. Every desktop ships at least
        // one of these; a name the machine does not have costs nothing, because
        // the list is filtered against what is installed.
        for (const char* n : {"Segoe UI Symbol", "Segoe UI Emoji", "Apple Color Emoji",
                              "Noto Color Emoji", "Noto Sans Symbols2", "Noto Sans Symbols",
                              "DejaVu Sans", "Symbola",
                              // Japanese
                              "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Hiragino Sans",
                              "Noto Sans CJK JP", "Noto Sans JP",
                              // Simplified and traditional Chinese
                              "Microsoft YaHei", "Microsoft JhengHei", "PingFang SC", "PingFang TC",
                              "Noto Sans CJK SC", "Noto Sans SC", "WenQuanYi Micro Hei",
                              // Korean
                              "Malgun Gothic", "Apple SD Gothic Neo", "Noto Sans CJK KR",
                              "Noto Sans KR",
                              // The catch-all Android and some Linux images ship
                              "Droid Sans Fallback"}) {
            if (installed.size() > 0 && !installed.has(String(n))) continue;
            cache.pending.emplace_back(n);
        }
    }
    if (cache.next < cache.pending.size()) {
        const String& name = cache.pending[cache.next];
        if (profile) started = DrawClock::now();
        Ref<SystemFont> sf;
        sf.instantiate();
        PackedStringArray names;
        names.push_back(name);
        sf->set_font_names(names);
        sf->get_rids(); // Complete lazy face setup during this loading step.
        cache.fonts.push_back(sf);
        ++cache.next;
        if (profile) std::fprintf(stderr, "godot system fonts: %s %.6f ms\n", name.utf8().get_data(), draw_elapsed(started));
    }
    return cache.next == cache.pending.size();
}

const std::vector<Ref<SystemFont>>& shared_symbol_fonts() {
    // Existing callers still get the complete, ordered fallback chain even
    // when a loading screen performed only part of the optional warmup.
    while (!WevaDocument::warmup_fonts_step()) {}
    return symbol_font_cache().fonts;
}

// Dropped at module shutdown rather than by a static destructor, which would
// run after the engine has gone and free RIDs into nothing.
void release_shared_symbol_fonts() { symbol_font_cache() = SymbolFontCache{}; }

bool WevaDocument::register_font_family(const String& family, const Ref<Font>& font) {
    const String key = family.strip_edges().to_lower();
    if (key.is_empty() || key.contains(",") || key.contains("\"") || key.contains("'")) return false;
    auto it = family_fonts_.find(key);
    if (it == family_fonts_.end() && font.is_null()) return true;
    if (it != family_fonts_.end() && it->second == font) return true;
    disconnect_family_fonts();
    if (it != family_fonts_.end()) {
        // Adopted RIDs remain borrowed until the backend rebuild below.
        if (font_face_) retired_family_fonts_.push_back(it->second);
        family_fonts_.erase(it);
    }
    if (font.is_valid()) family_fonts_[key] = font;
    if (family_font_changed_.is_null())
        family_font_changed_ = callable_mp(this, &WevaDocument::family_font_resource_changed);
    for (const auto& entry : family_fonts_)
        if (!entry.second->is_connected("changed", family_font_changed_))
            entry.second->connect("changed", family_font_changed_);
    font_resource_changed();
    return true;
}

void WevaDocument::family_font_resource_changed() { font_resource_changed(); }

void WevaDocument::disconnect_family_fonts() {
    for (const auto& entry : family_fonts_)
        if (entry.second->is_connected("changed", family_font_changed_))
            entry.second->disconnect("changed", family_font_changed_);
    for (const auto& family : family_variants_)
        for (const auto& variant : family.second)
            if (variant.second->is_connected("changed", family_font_changed_))
                variant.second->disconnect("changed", family_font_changed_);
}

bool WevaDocument::register_font_face(const String& family, const Ref<Font>& font, int weight, bool italic) {
    const String key = family.strip_edges().to_lower();
    if (key.is_empty() || key.contains(",") || key.contains("\"") || key.contains("'")) return false;
    const int strength = weight >= 800 ? 2 : weight >= 600 ? 1 : 0;
    if (strength == 0 && !italic) return false; // that is the family's regular face
    auto& variants = family_variants_[key];
    const auto slot = std::make_pair(strength, italic);
    const auto it = variants.find(slot);
    if (it == variants.end() && font.is_null()) {
        if (variants.empty()) family_variants_.erase(key);
        return true;
    }
    if (it != variants.end() && it->second == font) return true;
    disconnect_family_fonts();
    if (it != variants.end()) {
        if (font_face_) retired_family_fonts_.push_back(it->second);
        variants.erase(it);
    }
    if (font.is_valid()) variants[slot] = font;
    if (variants.empty()) family_variants_.erase(key);
    if (family_font_changed_.is_null())
        family_font_changed_ = callable_mp(this, &WevaDocument::family_font_resource_changed);
    for (const auto& entry : family_fonts_)
        if (!entry.second->is_connected("changed", family_font_changed_))
            entry.second->connect("changed", family_font_changed_);
    for (const auto& entry : family_variants_)
        for (const auto& variant : entry.second)
            if (!variant.second->is_connected("changed", family_font_changed_))
                variant.second->connect("changed", family_font_changed_);
    font_resource_changed();
    return true;
}

void WevaDocument::set_use_engine_font(bool use) {
    if (use == use_engine_font_) return;
    use_engine_font_ = use;
    if (!use && doc_ && font_face_ != 0) {
        // A null table is what the ABI reads as "back to the built-in".
        weva_document_set_font_backend(doc_, nullptr, 0);
        font_face_ = 0;
        font_backend_.clear();
        retired_family_fonts_.clear();
        disconnect_theme_font();
    }
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_use_sdf_rects(bool use) {
    if (use == use_sdf_rects_) return;
    use_sdf_rects_ = use;
    queue_redraw();
}

void WevaDocument::ensure_font_backend() {
    // Deliberately lazy rather than done in the constructor: ThemeDB may not be
    // up that early, and use_engine_font has to be settable before the first
    // update. Later resource/theme changes reinstall it on the next update.
    if (!doc_ || !use_engine_font_) return;
    if (font_face_ != 0 && !theme_font_dirty_ && !font_resource_dirty_) return;
    static const bool profile = std::getenv("WEVA_STAGE_LOG") != nullptr;
    auto profile_start = profile ? DrawClock::now() : DrawClock::time_point{};
    const auto profile_lap = [&](const char* stage) {
        if (!profile) return;
        const auto done = DrawClock::now();
        std::fprintf(stderr, "godot font setup: %s %.6f ms\n", stage,
            std::chrono::duration<double, std::milli>(done - profile_start).count());
        profile_start = done;
    };

    // Resolve through Control so node overrides, inherited project themes
    // and theme type variations select the same resource as native controls.
    Ref<Font> fallback = get_theme_font("font");
    if (fallback.is_null()) {
        ThemeDB* theme = ThemeDB::get_singleton();
        if (theme) fallback = theme->get_fallback_font();
    }
    if (fallback.is_null()) return;
    theme_font_dirty_ = false;
    if (font_face_ != 0 && fallback == theme_font_ && !font_resource_dirty_) return;
    // Font caches and shares this Array. Appending our compatibility fallbacks
    // to it changes the resource seen by every native control and makes the
    // chain grow again with each document. Own the array before extending it.
    TypedArray<RID> rids = fallback->get_rids().duplicate();
    if (rids.is_empty()) return;
    // Extra faces already on the resource may change independently. Only the
    // compatibility faces appended below are private immutable inputs.
    const bool immutable_fallbacks = rids.size() == 1;
    profile_lap("theme");

    // Behind it, whatever the system has for symbols and emoji: the theme
    // font covers Latin and little else, and a sample's ★ or 🛡 would draw
    // nothing. One SystemFont PER installed name — a single SystemFont with a
    // name list resolves to the first match only, so with "Segoe UI Symbol"
    // present the emoji face after it was never reached. Names the system
    // lacks are skipped rather than left to fall back to the default face,
    // which would shadow every name behind them.
    // Keep these compatibility fallbacks only for the global default. An
    // explicit project font already defines its fallback chain; extra system
    // faces can take precedence over its bitmap fallbacks during shaping.
    ThemeDB* theme_db = ThemeDB::get_singleton();
    if (theme_db && fallback == theme_db->get_fallback_font()) {
        for (const Ref<SystemFont>& sf : shared_symbol_fonts()) {
            const TypedArray<RID> symbol_rids = sf->get_rids();
            for (int64_t i = 0; i < symbol_rids.size(); ++i) rids.push_back(symbol_rids[i]);
        }
    }

    profile_lap("compatibility-faces");
    // The theme font's file data lets the backend build bold and italic
    // variants as fonts of their own (a variation would share its glyphs).
    PackedByteArray primary_data;
    const Ref<FontFile> file = fallback;
    if (file.is_valid()) primary_data = file->get_data();
    // Release owned variants and stale borrowed RIDs before dropping the old
    // resource reference. A resource can change its RIDs without changing its
    // object identity, so its changed signal is also an input to this rebuild.
    font_backend_.clear();
    retired_family_fonts_.clear();
    disconnect_theme_font();
    theme_font_ = fallback;
    if (theme_font_changed_.is_null())
        theme_font_changed_ = callable_mp(this, &WevaDocument::font_resource_changed);
    theme_font_->connect("changed", theme_font_changed_);
    font_resource_dirty_ = false;
    font_face_ = font_backend_.adopt(rids, primary_data, immutable_fallbacks);
    if (font_face_ == 0) return;
    weva_shape_glyphs_fn shaper = nullptr;
    font_backend_.fill(&font_table_, &shaper);
    weva_document_set_font_backend(doc_, &font_table_, font_face_);
    weva_document_set_font_shaper(doc_, shaper);
    profile_lap("backend");
    for (const auto& entry : family_fonts_) {
        const TypedArray<RID> family_rids = entry.second->get_rids().duplicate();
        PackedByteArray family_data;
        const Ref<FontFile> family_file = entry.second;
        if (family_file.is_valid()) family_data = family_file->get_data();
        const auto face = font_backend_.adopt(family_rids, family_data, family_rids.size() == 1);
        if (!face) {
            UtilityFunctions::push_warning("Weva could not adopt font family: ", entry.first);
            continue;
        }
        weva_document_register_font_family(doc_, entry.first.utf8().get_data(), face);
        const auto variants = family_variants_.find(entry.first);
        if (variants == family_variants_.end()) continue;
        for (const auto& variant : variants->second) {
            const TypedArray<RID> variant_rids = variant.second->get_rids().duplicate();
            PackedByteArray variant_data;
            const Ref<FontFile> variant_file = variant.second;
            if (variant_file.is_valid()) variant_data = variant_file->get_data();
            const auto variant_face = font_backend_.adopt(variant_rids, variant_data, variant_rids.size() == 1);
            if (!variant_face) {
                UtilityFunctions::push_warning("Weva could not adopt a font face for family: ", entry.first);
                continue;
            }
            font_backend_.set_real_variant(face, css_weight(variant.first.first), variant.first.second, variant_face);
        }
    }
    profile_lap("families");
    dirty_ = true;
}

void WevaDocument::font_resource_changed() {
    font_resource_dirty_ = true;
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::disconnect_theme_font() {
    if (theme_font_.is_valid()) {
        if (theme_font_->is_connected("changed", theme_font_changed_))
            theme_font_->disconnect("changed", theme_font_changed_);
        theme_font_.unref();
    }
}

WevaDocument::~WevaDocument() {
    disconnect_family_fonts();
    disconnect_theme_font();
    close_ime();
    if (doc_) weva_document_destroy(doc_);
    release_layers();
    release_retained_batches();
    RenderingServer* rs_ = RenderingServer::get_singleton();
    if (backdrop_shader_.is_valid()) rs_->free_rid(backdrop_shader_);
    for (const RID& r : rounded_materials_) {
        if (r.is_valid()) rs_->free_rid(r);
    }
    if (rounded_shader_.is_valid()) rs_->free_rid(rounded_shader_);
}

RID WevaDocument::blend_item(int32_t blend_mode) {
    CanvasItemMaterial::BlendMode godot_mode = CanvasItemMaterial::BLEND_MODE_MIX;
    switch (blend_mode) {
        case WEVA_BLEND_MULTIPLY: godot_mode = CanvasItemMaterial::BLEND_MODE_MUL; break;
        case WEVA_BLEND_SCREEN: case WEVA_BLEND_LIGHTEN: case WEVA_BLEND_COLOR_DODGE:
            godot_mode = CanvasItemMaterial::BLEND_MODE_ADD; break;
        default: break;
    }
    Ref<CanvasItemMaterial>& material = blend_materials_[static_cast<int>(godot_mode)];
    if (material.is_null()) {
        material.instantiate();
        material->set_blend_mode(godot_mode);
    }
    RenderingServer* rs = RenderingServer::get_singleton();
    const RID item = rs->canvas_item_create();
    rs->canvas_item_set_parent(item, get_canvas_item());
    rs->canvas_item_set_material(item, material->get_rid());
    rs->canvas_item_set_draw_index(item, static_cast<int32_t>(layer_items_.size()));
    layer_items_.push_back(item);
    return item;
}

void WevaDocument::release_layers() {
    RenderingServer* rs = RenderingServer::get_singleton();
    for (const RID& r : layer_items_) {
        if (r.is_valid()) rs->free_rid(r);
    }
    layer_items_.clear();
    for (const RID& r : layer_materials_) {
        if (r.is_valid()) rs->free_rid(r);
    }
    layer_materials_.clear();
}

void WevaDocument::_bind_methods() {
    ClassDB::bind_static_method("WevaDocument", D_METHOD("warmup_fonts_step"), &WevaDocument::warmup_fonts_step);
    ClassDB::bind_method(D_METHOD("set_html", "html"), &WevaDocument::set_html);
    ClassDB::bind_method(D_METHOD("get_html"), &WevaDocument::get_html);
    ClassDB::bind_method(D_METHOD("set_css", "css"), &WevaDocument::set_css);
    ClassDB::bind_method(D_METHOD("get_css"), &WevaDocument::get_css);
    ClassDB::bind_method(D_METHOD("set_document_size", "size"), &WevaDocument::set_document_size);
    ClassDB::bind_method(D_METHOD("get_document_size"), &WevaDocument::get_document_size);
    ClassDB::bind_method(D_METHOD("update_document", "dt"), &WevaDocument::update_document,
                         DEFVAL(0.0));
    ClassDB::bind_method(D_METHOD("get_content_size"), &WevaDocument::get_content_size);
    ClassDB::bind_method(D_METHOD("query_bounds", "selector"), &WevaDocument::query_bounds);
    ClassDB::bind_method(D_METHOD("query_text", "selector"), &WevaDocument::query_text);
    ClassDB::bind_method(D_METHOD("show_popover", "selector"), &WevaDocument::show_popover);
    ClassDB::bind_method(D_METHOD("hide_popover", "selector"), &WevaDocument::hide_popover);
    ClassDB::bind_method(D_METHOD("toggle_popover", "selector"), &WevaDocument::toggle_popover);
    ClassDB::bind_method(D_METHOD("show_dialog", "selector"), &WevaDocument::show_dialog);
    ClassDB::bind_method(D_METHOD("show_modal_dialog", "selector"),
                         &WevaDocument::show_modal_dialog);
    ClassDB::bind_method(D_METHOD("close_dialog", "selector", "result"), &WevaDocument::close_dialog, DEFVAL(Variant()));
    ClassDB::bind_method(D_METHOD("request_close_dialog", "selector", "result"), &WevaDocument::request_close_dialog, DEFVAL(Variant()));
    ClassDB::bind_method(D_METHOD("prevent_default"), &WevaDocument::prevent_default);
    ClassDB::bind_method(D_METHOD("get_dialog_return_value", "selector"), &WevaDocument::get_dialog_return_value);
    ClassDB::bind_method(D_METHOD("set_custom_validity", "selector", "message"), &WevaDocument::set_custom_validity);
    ClassDB::bind_method(D_METHOD("get_custom_validity", "selector"), &WevaDocument::get_custom_validity);
    ClassDB::bind_method(D_METHOD("get_element_validity", "selector"), &WevaDocument::get_element_validity);
    ClassDB::bind_method(D_METHOD("check_validity", "selector"), &WevaDocument::check_validity);
    ClassDB::bind_method(D_METHOD("report_validity", "selector"), &WevaDocument::report_validity);
    ClassDB::bind_method(D_METHOD("set_dialog_return_value", "selector", "value"), &WevaDocument::set_dialog_return_value);
    ClassDB::bind_method(D_METHOD("has_element_attribute", "selector", "name"),
                         &WevaDocument::has_element_attribute);
    ClassDB::bind_method(D_METHOD("set_element_attribute", "selector", "name", "value"),
                         &WevaDocument::set_element_attribute);
    ClassDB::bind_method(D_METHOD("remove_element_attribute", "selector", "name"),
                         &WevaDocument::remove_element_attribute);
    ClassDB::bind_method(D_METHOD("register_font_family", "family", "font"), &WevaDocument::register_font_family);
    ClassDB::bind_method(D_METHOD("register_font_face", "family", "font", "weight", "italic"), &WevaDocument::register_font_face);
    ClassDB::bind_method(D_METHOD("set_use_engine_font", "use"),
                         &WevaDocument::set_use_engine_font);
    ClassDB::bind_method(D_METHOD("get_use_engine_font"), &WevaDocument::get_use_engine_font);
    ClassDB::bind_method(D_METHOD("set_use_sdf_rects", "use"), &WevaDocument::set_use_sdf_rects);
    ClassDB::bind_method(D_METHOD("get_use_sdf_rects"), &WevaDocument::get_use_sdf_rects);
    ClassDB::bind_method(D_METHOD("has_engine_font"), &WevaDocument::has_engine_font);
    ClassDB::bind_method(D_METHOD("set_interactive", "on"), &WevaDocument::set_interactive);
    ClassDB::bind_method(D_METHOD("get_interactive"), &WevaDocument::get_interactive);
    ClassDB::bind_method(D_METHOD("element_id_at", "point"), &WevaDocument::element_id_at);
    ClassDB::bind_method(D_METHOD("set_focus", "selector"), &WevaDocument::set_focus);
    ClassDB::bind_method(D_METHOD("get_focused_id"), &WevaDocument::get_focused_id);
    ClassDB::bind_method(D_METHOD("get_focused_row"), &WevaDocument::get_focused_row);
    ClassDB::bind_method(D_METHOD("get_row", "selector"), &WevaDocument::get_row);
    ClassDB::bind_method(D_METHOD("set_tooltip_delay", "seconds"),
                         &WevaDocument::set_tooltip_delay);
    ClassDB::bind_method(D_METHOD("get_tooltip_delay"), &WevaDocument::get_tooltip_delay);
    ADD_PROPERTY(PropertyInfo(Variant::FLOAT, "tooltip_delay"), "set_tooltip_delay",
                 "get_tooltip_delay");
    ClassDB::bind_method(D_METHOD("set_dark_color_scheme", "dark"),
                         &WevaDocument::set_dark_color_scheme);
    ClassDB::bind_method(D_METHOD("get_dark_color_scheme"), &WevaDocument::get_dark_color_scheme);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "dark_color_scheme"), "set_dark_color_scheme",
                 "get_dark_color_scheme");
    ClassDB::bind_method(D_METHOD("set_safe_area_insets", "top", "right", "bottom", "left"),
                         &WevaDocument::set_safe_area_insets);
    ClassDB::bind_method(D_METHOD("get_safe_area_insets"), &WevaDocument::get_safe_area_insets);
    ClassDB::bind_method(D_METHOD("set_follow_display_safe_area", "follow"),
                         &WevaDocument::set_follow_display_safe_area);
    ClassDB::bind_method(D_METHOD("get_follow_display_safe_area"),
                         &WevaDocument::get_follow_display_safe_area);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "follow_display_safe_area"), "set_follow_display_safe_area",
                 "get_follow_display_safe_area");
    ClassDB::bind_method(D_METHOD("get_cursor"), &WevaDocument::get_cursor);
    ClassDB::bind_method(D_METHOD("get_stats"), &WevaDocument::get_stats);
    ClassDB::bind_method(D_METHOD("get_box_tree"), &WevaDocument::get_box_tree);
    ClassDB::bind_method(D_METHOD("reload_html", "html"), &WevaDocument::reload_html);
    ClassDB::bind_method(D_METHOD("set_follow_css_cursor", "follow"),
                         &WevaDocument::set_follow_css_cursor);
    ClassDB::bind_method(D_METHOD("get_follow_css_cursor"), &WevaDocument::get_follow_css_cursor);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "follow_css_cursor"), "set_follow_css_cursor",
                 "get_follow_css_cursor");
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "interactive"), "set_interactive", "get_interactive");
    ClassDB::bind_method(D_METHOD("set_gamepad_navigation", "on"), &WevaDocument::set_gamepad_navigation);
    ClassDB::bind_method(D_METHOD("get_gamepad_navigation"), &WevaDocument::get_gamepad_navigation);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "gamepad_navigation"), "set_gamepad_navigation", "get_gamepad_navigation");
    ClassDB::bind_method(D_METHOD("set_gamepad_wake", "on"), &WevaDocument::set_gamepad_wake);
    ClassDB::bind_method(D_METHOD("get_gamepad_wake"), &WevaDocument::get_gamepad_wake);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "gamepad_wake"), "set_gamepad_wake", "get_gamepad_wake");
    ClassDB::bind_method(D_METHOD("set_gamepad_text_entry", "on"), &WevaDocument::set_gamepad_text_entry);
    ClassDB::bind_method(D_METHOD("get_gamepad_text_entry"), &WevaDocument::get_gamepad_text_entry);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "gamepad_text_entry"), "set_gamepad_text_entry", "get_gamepad_text_entry");
    ClassDB::bind_method(D_METHOD("set_retain_html_focus", "on"), &WevaDocument::set_retain_html_focus);
    ClassDB::bind_method(D_METHOD("get_retain_html_focus"), &WevaDocument::get_retain_html_focus);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "retain_html_focus"), "set_retain_html_focus", "get_retain_html_focus");
    ClassDB::bind_method(D_METHOD("set_paused", "on"), &WevaDocument::set_paused);
    ClassDB::bind_method(D_METHOD("get_paused"), &WevaDocument::get_paused);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "paused"), "set_paused", "get_paused");

    ClassDB::bind_method(D_METHOD("set_element_text", "selector", "text"),
                         &WevaDocument::set_element_text);
    ClassDB::bind_method(D_METHOD("get_element_text", "selector"),
                         &WevaDocument::get_element_text);
    ClassDB::bind_method(D_METHOD("add_element_class", "selector", "name"),
                         &WevaDocument::add_element_class);
    ClassDB::bind_method(D_METHOD("remove_element_class", "selector", "name"),
                         &WevaDocument::remove_element_class);
    ClassDB::bind_method(D_METHOD("toggle_element_class", "selector", "name", "on"),
                         &WevaDocument::toggle_element_class);
    ClassDB::bind_method(D_METHOD("has_element", "selector"), &WevaDocument::has_element);
    ClassDB::bind_method(D_METHOD("get_element_value", "selector"),
                         &WevaDocument::get_element_value);
    ClassDB::bind_method(D_METHOD("set_element_value", "selector", "value"),
                         &WevaDocument::set_element_value);
    ClassDB::bind_method(D_METHOD("reset_form", "selector"), &WevaDocument::reset_form);
    ClassDB::bind_method(D_METHOD("set_pointer", "point", "buttons", "modifiers"), &WevaDocument::set_pointer, DEFVAL(0));
    ClassDB::bind_method(D_METHOD("clear_pointer"), &WevaDocument::clear_pointer);
    ClassDB::bind_method(D_METHOD("scroll_at", "point", "delta"), &WevaDocument::scroll_at);
    ClassDB::bind_method(D_METHOD("scroll_element", "selector", "delta"),
                         &WevaDocument::scroll_element);
    ClassDB::bind_method(D_METHOD("set_element_scroll", "selector", "offset"),
                         &WevaDocument::set_element_scroll);
    ClassDB::bind_method(D_METHOD("get_element_scroll", "selector"),
                         &WevaDocument::get_element_scroll);
    ClassDB::bind_method(D_METHOD("get_element_scroll_max", "selector"),
                         &WevaDocument::get_element_scroll_max);
    ClassDB::bind_method(D_METHOD("scroll_into_view", "selector"),
                         &WevaDocument::scroll_into_view);
    ClassDB::bind_method(D_METHOD("set_element_html", "selector", "html"),
                         &WevaDocument::set_element_html);
    ClassDB::bind_method(D_METHOD("append_html", "selector", "html"),
                         &WevaDocument::append_html);
    ClassDB::bind_method(D_METHOD("remove_element", "selector"), &WevaDocument::remove_element);
    ClassDB::bind_method(D_METHOD("count_elements", "selector"), &WevaDocument::count_elements);
    ClassDB::bind_method(D_METHOD("set_base_path", "path"), &WevaDocument::set_base_path);
    ClassDB::bind_method(D_METHOD("get_base_path"), &WevaDocument::get_base_path);
    ClassDB::bind_method(D_METHOD("get_missing_assets"), &WevaDocument::get_missing_assets);
    ClassDB::bind_method(D_METHOD("get_css_diagnostics"), &WevaDocument::get_css_diagnostics);
    ClassDB::bind_method(D_METHOD("get_html_diagnostics"), &WevaDocument::get_html_diagnostics);
    ClassDB::bind_method(D_METHOD("get_changed_elements"), &WevaDocument::get_changed_elements);
    ClassDB::bind_method(D_METHOD("get_structure_version"), &WevaDocument::get_structure_version);
    ClassDB::bind_method(D_METHOD("set_element_style", "selector", "property", "value"),
                         &WevaDocument::set_element_style);
    ClassDB::bind_method(D_METHOD("get_element_style", "selector", "property"),
                         &WevaDocument::get_element_style);
    ClassDB::bind_method(D_METHOD("get_element_screen_rect", "selector"),
                         &WevaDocument::get_element_screen_rect);
    ADD_PROPERTY(PropertyInfo(Variant::STRING, "base_path"), "set_base_path", "get_base_path");
    ClassDB::bind_method(D_METHOD("get_computed_style", "selector", "property"),
                         &WevaDocument::get_computed_style);
    ClassDB::bind_method(D_METHOD("query_all_text", "selector"), &WevaDocument::query_all_text);
    ClassDB::bind_method(D_METHOD("query_all_bounds", "selector"),
                         &WevaDocument::query_all_bounds);
    ClassDB::bind_method(D_METHOD("query_all_ids", "selector"), &WevaDocument::query_all_ids);
    ClassDB::bind_method(D_METHOD("get_element_attribute", "selector", "name"),
                         &WevaDocument::get_element_attribute);
    ClassDB::bind_method(D_METHOD("set_data", "data"), &WevaDocument::set_data);
    ClassDB::bind_method(D_METHOD("get_data"), &WevaDocument::get_data);
    ClassDB::bind_method(D_METHOD("set_data_source", "resolver"),
                         &WevaDocument::set_data_source);
    ClassDB::bind_method(D_METHOD("refresh_bindings"), &WevaDocument::refresh_bindings);
    ClassDB::bind_method(D_METHOD("set_controller", "controller"),
                         &WevaDocument::set_controller);
    ClassDB::bind_method(D_METHOD("get_controller"), &WevaDocument::get_controller);
    // A property like every other configurable thing on the node. It was
    // bound as a method pair only, so `doc.controller = self` -- which is how
    // `html`, `css`, `data` and `base_path` are all set -- failed outright.
    ADD_PROPERTY(PropertyInfo(Variant::OBJECT, "controller", PROPERTY_HINT_NONE, "",
                              PROPERTY_USAGE_NONE),
                 "set_controller", "get_controller");
    ADD_PROPERTY(PropertyInfo(Variant::DICTIONARY, "data"), "set_data", "get_data");
    ClassDB::bind_method(D_METHOD("send_key", "keycode", "pressed", "shift", "ctrl"),
                         &WevaDocument::send_key, DEFVAL(true), DEFVAL(false), DEFVAL(false));
    ClassDB::bind_method(D_METHOD("send_text", "text"), &WevaDocument::send_text);
    ClassDB::bind_method(D_METHOD("paste_text", "text"), &WevaDocument::paste_text);
    ClassDB::bind_method(D_METHOD("set_composition", "text", "start", "end"), &WevaDocument::set_composition);
    ClassDB::bind_method(D_METHOD("commit_composition", "text"), &WevaDocument::commit_composition);
    ClassDB::bind_method(D_METHOD("finish_composition"), &WevaDocument::finish_composition);
    ClassDB::bind_method(D_METHOD("has_composition"), &WevaDocument::has_composition);
    ClassDB::bind_method(D_METHOD("get_caret_bounds"), &WevaDocument::get_caret_bounds);
    ClassDB::bind_method(D_METHOD("get_caret_window_bounds"), &WevaDocument::get_caret_window_bounds);
    for (const char* signal : {"composition_started", "composition_updated", "composition_ended"})
        ADD_SIGNAL(MethodInfo(signal, PropertyInfo(Variant::STRING, "id"), PropertyInfo(Variant::STRING, "text")));
    ClassDB::bind_method(D_METHOD("select_all"), &WevaDocument::select_all);
    ClassDB::bind_method(D_METHOD("undo"), &WevaDocument::undo);
    ClassDB::bind_method(D_METHOD("redo"), &WevaDocument::redo);
    ClassDB::bind_method(D_METHOD("select_word_at", "point"), &WevaDocument::select_word_at);
    ClassDB::bind_method(D_METHOD("open_select", "selector"), &WevaDocument::open_select);
    ClassDB::bind_method(D_METHOD("close_select"), &WevaDocument::close_select);
    ClassDB::bind_method(D_METHOD("get_open_select"), &WevaDocument::get_open_select);
    ClassDB::bind_method(D_METHOD("get_selected_text"), &WevaDocument::get_selected_text);
    ClassDB::bind_method(D_METHOD("set_element_selection", "selector", "start", "end"),
                         &WevaDocument::set_element_selection);
    ClassDB::bind_method(D_METHOD("get_element_selection", "selector"),
                         &WevaDocument::get_element_selection);
    ClassDB::bind_method(D_METHOD("set_element_selection_without_focus", "selector", "start", "end"),
                         &WevaDocument::set_element_selection_without_focus);

    // The element is named by its `id`, because that is the handle a script
    // and a stylesheet already share. An element with no id reports an empty
    // string, which a script can still compare against.
    ADD_SIGNAL(MethodInfo("handler_invoked", PropertyInfo(Variant::STRING, "handler"),
                          PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_clicked", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("text_entry_requested", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_pressed", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_released", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_entered", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_exited", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_focused", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_blurred", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("value_committed", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::STRING, "value")));
    ADD_SIGNAL(MethodInfo("form_submitted", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("form_reset", PropertyInfo(Variant::STRING, "id")));
    // A `data-model` control wrote its value back into `data`. The path, not
    // the element, because the path is what a script keyed its own state on.
    ADD_SIGNAL(MethodInfo("data_changed", PropertyInfo(Variant::STRING, "path"),
                          PropertyInfo(Variant::STRING, "value")));
    ADD_SIGNAL(MethodInfo("dialog_cancel_requested", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("dialog_closed", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_invalid", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_before_toggled", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::BOOL, "open")));
    ADD_SIGNAL(MethodInfo("element_toggled", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::BOOL, "open")));
    ADD_SIGNAL(MethodInfo("context_menu_requested", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::VECTOR2, "position")));
    ADD_SIGNAL(MethodInfo("row_activated", PropertyInfo(Variant::STRING, "handler"),
                          PropertyInfo(Variant::INT, "index"),
                          PropertyInfo(Variant::STRING, "key")));
    ADD_SIGNAL(MethodInfo("element_scrolled", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::FLOAT, "x"), PropertyInfo(Variant::FLOAT, "y")));
    ADD_SIGNAL(MethodInfo("value_changed", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::STRING, "value")));
    ADD_SIGNAL(MethodInfo("key_pressed", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::INT, "key"),
                          PropertyInfo(Variant::INT, "modifiers")));
    ADD_SIGNAL(MethodInfo("text_entered", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::STRING, "text")));
    ClassDB::bind_method(D_METHOD("focus_next", "backwards"), &WevaDocument::focus_next);
    ClassDB::bind_method(D_METHOD("focus_move", "direction"), &WevaDocument::focus_move);
    ClassDB::bind_method(D_METHOD("get_draw_count"), &WevaDocument::get_draw_count);
    ClassDB::bind_method(D_METHOD("get_last_update_ms"), &WevaDocument::get_last_update_ms);
    ClassDB::bind_method(D_METHOD("get_total_core_update_ms"), &WevaDocument::get_total_core_update_ms);
    ClassDB::bind_method(D_METHOD("get_core_update_count"), &WevaDocument::get_core_update_count);
    ClassDB::bind_method(D_METHOD("get_triangle_count"), &WevaDocument::get_triangle_count);

    // Multiline so the editor gives a usable box for markup rather than a
    // single-line field.
    ADD_PROPERTY(PropertyInfo(Variant::STRING, "html", PROPERTY_HINT_MULTILINE_TEXT),
                 "set_html", "get_html");
    ADD_PROPERTY(PropertyInfo(Variant::STRING, "css", PROPERTY_HINT_MULTILINE_TEXT), "set_css",
                 "get_css");
    ADD_PROPERTY(PropertyInfo(Variant::VECTOR2, "document_size"), "set_document_size",
                 "get_document_size");
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "use_engine_font"), "set_use_engine_font",
                 "get_use_engine_font");
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "use_sdf_rects"), "set_use_sdf_rects",
                 "get_use_sdf_rects");
}

void WevaDocument::_ready() {

    // The glyph atlas is shelf-packed with no gutter, and the core emits UVs
    // that address texels exactly. Godot's canvas default is linear filtering,
    // which both softens glyph edges the core drew crisply and samples across
    // shelf boundaries into whatever glyph was packed next door.
    set_texture_filter(TEXTURE_FILTER_NEAREST);

    // Native anchors and Containers own the rectangle. An unsized document
    // fills its parent; an explicitly sized one keeps its chosen rectangle.
    if (get_size() == Vector2()) set_anchors_and_offsets_preset(PRESET_FULL_RECT);
    sync_control_size();
    ensure_updated();
    sync_gui_focus();
    set_process_input(true);
}

void WevaDocument::set_html(const String& html) {
    close_ime();
    binding_paths_.clear();
    html_ = html;
    if (!doc_) return;
    const CharString utf8 = html.utf8();
    weva_document_load_html(doc_, utf8.get_data(), static_cast<size_t>(utf8.length()));
    // Reload creates new controls and repeat rows. Existing data must fill
    // their models just as it does when data is assigned after the markup.
    if (bindings_active_) refresh_bindings();
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::reload_html(const String& html) {
    html_ = html;
    if (!doc_) return;
    const CharString utf8 = html.utf8();
    weva_document_reload_html(doc_, utf8.get_data(), static_cast<size_t>(utf8.length()));
    if (bindings_active_) refresh_bindings();
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_css(const String& css) {
    if (css_ == css) return;
    if (doc_) {
        const CharString utf8 = css.utf8();
        const weva_status status = weva_document_set_css(doc_, utf8.get_data(), static_cast<size_t>(utf8.length()));
        if (status != WEVA_OK) {
            UtilityFunctions::push_error("WevaDocument could not replace CSS (status ", status, ").");
            return;
        }
    }
    css_ = css;
    sync_css_font_faces();
    for (const String& diagnostic : get_css_diagnostics()) {
        UtilityFunctions::push_warning("Weva CSS: ", diagnostic);
    }
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_document_size(const Vector2& size) {
    set_size(size);
    sync_control_size();
}

void WevaDocument::sync_control_size() {
    const Vector2 size = get_size();
    if (size == size_) return;
    size_ = size;
    if (!doc_) return;
    weva_document_set_viewport(doc_, static_cast<int>(size.x), static_cast<int>(size.y));
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_interactive(bool on) {
    interactive_ = on;
    set_mouse_filter(on ? MOUSE_FILTER_PASS : MOUSE_FILTER_IGNORE);
    set_focus_mode(on ? FOCUS_ALL : FOCUS_NONE);
    if (!on && doc_) {
        clear_pointer();
    }
}

void WevaDocument::sync_gui_focus() {
    if (doc_ && interactive_ && is_inside_tree() && is_visible_in_tree() &&
        get_focus_mode_with_override() != FOCUS_NONE && weva_document_focus(doc_) != WEVA_ELEMENT_NONE &&
        !has_focus()) grab_focus();
}

void WevaDocument::close_ime() {
    const int32_t window = ime_window_;
    ime_window_ = -1;
    ime_target_ = WEVA_ELEMENT_NONE;
    ime_end_pending_ = false;
    ime_end_target_ = WEVA_ELEMENT_NONE;
    ime_commit_text_ = String();
    if (window >= 0 && DisplayServer::get_singleton())
        DisplayServer::get_singleton()->window_set_ime_active(false, window);
}

Rect2 WevaDocument::get_caret_bounds() {
    if (!doc_) return Rect2();
    ensure_updated();
    if (weva_document_text_input_target(doc_) == WEVA_ELEMENT_NONE) return Rect2();
    const uint64_t serial = weva_document_draw_serial(doc_);
    if (serial != caret_bounds_serial_) {
        double x = 0, y = 0, width = 0, height = 0;
        weva_document_caret_bounds(doc_, &x, &y, &width, &height);
        caret_bounds_ = Rect2(x, y, width, height);
        caret_bounds_serial_ = serial;
    }
    return caret_bounds_;
}

Rect2 WevaDocument::get_caret_window_bounds() {
    if (!is_inside_tree()) return Rect2();
    const Rect2 bounds = get_caret_bounds();
    if (!bounds.has_area()) return Rect2();
    return (get_viewport()->get_screen_transform() * get_global_transform_with_canvas()).xform(bounds);
}

void WevaDocument::sync_ime() {
    static const bool profile = std::getenv("WEVA_GODOT_IME_PROFILE") != nullptr;
    const auto begin = profile ? DrawClock::now() : DrawClock::time_point{};
    auto* display = DisplayServer::get_singleton();
    if (!display || !display->has_feature(DisplayServer::FEATURE_IME)) return;
    Window* window = is_inside_tree() ? get_window() : nullptr;
    const int32_t id = window ? window->get_window_id() : -1;
    uint32_t target = WEVA_ELEMENT_NONE;
    if (doc_ && interactive_ && is_visible_in_tree() && has_focus() && id >= 0 && display->window_is_focused(id) &&
        weva_document_text_input_candidate(doc_) != WEVA_ELEMENT_NONE) {
        // Button hover/press/focus does not need an IME anchor. Keep its paint
        // pending; text controls still flush and recheck CSS/editability below.
        ensure_updated();
        target = weva_document_text_input_target(doc_);
    }
    if (target == WEVA_ELEMENT_NONE) {
        if (ime_window_ >= 0 && weva_document_commit_composition(doc_, nullptr)) {
            dirty_ = true;
            queue_redraw();
        }
        close_ime();
        return;
    }
    const double update_ms = profile ? draw_elapsed(begin) : 0;
    const auto close_start = profile ? DrawClock::now() : DrawClock::time_point{};
    const bool opening = ime_window_ != id || ime_target_ != target;
    if (opening) {
        close_ime();
        ime_window_ = id;
        ime_target_ = target;
    }
    const double close_ms = profile ? draw_elapsed(close_start) : 0;
    const auto caret_start = profile ? DrawClock::now() : DrawClock::time_point{};
    const Rect2 caret = get_caret_window_bounds();
    const Vector2i position = Vector2i(caret.position + Vector2(0, caret.size.y));
    const uint64_t serial = weva_document_draw_serial(doc_);
    const double caret_ms = profile ? draw_elapsed(caret_start) : 0;
#ifdef _WIN32
    const auto active_start = profile ? DrawClock::now() : DrawClock::time_point{};
    // Windows retains the associated IME context until focus/window teardown,
    // where close_ime() already deactivates it. Reassociating on every caret
    // repaint pays an OS transition on ordinary typing and unrelated HUD edits.
    if (opening) display->window_set_ime_active(true, id);
    const double active_ms = profile ? draw_elapsed(active_start) : 0;
    const auto position_start = profile ? DrawClock::now() : DrawClock::time_point{};
    if (opening || position != ime_position_) display->window_set_ime_position(position, id);
    if (profile && opening)
        std::fprintf(stderr, "godot ime opening: update %.6f close %.6f caret %.6f active %.6f position %.6f ms\n",
            update_ms, close_ms, caret_ms, active_ms, draw_elapsed(position_start));
    ime_position_ = position;
    ime_draw_serial_ = serial;
#else
    if (opening || position != ime_position_ || serial != ime_draw_serial_) {
        // X11 may transfer keyboard focus back from its IME child window
        // without changing the HTML target. Refresh on a painted caret,
        // as Godot's LineEdit does, including after a window activation.
        display->window_set_ime_active(true, id);
        ime_draw_serial_ = serial;
        ime_position_ = position;
        display->window_set_ime_position(position, id);
    }
#endif
}

bool WevaDocument::set_composition(const String& text, int start, int end) {
    if (!doc_) return false;
    ensure_updated();
    const CharString utf8 = text.utf8();
    const int from = text.substr(0, std::clamp(start, 0, static_cast<int>(text.length()))).utf8().length();
    const int to = text.substr(0, std::clamp(end, 0, static_cast<int>(text.length()))).utf8().length();
    if (!weva_document_set_composition(doc_, utf8.get_data(), from, to)) return false;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

bool WevaDocument::commit_composition(const String& text) {
    if (!doc_) return false;
    const CharString utf8 = text.utf8();
    if (!weva_document_commit_composition(doc_, utf8.get_data())) return false;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

bool WevaDocument::finish_composition() {
    if (!doc_ || !weva_document_commit_composition(doc_, nullptr)) return false;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

bool WevaDocument::has_composition() const {
    return doc_ && weva_document_composition(doc_, nullptr, nullptr) != WEVA_ELEMENT_NONE;
}

void WevaDocument::schedule_ime_end() {
    if (ime_end_pending_) return;
    ime_end_pending_ = true;
    ime_end_target_ = weva_document_composition(doc_, nullptr, nullptr);
    callable_mp(this, &WevaDocument::flush_ime_commit).call_deferred();
}

void WevaDocument::flush_ime_commit() {
    if (!ime_end_pending_) return;
    ime_end_pending_ = false;
    const String text = ime_commit_text_;
    ime_commit_text_ = String();
    const uint32_t target = ime_end_target_;
    ime_end_target_ = WEVA_ELEMENT_NONE;
    if (doc_ && target != WEVA_ELEMENT_NONE && weva_document_composition(doc_, nullptr, nullptr) == target)
        commit_composition(text);
}

void WevaDocument::receive_ime_update() {
    if (ime_window_ < 0 || !has_focus() || !interactive_ || !is_visible_in_tree()) return;
    auto* display = DisplayServer::get_singleton();
    const String text = display->ime_get_text();
    if (text.is_empty()) {
        if (has_composition()) schedule_ime_end();
    } else {
        flush_ime_commit();
        const Vector2i selection = display->ime_get_selection();
        if (!set_composition(text, selection.x, selection.x + selection.y)) close_ime();
    }
}

// Pointer position in the document's own coordinates, which are the node's
// local ones: the document is laid out from this node's origin.
// Godot's key codes, translated to the handful the engine has meaning for.
// Everything else travels as WEVA_KEY_OTHER and reaches a script unchanged --
// the engine has no business deciding what `J` means in someone's game.
static int weva_key_from_godot(Key code) {
    switch (code) {
        case KEY_TAB: return WEVA_KEY_TAB;
        case KEY_ENTER:
        case KEY_KP_ENTER: return WEVA_KEY_ENTER;
        case KEY_SPACE: return WEVA_KEY_SPACE;
        case KEY_ESCAPE: return WEVA_KEY_ESCAPE;
        case KEY_BACKSPACE: return WEVA_KEY_BACKSPACE;
        case KEY_DELETE: return WEVA_KEY_DELETE;
        case KEY_LEFT: return WEVA_KEY_LEFT;
        case KEY_RIGHT: return WEVA_KEY_RIGHT;
        case KEY_UP: return WEVA_KEY_UP;
        case KEY_DOWN: return WEVA_KEY_DOWN;
        case KEY_HOME: return WEVA_KEY_HOME;
        case KEY_END: return WEVA_KEY_END;
        case KEY_PAGEUP: return WEVA_KEY_PAGE_UP;
        case KEY_PAGEDOWN: return WEVA_KEY_PAGE_DOWN;
        default: return WEVA_KEY_OTHER;
    }
}

bool WevaDocument::_has_point(const Vector2& point) const {
    if (!interactive_ || !doc_) return false;
    const_cast<WevaDocument*>(this)->ensure_updated(0, 0, true);
    return weva_document_accepts_pointer(doc_, point.x, point.y) != 0;
}

void WevaDocument::dismiss_outside_transients() {
    pointer_focus_entry_ = false;
    const uint64_t version = outside_dismiss_version_;
    outside_dismiss_version_ = 0;
    if (doc_ && weva_document_dismiss_transients(doc_, version)) {
        dirty_ = true;
        queue_redraw();
        pump_events();
    }
}

void WevaDocument::_input(const Ref<InputEvent>& event) {
    // Observe only light dismissal. Activation and typing belong exclusively
    // to _gui_input. Flush the previous event before another one is routed;
    // the deferred call also covers the last event of a frame.
    dismiss_outside_transients();
    if (!interactive_ || !doc_ || !is_visible_in_tree()) return;
    // With gamepad_wake, a controller's first press on a screen nobody has
    // focused wakes the document, the way a mouse click would: this node
    // takes Godot focus, FOCUS_ENTER selects the first HTML control, and
    // that press is spent. Only when nothing else holds focus, so a game
    // that put focus on its own Control keeps it. Opt-in, because a HUD
    // that is always on screen would otherwise take the movement stick.
    const Ref<InputEventJoypadButton> wake_button = event;
    const Ref<InputEventJoypadMotion> wake_motion = event;
    if (gamepad_navigation_ && gamepad_wake_ && !has_focus() && get_focus_mode() != FOCUS_NONE &&
        ((wake_button.is_valid() && wake_button->is_pressed()) ||
         (wake_motion.is_valid() && (wake_motion->get_axis_value() > 0.5f || wake_motion->get_axis_value() < -0.5f)))) {
        Viewport* viewport = get_viewport();
        if (viewport && viewport->gui_get_focus_owner() == nullptr) {
            wake_event_ = event;
            grab_focus();
            return;
        }
    }
    const Ref<InputEventMouseButton> button = event;
    if (button.is_null() || !button->is_pressed() || button->get_button_index() != MOUSE_BUTTON_LEFT) return;
    pointer_focus_entry_ = true;
    outside_dismiss_version_ = weva_document_transient_version(doc_);
    callable_mp(this, &WevaDocument::dismiss_outside_transients).call_deferred();
}

void WevaDocument::_gui_input(const Ref<InputEvent>& event) {
    if (!interactive_ || !doc_ || event.is_null() || !is_visible_in_tree()) return;
    const Ref<InputEventMouseButton> routed_button = event;
    if (routed_button.is_valid() && routed_button->is_pressed() && routed_button->get_button_index() == MOUSE_BUTTON_LEFT)
        outside_dismiss_version_ = 0;
    ensure_updated(0, 0, true);

    const Ref<InputEventKey> key = event;
    if (key.is_valid()) {
        // Result text may arrive as several Unicode key events after the OS
        // clears its preedit. Collect that batch before replacing the range.
        if (has_composition() || ime_end_pending_) {
            if (key->is_pressed() && key->get_unicode() >= 32 && key->get_unicode() != 127) {
                schedule_ime_end();
                ime_commit_text_ += String::chr(key->get_unicode());
            }
            accept_event();
            return;
        }
        uint32_t modifiers = 0;
        if (key->is_shift_pressed()) modifiers |= WEVA_MOD_SHIFT;
        if (key->is_ctrl_pressed()) modifiers |= WEVA_MOD_CTRL;
        if (key->is_alt_pressed()) modifiers |= WEVA_MOD_ALT;
        if (key->is_meta_pressed()) modifiers |= WEVA_MOD_META;
        const int code = weva_key_from_godot(key->get_keycode());
        bool consumed = false;
        if (key->is_pressed() && code == WEVA_KEY_TAB && !key->is_ctrl_pressed()) {
            const bool backwards = key->is_shift_pressed();
            consumed = weva_document_focus_step(doc_, backwards, 0) != WEVA_ELEMENT_NONE;
            // At the edge, let Godot continue its native focus chain. If this
            // is the only Control, keep the HTML tab order cycling locally.
            Control* next = backwards ? find_prev_valid_focus() : find_next_valid_focus();
            if (!consumed && (!next || next == this))
                consumed = weva_document_focus_step(doc_, backwards, 1) != WEVA_ELEMENT_NONE;
        } else {
            consumed = weva_document_key(doc_, code, modifiers, key->is_pressed() ? 1 : 0) != 0;
        }
        const bool shortcut = key->is_command_or_control_pressed() && !key->is_alt_pressed();
        if (key->is_pressed() && shortcut) {
            switch (key->get_keycode()) {
                case KEY_A: consumed = select_all() || consumed; break;
                case KEY_C:
                case KEY_X: {
                    const String selected = get_selected_text();
                    if (!selected.is_empty()) {
                        DisplayServer::get_singleton()->clipboard_set(selected);
                        if (key->get_keycode() == KEY_X) send_key(KEY_BACKSPACE);
                        consumed = true;
                    }
                    break;
                }
                case KEY_V: {
                    const CharString text = DisplayServer::get_singleton()->clipboard_get().utf8();
                    consumed = weva_document_paste_text(doc_, text.get_data()) != 0 || consumed;
                    break;
                }
                case KEY_Z: consumed = (key->is_shift_pressed() ? redo() : undo()) || consumed; break;
                case KEY_Y: consumed = redo() || consumed; break;
                default: break;
            }
        }
        // The unicode a key produced is a separate thing from the key: Shift+1
        // is one key and the text "!", and a dead key produces no text at all.
        if (key->is_pressed() && !consumed && !shortcut && key->get_unicode() >= 32 && key->get_unicode() != 127) {
            const String character = String::chr(key->get_unicode());
            const CharString utf8 = character.utf8();
            consumed = weva_document_try_text_input_modifiers(doc_, utf8.get_data(), modifiers) != 0 || consumed;
        }
        dirty_ = true;
        queue_redraw();
        pump_events();
        if (consumed) accept_event();
        return;
    }

    // A controller reaches the focused Control as joypad events. The project's
    // ui_* actions say what they mean; the document answers as a browser
    // would to the keyboard those actions stand in for.
    const Ref<InputEventJoypadButton> joypad_button = event;
    const Ref<InputEventJoypadMotion> joypad_motion = event;
    if (joypad_button.is_valid() || joypad_motion.is_valid()) {
        const bool woke = wake_event_.is_valid() && wake_event_ == event;
        wake_event_.unref();
        if (woke) {
            accept_event();
            return;
        }
        if (gamepad_navigation_ && navigation_action(event)) {
            dirty_ = true;
            queue_redraw();
            pump_events();
            accept_event();
        }
        return;
    }

    // A finger dragging pans what is under it. There is no mouse equivalent --
    // a browser does not pan on drag, and doing so would fight every button
    // and slider in the document -- but on a touchscreen it is the only way to
    // scroll at all.
    const Ref<InputEventScreenDrag> touch = event;
    if (touch.is_valid()) {
        const Vector2 at = touch->get_position();
        const Vector2 by = touch->get_relative();
        ensure_updated(0, 0, true);
        // Negated: the content follows the finger, so dragging UP moves the
        // list down through the view.
        if (weva_document_scroll(doc_, at.x, at.y, -by.x, -by.y)) {
            dirty_ = true;
            queue_redraw();
            get_viewport()->set_input_as_handled();
        }
        return;
    }

    const Ref<InputEventMouseMotion> motion = event;
    const Ref<InputEventMouseButton> button = event;
    if (motion.is_null() && button.is_null()) return;
    if (button.is_valid() && button->is_pressed() && button->get_button_index() == MOUSE_BUTTON_LEFT && !has_focus())
        grab_focus();

    // Control delivers local coordinates, including CanvasLayer/camera/scale.
    const Vector2 local = motion.is_valid() ? motion->get_position() : button->get_position();
    // The wheel, before the pointer bookkeeping: a wheel event carries no
    // movement, so the "nothing moved" early return below would swallow it.
    if (button.is_valid() && button->is_pressed()) {
        // Godot reports a wheel notch as a button press with a factor; a line
        // is the 40px a browser scrolls per notch.
        const double factor = button->get_factor() > 0 ? button->get_factor() : 1.0;
        const double step = 40.0 * factor;
        double dx = 0, dy = 0;
        switch (button->get_button_index()) {
            case MOUSE_BUTTON_WHEEL_UP: dy = -step; break;
            case MOUSE_BUTTON_WHEEL_DOWN: dy = step; break;
            case MOUSE_BUTTON_WHEEL_LEFT: dx = -step; break;
            case MOUSE_BUTTON_WHEEL_RIGHT: dx = step; break;
            default: break;
        }
        if (dx != 0 || dy != 0) {
            ensure_updated(0, 0, true);
            // Handled only when something actually scrolled, so a wheel over a
            // document with nowhere to go still reaches the game behind it.
            if (weva_document_scroll(doc_, local.x, local.y, dx, dy)) {
                dirty_ = true;
                queue_redraw();
                get_viewport()->set_input_as_handled();
            }
            return;
        }
    }

    // A double click takes the word under it. The platform decides what counts
    // as one -- the document is never told the time -- so this is where that
    // knowledge enters.
    if (button.is_valid() && button->is_pressed() && button->is_double_click() &&
        button->get_button_index() == MOUSE_BUTTON_LEFT) {
        ensure_updated(0, 0, true);
        if (weva_document_select_word_at(doc_, local.x, local.y)) {
            dirty_ = true;
            queue_redraw();
            pump_events();
            get_viewport()->set_input_as_handled();
            return;
        }
    }

    uint32_t buttons = buttons_;
    if (button.is_valid()) {
        // All three buttons now, as a mask. The right button used to be
        // dropped here -- "the host's to route" -- which meant a document
        // could never hear a right-click at all and no context menu could be
        // built on top of one. The core keeps activation to the primary
        // button, so forwarding the others changes nothing about what a
        // click does.
        uint32_t bit = 0;
        switch (button->get_button_index()) {
            case MOUSE_BUTTON_LEFT: bit = WEVA_BUTTON_PRIMARY; break;
            case MOUSE_BUTTON_RIGHT: bit = WEVA_BUTTON_SECONDARY; break;
            case MOUSE_BUTTON_MIDDLE: bit = WEVA_BUTTON_MIDDLE; break;
            default: break;   // the wheel is handled above
        }
        if (bit != 0) {
            if (button->is_pressed()) buttons |= bit;
            else buttons &= ~bit;
        }
    }
    const Ref<InputEventWithModifiers> mouse_modifiers = event;
    uint32_t modifiers = 0;
    if (mouse_modifiers->is_shift_pressed()) modifiers |= WEVA_MOD_SHIFT;
    if (mouse_modifiers->is_ctrl_pressed()) modifiers |= WEVA_MOD_CTRL;
    if (mouse_modifiers->is_alt_pressed()) modifiers |= WEVA_MOD_ALT;
    if (mouse_modifiers->is_meta_pressed()) modifiers |= WEVA_MOD_META;
    if (local == pointer_ && buttons == buttons_ && modifiers == pointer_modifiers_) return;
    pointer_ = local;
    buttons_ = buttons;
    pointer_modifiers_ = modifiers;
    weva_document_set_pointer_modifiers(doc_, local.x, local.y, buttons, modifiers);
    // The document decides whether anything actually changed; an update that
    // finds no style different publishes the frame it already had.
    dirty_ = true;
    queue_redraw();
    pump_events();
    if (button.is_valid()) accept_event();
}

void WevaDocument::_notification(int what) {
    if (what == NOTIFICATION_THEME_CHANGED) {
        theme_font_dirty_ = true;
        dirty_ = true;
        queue_redraw();
    }
    if (what == NOTIFICATION_ENTER_TREE) last_input_tick_usec_ = 0;
    if (what == MainLoop::NOTIFICATION_OS_IME_UPDATE) receive_ime_update();
    if (what == NOTIFICATION_FOCUS_EXIT || what == NOTIFICATION_WM_WINDOW_FOCUS_OUT || what == NOTIFICATION_EXIT_TREE)
        close_ime();
    if (what == NOTIFICATION_WM_WINDOW_FOCUS_OUT) {
        finish_composition();
        clear_pointer();
    }
    if (what == NOTIFICATION_WM_WINDOW_FOCUS_IN) sync_ime();
    if (what == NOTIFICATION_RESIZED) sync_control_size();
    if ((what == NOTIFICATION_WM_SIZE_CHANGED || what == NOTIFICATION_ENTER_TREE) && follow_display_safe_area_) {
        apply_display_safe_area();
    }
    if (what == NOTIFICATION_MOUSE_EXIT && buttons_ == 0) clear_pointer();
    if (what == NOTIFICATION_FOCUS_ENTER && doc_ && interactive_) {
        ensure_updated();
        // A pointer entry focuses what was clicked in _gui_input. Selecting
        // the first HTML field here can scroll it into view before that click.
        if (!pointer_focus_entry_ && weva_document_focus(doc_) == WEVA_ELEMENT_NONE)
            weva_document_focus_next(doc_, Input::get_singleton()->is_action_pressed("ui_focus_prev") ||
                                           Input::get_singleton()->is_key_pressed(KEY_SHIFT));
        dirty_ = true;
        queue_redraw();
        pump_events();
    }
    if (what == NOTIFICATION_FOCUS_EXIT) held_direction_ = -1;
    if (what == NOTIFICATION_FOCUS_EXIT && doc_ && !retain_html_focus_) set_focus(String());
    if (what == NOTIFICATION_VISIBILITY_CHANGED && is_inside_tree() && !is_visible_in_tree()) clear_pointer();
    if (what == NOTIFICATION_EXIT_TREE && doc_) {
        weva_document_clear_pointer(doc_);
        pointer_ = Vector2(-1, -1);
        buttons_ = 0;
        outside_dismiss_version_ = 0;
    }
}

void WevaDocument::set_pointer(const Vector2& point, int buttons, int modifiers) {
    if (!doc_) return;
    ensure_updated();
    pointer_ = point;
    buttons_ = static_cast<uint32_t>(buttons);
    pointer_modifiers_ = static_cast<uint32_t>(modifiers);
    weva_document_set_pointer_modifiers(doc_, point.x, point.y, buttons_, pointer_modifiers_);
    sync_gui_focus();
    dirty_ = true;
    queue_redraw();
    pump_events();
}

godot::String WevaDocument::focus_next(bool backwards) {
    if (!doc_) return String();
    ensure_updated();
    const weva_element_t e = weva_document_focus_next(doc_, backwards ? 1 : 0);
    sync_gui_focus();
    dirty_ = true;
    queue_redraw();
    // Delivered here rather than next frame: a script that moves focus and
    // then looks at what happened should not have to wait for a redraw.
    pump_events();
    return id_of(e);
}

godot::String WevaDocument::focus_move(const Vector2& direction) {
    if (!doc_) return String();
    ensure_updated();
    const weva_element_t e = weva_document_focus_move(doc_, direction.x, direction.y);
    sync_gui_focus();
    dirty_ = true;
    queue_redraw();
    return id_of(e);
}

bool WevaDocument::scroll_at(const Vector2& point, const Vector2& delta) {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_scroll(doc_, point.x, point.y, delta.x, delta.y)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::scroll_element(const String& selector, const Vector2& delta) {
    if (!doc_) return false;
    ensure_updated();
    const Vector2 at = get_element_scroll(selector);
    const Vector2 most = get_element_scroll_max(selector);
    const Vector2 to(CLAMP(at.x + delta.x, 0.0f, most.x), CLAMP(at.y + delta.y, 0.0f, most.y));
    if (to == at) return false;
    set_element_scroll(selector, to);
    return true;
}

void WevaDocument::set_element_scroll(const String& selector, const Vector2& offset) {
    if (!doc_) return;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return;
    weva_element_set_scroll(doc_, e, offset.x, offset.y);
    dirty_ = true;
    queue_redraw();
}

Vector2 WevaDocument::get_element_scroll(const String& selector) {
    if (!doc_) return Vector2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2();
    double x = 0, y = 0;
    if (weva_element_scroll(doc_, e, &x, &y, nullptr, nullptr) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(x), static_cast<real_t>(y));
}

Vector2 WevaDocument::get_element_scroll_max(const String& selector) {
    if (!doc_) return Vector2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2();
    double mx = 0, my = 0;
    if (weva_element_scroll(doc_, e, nullptr, nullptr, &mx, &my) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(mx), static_cast<real_t>(my));
}

// ---- Building the document from data ------------------------------------
//
// Setting text and classes lets a script update a panel; these let it build
// one. Rows are addressed the way CSS addresses them -- `#list .row:nth-child(2)`
// -- so a script that can style a list can also fill it, without inventing a
// second naming scheme for the same elements.

// ---- Input a host routes itself ----------------------------------------
//
// `interactive` makes the document receive Godot's routed GUI events.
// A game with its own input map can hand events over one at a time through
// these explicit document-coordinate operations.

namespace {
struct Direction {
    const char* action;
    JoyButton fallback;
    int key;
    double dx, dy;
};
const Direction kDirections[4] = {
    {"ui_left", JOY_BUTTON_DPAD_LEFT, WEVA_KEY_LEFT, -1, 0}, {"ui_right", JOY_BUTTON_DPAD_RIGHT, WEVA_KEY_RIGHT, 1, 0},
    {"ui_up", JOY_BUTTON_DPAD_UP, WEVA_KEY_UP, 0, -1}, {"ui_down", JOY_BUTTON_DPAD_DOWN, WEVA_KEY_DOWN, 0, 1}};
// A held direction repeats after the delay, then at the interval: the
// keyboard's own timing for the delay, a menu's pace for the rate.
constexpr uint64_t kRepeatDelayUsec = 400000;
constexpr uint64_t kRepeatIntervalUsec = 100000;

// Whether the project bound any joypad event to the action; otherwise the
// conventional button stands in for it.
bool mapped_to_joypad(const char* action) {
    InputMap* map = InputMap::get_singleton();
    if (!map || !map->has_action(action)) return false;
    const TypedArray<InputEvent> events = map->action_get_events(action);
    for (int64_t i = 0; i < events.size(); ++i) {
        const Ref<InputEvent> bound = events[i];
        if (bound.is_valid() && (bound->is_class("InputEventJoypadButton") || bound->is_class("InputEventJoypadMotion")))
            return true;
    }
    return false;
}
} // namespace

String WevaDocument::focused_tag(String* type) const {
    if (type) *type = String();
    if (!doc_) return String();
    const weva_element_t focused = weva_document_focus(doc_);
    if (focused == WEVA_ELEMENT_NONE) return String();
    char tag[32] = {};
    weva_element_tag_name(doc_, focused, tag, sizeof(tag));
    if (type) {
        char kind[32] = {};
        weva_element_attribute(doc_, focused, "type", kind, sizeof(kind));
        *type = String::utf8(kind).to_lower();
    }
    return String::utf8(tag);
}

// ui_accept is Space on a control and Enter in a field, the two keys a
// browser activates with; ui_cancel is Escape and is only claimed when the
// document closed something, so the game's own back action still works.
// Left/right reach the focused element first (a slider's value, a caret, a
// radio group) and move focus only when it did not want them; up/down move
// focus unless the element is one whose value they change (<select>,
// <textarea>, number fields). A menu of sliders therefore reads as a
// settings screen: down and up between rows, left and right on the row.
bool WevaDocument::navigation_action(const Ref<InputEvent>& event) {
    InputMap* map = InputMap::get_singleton();
    if (!map || !doc_) return false;
    // Godot's default map gives the pad and stick to ui_left/right/up/down
    // but binds no joypad button to ui_accept or ui_cancel. An action the
    // project mapped to the pad is honoured as mapped; one it left without
    // any joypad binding answers to the conventional button instead.
    const Ref<InputEventJoypadButton> joypad_button = event;
    const auto pressed = [&](const char* action, JoyButton fallback) {
        if (!map->has_action(action)) return false;
        if (mapped_to_joypad(action)) return event->is_action_pressed(action);
        return joypad_button.is_valid() && joypad_button->is_pressed() && joypad_button->get_button_index() == fallback;
    };
    const auto tap = [&](int code) {
        const bool down = weva_document_key(doc_, code, 0, 1) != 0;
        const bool up = weva_document_key(doc_, code, 0, 0) != 0;
        return down || up;
    };
    String type;
    const String tag = focused_tag(&type);
    const bool text_field = tag == "textarea" ||
        (tag == "input" && (type.is_empty() || type == "text" || type == "search" || type == "password" ||
                            type == "email" || type == "url" || type == "tel" || type == "number"));
    if (pressed("ui_accept", JOY_BUTTON_A)) {
        if (text_field && gamepad_text_entry_) {
            // A pad cannot type. Whoever listens supplies the keyboard; the
            // field keeps its focus and caret meanwhile.
            emit_signal("text_entry_requested", get_focused_id());
        } else if (text_field) {
            tap(WEVA_KEY_ENTER);
        } else if (!tap(WEVA_KEY_SPACE)) {
            tap(WEVA_KEY_ENTER);
        }
        return true;
    }
    if (pressed("ui_cancel", JOY_BUTTON_B)) return tap(WEVA_KEY_ESCAPE);
    if (pressed("ui_focus_next", JOY_BUTTON_RIGHT_SHOULDER)) {
        weva_document_focus_next(doc_, 0);
        return true;
    }
    if (pressed("ui_focus_prev", JOY_BUTTON_LEFT_SHOULDER)) {
        weva_document_focus_next(doc_, 1);
        return true;
    }
    for (int i = 0; i < 4; ++i) {
        const Direction& d = kDirections[i];
        if (!pressed(d.action, d.fallback)) continue;
        navigate_direction(i, tag, type);
        // Holding the direction repeats after a keyboard-like delay.
        held_direction_ = i;
        held_by_action_ = mapped_to_joypad(d.action);
        held_device_ = event->get_device();
        repeat_at_usec_ = Time::get_singleton()->get_ticks_usec() + kRepeatDelayUsec;
        return true;
    }
    return false;
}

bool WevaDocument::navigate_direction(int index, const String& tag, const String& type) {
    const Direction& d = kDirections[index];
    const bool vertical = d.dy != 0;
    const bool element_first = !vertical || tag == "select" || tag == "textarea" ||
                               (tag == "input" && type == "number");
    if (element_first) {
        const bool down = weva_document_key(doc_, d.key, 0, 1) != 0;
        const bool up = weva_document_key(doc_, d.key, 0, 0) != 0;
        if (down || up) return true;
    }
    weva_document_focus_move(doc_, d.dx, d.dy);
    return true;
}

// Polled each frame: a pad direction still held keeps stepping, the way a
// held arrow key keeps stepping through its OS repeat, so a long list does
// not need a press per row. Release, focus loss or turning navigation off
// ends it.
void WevaDocument::repeat_navigation(uint64_t now_usec) {
    if (held_direction_ < 0) return;
    Input* input = Input::get_singleton();
    const Direction& d = kDirections[held_direction_];
    const bool still_held = gamepad_navigation_ && input && has_focus() && is_visible_in_tree() &&
        (held_by_action_ ? input->is_action_pressed(d.action) : input->is_joy_button_pressed(held_device_, d.fallback));
    if (!still_held) {
        held_direction_ = -1;
        return;
    }
    if (now_usec < repeat_at_usec_) return;
    repeat_at_usec_ = now_usec + kRepeatIntervalUsec;
    String type;
    const String tag = focused_tag(&type);
    navigate_direction(held_direction_, tag, type);
    dirty_ = true;
    queue_redraw();
    pump_events();
}

bool WevaDocument::send_key(int keycode, bool pressed, bool shift, bool ctrl) {
    if (!doc_) return false;
    ensure_updated();
    uint32_t modifiers = 0;
    if (shift) modifiers |= WEVA_MOD_SHIFT;
    if (ctrl) modifiers |= WEVA_MOD_CTRL;
    const int code = weva_key_from_godot(static_cast<Key>(keycode));
    const bool consumed = weva_document_key(doc_, code, modifiers, pressed ? 1 : 0) != 0;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return consumed;
}

void WevaDocument::send_text(const String& text) {
    if (!doc_ || text.is_empty()) return;
    ensure_updated();
    const CharString utf8 = text.utf8();
    weva_document_text_input(doc_, utf8.get_data());
    dirty_ = true;
    queue_redraw();
    pump_events();
}

bool WevaDocument::paste_text(const String& text) {
    if (!doc_ || text.is_empty()) return false;
    ensure_updated();
    const CharString utf8 = text.utf8();
    const bool consumed = weva_document_paste_text(doc_, utf8.get_data()) != 0;
    if (consumed) {
        dirty_ = true;
        queue_redraw();
        pump_events();
    }
    return consumed;
}

// ---- Selection -----------------------------------------------------------
//
// Shift with the movement keys selects, and typing replaces what is selected;
// those the document does by itself. These are the two it cannot: select-all,
// because the ABI's key enum has no letters and so never sees Ctrl+A, and
// reading the selected text, because the clipboard belongs to the platform.

bool WevaDocument::select_word_at(const Vector2& point) {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_select_word_at(doc_, point.x, point.y)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

// ---- Dropdowns -----------------------------------------------------------
//
// Clicking a <select> opens it and clicking an option chooses it, all through
// the pointer the node already forwards. These are for a host that routes its
// own input -- a controller opening the list, a menu closing it.

bool WevaDocument::open_select(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (!weva_document_open_select(doc_, e)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

void WevaDocument::close_select() {
    if (!doc_) return;
    weva_document_open_select(doc_, WEVA_ELEMENT_NONE);
    dirty_ = true;
    queue_redraw();
}

String WevaDocument::get_open_select() {
    if (!doc_) return String();
    return id_of(weva_document_open_select_element(doc_));
}

bool WevaDocument::select_all() {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_select_all(doc_)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::undo() {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_undo(doc_)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::redo() {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_redo(doc_)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

String WevaDocument::get_selected_text() {
    if (!doc_) return String();
    ensure_updated();
    const size_t n = weva_document_selected_text(doc_, nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_document_selected_text(doc_, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

bool WevaDocument::set_element_selection(const String& selector, int start, int end) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_set_selection(doc_, e, start, end) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::set_element_selection_without_focus(const String& selector, int start, int end) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_set_selection_without_focus(doc_, e, start, end) != WEVA_OK) return false;
    if (weva_document_focus(doc_) == e) {
        dirty_ = true;
        queue_redraw();
    }
    return true;
}

Vector2i WevaDocument::get_element_selection(const String& selector) {
    if (!doc_) return Vector2i();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2i();
    int start = 0, end = 0;
    if (weva_element_selection(doc_, e, &start, &end) != WEVA_OK) return Vector2i();
    return Vector2i(start, end);
}

// ---- Data binding --------------------------------------------------------
//
// `{{ Player.Gold }}` in the markup and a Dictionary in the script. A dotted
// path walks nested Dictionaries and Objects, so `{{ Player.Gold }}` reads
// data["Player"]["Gold"] whether Player is a Dictionary, a Resource or a Node.
// A Callable takes over entirely when a game keeps its state somewhere this
// cannot reach.

namespace {

// One step of a dotted path.
bool step(const Variant& from, const String& key, Variant* out) {
    switch (from.get_type()) {
        case Variant::DICTIONARY: {
            // One checked lookup; unlike operator[], it cannot insert a
            // missing key. The validity flag also distinguishes a nil value.
            bool valid = false;
            *out = from.get(key, &valid);
            return valid;
        }
        case Variant::OBJECT: {
            Object* o = from;
            if (o == nullptr) return false;
            // Godot's checked property read supports script/resource getters
            // without constructing the complete editor property list for
            // every segment of every binding on each refresh.
            bool valid = false;
            *out = from.get_named(StringName(key), valid);
            return valid;
        }
        case Variant::ARRAY: {
            // `Items.0` indexes a list, which is what a repeat will want.
            if (!key.is_valid_int()) return false;
            const Array a = from;
            const int i = key.to_int();
            if (i < 0 || i >= a.size()) return false;
            *out = a[i];
            return true;
        }
        default: return false;
    }
}

}   // namespace

const PackedStringArray& WevaDocument::binding_parts(const String& path) const {
    auto found = binding_paths_.find(path);
    if (found != binding_paths_.end()) return found->second;
    if (binding_paths_.size() >= 1024) binding_paths_.clear();
    return binding_paths_.emplace(path, path.split(".")).first->second;
}

bool WevaDocument::resolve_binding(const String& path, String* out) const {
    if (data_source_.is_valid()) {
        const Variant v = data_source_.call(path);
        if (v.get_type() == Variant::NIL) return false;
        *out = v.stringify();
        return true;
    }
    Variant current = data_;
    const PackedStringArray parts = binding_parts(path);
    for (int i = 0; i < parts.size(); ++i) {
        Variant next;
        if (!step(current, parts[i], &next)) return false;
        current = next;
    }
    if (current.get_type() == Variant::NIL) return false;
    // A bool arrives as "true"/"false", which `data-class-` and an attribute
    // selector both read the way they read the "True"/"False" the Unity engine
    // produces.
    *out = current.stringify();
    return true;
}

// How long the list at `path` is, or -1 when it is not one. `data-each` is the
// only caller: an Array answers, and so does anything with a size() a script
// exposed, but a String is deliberately NOT a list of characters.
static int weva_binding_count(void* user, const char* path) {
    WevaDocument* node = static_cast<WevaDocument*>(user);
    return node->resolve_binding_count(String::utf8(path));
}

static size_t weva_binding_read(void* user, const char* path, char* buffer, size_t capacity,
                                int* found) {
    WevaDocument* node = static_cast<WevaDocument*>(user);
    String value;
    if (!node->resolve_binding(String::utf8(path), &value)) {
        *found = 0;
        return 0;
    }
    *found = 1;
    const CharString utf8 = value.utf8();
    const size_t length = static_cast<size_t>(utf8.length());
    if (buffer && capacity > 0) {
        const size_t n = length < capacity - 1 ? length : capacity - 1;
        if (n > 0) memcpy(buffer, utf8.get_data(), n);
        buffer[n] = 0;
    }
    return length;
}

bool WevaDocument::run_popover(const String& selector,
                              weva_status (*fn)(weva_document_t, weva_element_t)) {
    if (!doc_) return false;
    ensure_updated();
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return false;
    if (fn(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

void WevaDocument::set_tooltip_delay(double seconds) {
    tooltip_delay_ = seconds;
    if (!doc_) return;
    weva_document_set_tooltip_delay(doc_, seconds);
    dirty_ = true;
    queue_redraw();
}

double WevaDocument::get_tooltip_delay() const { return tooltip_delay_; }

void WevaDocument::set_dark_color_scheme(bool dark) {
    dark_color_scheme_ = dark;
    if (!doc_) return;
    weva_document_set_color_scheme(doc_, dark ? 1 : 0);
    dirty_ = true;
    queue_redraw();
}

bool WevaDocument::get_dark_color_scheme() const { return dark_color_scheme_; }

void WevaDocument::set_safe_area_insets(double top, double right, double bottom, double left) {
    safe_area_[0] = std::max(0.0, top);
    safe_area_[1] = std::max(0.0, right);
    safe_area_[2] = std::max(0.0, bottom);
    safe_area_[3] = std::max(0.0, left);
    if (!doc_) return;
    weva_document_set_safe_area_insets(doc_, safe_area_[0], safe_area_[1], safe_area_[2], safe_area_[3]);
    dirty_ = true;
    queue_redraw();
}

Vector4 WevaDocument::get_safe_area_insets() const {
    return Vector4(safe_area_[0], safe_area_[1], safe_area_[2], safe_area_[3]);
}

void WevaDocument::set_follow_display_safe_area(bool follow) {
    follow_display_safe_area_ = follow;
    if (follow && is_inside_tree()) apply_display_safe_area();
}

bool WevaDocument::get_follow_display_safe_area() const { return follow_display_safe_area_; }

// The screen's safe area against the screen: the distance from each edge to
// the rectangle the display keeps clear of notches and system bars. On a
// desktop that is all zeros.
void WevaDocument::apply_display_safe_area() {
    DisplayServer* ds = DisplayServer::get_singleton();
    if (!ds) return;
    const Rect2i safe = ds->get_display_safe_area();
    const Vector2i screen = ds->screen_get_size();
    if (screen.x <= 0 || screen.y <= 0 || safe.size.x <= 0 || safe.size.y <= 0) return;
    set_safe_area_insets(safe.position.y, screen.x - (safe.position.x + safe.size.x),
                         screen.y - (safe.position.y + safe.size.y), safe.position.x);
}

String WevaDocument::get_cursor() const {
    if (!doc_) return "default";
    const size_t n = weva_document_cursor(doc_, nullptr, 0);
    std::string text(n + 1, '\0');
    weva_document_cursor(doc_, text.data(), text.size());
    return String(text.c_str());
}

void WevaDocument::set_follow_css_cursor(bool follow) { follow_css_cursor_ = follow; }

bool WevaDocument::get_follow_css_cursor() const { return follow_css_cursor_; }

Array WevaDocument::get_box_tree() const {
    Array out;
    if (!doc_) return out;
    const size_t n = weva_document_boxes(doc_, nullptr, 0);
    std::vector<weva_box> boxes(n);
    const size_t written = weva_document_boxes(doc_, boxes.data(), boxes.size());
    static const char* kinds[] = {"block", "anonymous-block", "inline", "anonymous-inline", "line", "text"};
    for (size_t i = 0; i < written; ++i) {
        const weva_box& b = boxes[i];
        Dictionary d;
        d["parent"] = b.parent == WEVA_BOX_NONE ? -1 : static_cast<int64_t>(b.parent);
        d["kind"] = b.kind < 6 ? kinds[b.kind] : "unknown";
        d["element"] = b.element == WEVA_ELEMENT_NONE ? -1 : static_cast<int64_t>(b.element);
        d["rect"] = Rect2(static_cast<float>(b.x), static_cast<float>(b.y), static_cast<float>(b.width), static_cast<float>(b.height));
        d["margin"] = Rect2(static_cast<float>(b.margin_left), static_cast<float>(b.margin_top),
                            static_cast<float>(b.margin_right), static_cast<float>(b.margin_bottom));
        d["border"] = Rect2(static_cast<float>(b.border_left), static_cast<float>(b.border_top),
                            static_cast<float>(b.border_right), static_cast<float>(b.border_bottom));
        d["padding"] = Rect2(static_cast<float>(b.padding_left), static_cast<float>(b.padding_top),
                             static_cast<float>(b.padding_right), static_cast<float>(b.padding_bottom));
        d["scroll"] = Vector2(static_cast<float>(b.scroll_x), static_cast<float>(b.scroll_y));
        if (b.text) d["text"] = String::utf8(b.text, static_cast<int>(b.text_length));
        out.push_back(d);
    }
    return out;
}

Dictionary WevaDocument::get_stats() const {
    Dictionary out;
    weva_stats s{};
    if (doc_) weva_document_stats(doc_, &s);
    out["update_ms"] = s.update_ms;
    out["cascade_ms"] = s.cascade_ms;
    out["animate_ms"] = s.animate_ms;
    out["boxes_ms"] = s.boxes_ms;
    out["layout_ms"] = s.layout_ms;
    out["paint_ms"] = s.paint_ms;
    out["updates"] = static_cast<int64_t>(s.updates);
    out["elements"] = static_cast<int64_t>(s.elements);
    out["boxes"] = static_cast<int64_t>(s.boxes);
    out["draws"] = static_cast<int64_t>(s.draws);
    out["textures"] = static_cast<int64_t>(s.textures);
    out["texture_cache_hits"] = static_cast<int64_t>(s.texture_cache_hits);
    out["texture_cache_misses"] = static_cast<int64_t>(s.texture_cache_misses);
    out["cascade_elements"] = static_cast<int64_t>(s.cascade_elements);
    out["cascade_pseudos"] = static_cast<int64_t>(s.cascade_pseudos);
    return out;
}

// Godot asks the hovered Control for its cursor shape on every mouse motion;
// answering the query is the whole integration. Setting the Control's
// default_cursor_shape instead would make Godot re-dispatch a synthetic
// mouse motion at the display's idea of the mouse position, which in a
// headless run sat outside the node, fired a mouse-exit, and cancelled the
// range drag range_direction_tests was in the middle of.
//
// The CSS keyword to the shape Godot can show; anything without a shape
// here is the arrow, as a browser falls back to its default.
int32_t WevaDocument::_get_cursor_shape(const Vector2& at_position) const {
    if (!doc_ || !follow_css_cursor_) return CURSOR_ARROW;
    const size_t n = weva_document_cursor_at(doc_, at_position.x, at_position.y, nullptr, 0);
    std::string text(n + 1, '\0');
    weva_document_cursor_at(doc_, at_position.x, at_position.y, text.data(), text.size());
    const String k(text.c_str());
    if (k == "pointer") return CURSOR_POINTING_HAND;
    if (k == "text" || k == "vertical-text") return CURSOR_IBEAM;
    if (k == "wait") return CURSOR_WAIT;
    if (k == "progress") return CURSOR_BUSY;
    if (k == "crosshair") return CURSOR_CROSS;
    if (k == "move" || k == "all-scroll") return CURSOR_MOVE;
    if (k == "grab" || k == "grabbing") return CURSOR_DRAG;
    if (k == "not-allowed" || k == "no-drop") return CURSOR_FORBIDDEN;
    if (k == "help") return CURSOR_HELP;
    if (k == "e-resize" || k == "w-resize" || k == "ew-resize" || k == "col-resize") return CURSOR_HSIZE;
    if (k == "n-resize" || k == "s-resize" || k == "ns-resize" || k == "row-resize") return CURSOR_VSIZE;
    if (k == "ne-resize" || k == "sw-resize" || k == "nesw-resize") return CURSOR_BDIAGSIZE;
    if (k == "nw-resize" || k == "se-resize" || k == "nwse-resize") return CURSOR_FDIAGSIZE;
    return CURSOR_ARROW;
}

Dictionary WevaDocument::get_focused_row() {
    Dictionary out;
    if (!doc_) return out;
    ensure_updated();
    const weva_element_t e = weva_document_focus(doc_);
    if (e == WEVA_ELEMENT_NONE) return out;
    int index = 0;
    char key[128] = {0};
    if (!weva_element_row(doc_, e, &index, key, sizeof(key))) return out;
    out["index"] = index;
    out["key"] = String::utf8(key);
    return out;
}

Dictionary WevaDocument::get_row(const String& selector) {
    Dictionary out;
    if (!doc_) return out;
    ensure_updated();
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return out;
    int index = 0;
    char key[128] = {0};
    if (!weva_element_row(doc_, e, &index, key, sizeof(key))) return out;
    out["index"] = index;
    out["key"] = String::utf8(key);
    return out;
}

String WevaDocument::get_focused_id() {
    if (!doc_) return String();
    ensure_updated();
    return id_of(weva_document_focus(doc_));
}

bool WevaDocument::show_popover(const String& selector) {
    const bool accepted = run_popover(selector, &weva_element_request_show_popover);
    if (accepted) pump_events();
    return accepted;
}

bool WevaDocument::hide_popover(const String& selector) {
    const bool accepted = run_popover(selector, &weva_element_request_hide_popover);
    if (accepted) pump_events();
    return accepted;
}

bool WevaDocument::toggle_popover(const String& selector) {
    const bool accepted = run_popover(selector, &weva_element_request_toggle_popover);
    if (accepted) pump_events();
    return accepted;
}

bool WevaDocument::show_dialog(const String& selector) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_show_dialog(doc_, e, 0) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::show_modal_dialog(const String& selector) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_show_dialog(doc_, e, 1) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::request_close_dialog(const String& selector, const Variant& result) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString value = String(result).utf8();
    if (weva_element_request_close_dialog_with_value(doc_, e, result.get_type() == Variant::NIL ? nullptr : value.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

bool WevaDocument::set_custom_validity(const String& selector, const String& message) {
    if (!doc_) return false;
    const auto element = resolve(selector, false);
    const CharString text = message.utf8();
    if (weva_element_set_custom_validity(doc_, element, text.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

String WevaDocument::get_custom_validity(const String& selector) {
    if (!doc_) return String();
    const auto element = resolve(selector, false);
    const size_t size = weva_element_custom_validity(doc_, element, nullptr, 0);
    std::vector<char> text(size + 1);
    weva_element_custom_validity(doc_, element, text.data(), text.size());
    return String::utf8(text.data());
}

Dictionary WevaDocument::get_element_validity(const String& selector) {
    Dictionary result;
    uint32_t errors = 0;
    int will_validate = 0;
    if (!doc_) return result;
    const auto status = weva_element_validity(doc_, resolve(selector, false), &errors, &will_validate);
    if (status == WEVA_ERR_UNSUPPORTED)
        UtilityFunctions::push_warning("get_element_validity: Unicode-v pattern validation is not implemented.");
    if (status != WEVA_OK) return result;
    result["will_validate"] = will_validate != 0;
    result["valid"] = errors == 0;
    const char* names[] = {"value_missing", "type_mismatch", "pattern_mismatch", "too_long", "too_short",
        "range_underflow", "range_overflow", "step_mismatch", "bad_input", "custom_error"};
    for (uint32_t bit = 0; bit < 10; ++bit) result[names[bit]] = (errors & (1u << bit)) != 0;
    return result;
}

bool WevaDocument::check_validity(const String& selector) { return run_validity(selector, false); }
bool WevaDocument::report_validity(const String& selector) { return run_validity(selector, true); }
bool WevaDocument::run_validity(const String& selector, bool report) {
    if (!doc_) return false;
    int valid = 0;
    const auto element = resolve(selector, false);
    const auto status = report ? weva_element_report_validity(doc_, element, &valid)
                               : weva_element_check_validity(doc_, element, &valid);
    if (status != WEVA_OK) {
        if (status == WEVA_ERR_UNSUPPORTED || status == WEVA_ERR_INVALID_STATE)
            UtilityFunctions::push_warning("Validity request unsupported: pattern constraints or nested invalid-handler validation.");
        return false;
    }
    pump_events();
    return valid != 0;
}

String WevaDocument::get_dialog_return_value(const String& selector) {
    if (!doc_) return {};
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return {};
    const size_t size = weva_element_dialog_return_value(doc_, e, nullptr, 0);
    std::vector<char> value(size + 1, 0);
    weva_element_dialog_return_value(doc_, e, value.data(), value.size());
    return String::utf8(value.data());
}

bool WevaDocument::set_dialog_return_value(const String& selector, const String& value) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString text = value.utf8();
    return weva_element_set_dialog_return_value(doc_, e, text.get_data()) == WEVA_OK;
}

bool WevaDocument::prevent_default() {
    return doc_ && weva_document_prevent_default(doc_) != 0;
}

bool WevaDocument::close_dialog(const String& selector, const Variant& result) {
    if (!doc_) return false;
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString value = String(result).utf8();
    if (weva_element_close_dialog_with_value(doc_, e, result.get_type() == Variant::NIL ? nullptr : value.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::has_element_attribute(const String& selector, const String& name) {
    if (!doc_) return false;
    // Attributes are DOM state; reading one must not publish pending layout.
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString n = name.utf8();
    return weva_element_has_attribute(doc_, e, n.get_data()) != 0;
}

String WevaDocument::get_element_attribute(const String& selector, const String& name) {
    if (!doc_) return String();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return String();
    const CharString key = name.utf8();
    const size_t n = weva_element_attribute(doc_, e, key.get_data(), nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_element_attribute(doc_, e, key.get_data(), buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

void WevaDocument::set_controller(Object* controller) {
    controller_ = controller ? controller->get_instance_id() : ObjectID();
}

Object* WevaDocument::get_controller() const {
    return controller_.is_valid() ? ObjectDB::get_instance(controller_) : nullptr;
}

int WevaDocument::resolve_binding_count(const String& path) const {
    if (data_source_.is_valid()) {
        const Variant v = data_source_.call(path);
        return v.get_type() == Variant::ARRAY ? static_cast<int>(Array(v).size()) : -1;
    }
    Variant current = data_;
    const PackedStringArray parts = binding_parts(path);
    for (int i = 0; i < parts.size(); ++i) {
        Variant next;
        if (!step(current, parts[i], &next)) return -1;
        current = next;
    }
    return current.get_type() == Variant::ARRAY ? static_cast<int>(Array(current).size()) : -1;
}

void WevaDocument::set_data(const Dictionary& data) {
    data_ = data;
    refresh_bindings();
}

Dictionary WevaDocument::get_data() const { return data_; }

void WevaDocument::set_data_source(const Callable& resolver) {
    data_source_ = resolver;
    refresh_bindings();
}

int WevaDocument::refresh_bindings() {
    bindings_active_ = true;
    if (!doc_) return 0;
    weva_binding_source source{};
    source.user = this;
    source.value = &weva_binding_read;
    source.count = &weva_binding_count;
    weva_document_set_binding_source(doc_, &source);
    static const bool profile = std::getenv("WEVA_GODOT_BINDING_PROFILE") != nullptr;
    using BindingClock = std::chrono::steady_clock;
    const auto started = profile ? BindingClock::now() : BindingClock::time_point{};
    int changed = weva_document_refresh_bindings(doc_);
    const auto core_done = profile ? BindingClock::now() : BindingClock::time_point{};
    // The controls come last, because `data-each` may only just have produced
    // the rows the models live on.
    changed += apply_models();
    if (profile) {
        const auto done = BindingClock::now();
        std::fprintf(stderr, "weva binding work: core %.6f ms; models %.6f ms; changes %d\n",
            std::chrono::duration<double, std::milli>(core_done - started).count(),
            std::chrono::duration<double, std::milli>(done - core_done).count(), changed);
    }
    if (changed > 0) {
        dirty_ = true;
        queue_redraw();
    }
    return changed;
}

godot::String WevaDocument::value_of(uint32_t element) {
    if (!doc_ || element == WEVA_ELEMENT_NONE) return String();
    char local[128];
    const size_t n = weva_element_value(doc_, element, local, sizeof(local));
    if (n == 0) return String();
    if (n < sizeof(local)) return String::utf8(local, static_cast<int64_t>(n));
    std::vector<char> buffer(n + 1, 0);
    weva_element_value(doc_, element, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

// An element's `data-model`, resolved against the whole document. Inside a
// repeated row the author writes the row's alias -- `quest.Done` -- and only
// the core knows which item that row is, so it does the unwinding.
String WevaDocument::get_computed_style(const String& selector, const String& property) {
    if (!doc_) return String();
    ensure_updated();
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const CharString prop = property.utf8();
    const size_t n = weva_element_computed_style(doc_, e, prop.get_data(), nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_element_computed_style(doc_, e, prop.get_data(), buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

// The matches, in document order. Shared by the three query_all_* below so the
// two-call convention is written once.
std::vector<uint32_t> WevaDocument::matches(const String& selector) {
    std::vector<uint32_t> out;
    if (!doc_) return out;
    ensure_updated();
    const CharString sel = selector.utf8();
    const size_t count = weva_document_query_all(doc_, sel.get_data(), nullptr, 0);
    if (count == 0) return out;
    out.resize(count);
    const size_t written = weva_document_query_all(doc_, sel.get_data(), out.data(), out.size());
    out.resize(written);
    return out;
}

PackedStringArray WevaDocument::query_all_text(const String& selector) {
    PackedStringArray out;
    for (uint32_t e : matches(selector)) {
        const size_t n = weva_element_text(doc_, e, nullptr, 0);
        if (n == 0) {
            out.push_back(String());
            continue;
        }
        std::vector<char> buffer(n + 1, 0);
        weva_element_text(doc_, e, buffer.data(), buffer.size());
        out.push_back(String::utf8(buffer.data()));
    }
    return out;
}

Array WevaDocument::query_all_bounds(const String& selector) {
    Array out;
    for (uint32_t e : matches(selector)) {
        double x = 0, y = 0, w = 0, h = 0;
        if (weva_element_bounds(doc_, e, &x, &y, &w, &h) != WEVA_OK) {
            out.push_back(Rect2());
            continue;
        }
        out.push_back(Rect2(static_cast<real_t>(x), static_cast<real_t>(y),
                            static_cast<real_t>(w), static_cast<real_t>(h)));
    }
    return out;
}

PackedStringArray WevaDocument::query_all_ids(const String& selector) {
    PackedStringArray out;
    for (uint32_t e : matches(selector)) out.push_back(id_of(e));
    return out;
}

godot::String WevaDocument::model_path_of(uint32_t element) {
    if (!doc_ || element == WEVA_ELEMENT_NONE) return String();
    const String written = attribute_of(element, "data-model");
    if (written.is_empty()) return String();
    const CharString raw = written.utf8();
    const size_t n = weva_element_model_path(doc_, element, raw.get_data(), nullptr, 0);
    if (n == 0) return written;
    std::vector<char> buffer(n + 1, 0);
    weva_element_model_path(doc_, element, raw.get_data(), buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

godot::String WevaDocument::attribute_of(uint32_t element, const char* name) {
    if (!doc_ || element == WEVA_ELEMENT_NONE) return String();
    char local[128];
    const size_t n = weva_element_attribute(doc_, element, name, local, sizeof(local));
    if (n == 0) return String();
    if (n < sizeof(local)) return String::utf8(local, static_cast<int64_t>(n));
    std::vector<char> buffer(n + 1, 0);
    weva_element_attribute(doc_, element, name, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

// Data -> control. Only where they disagree: writing a field's own value back
// into it would move the caret to the end while someone is typing in it.
int WevaDocument::apply_models() {
    if (!doc_) return 0;
    // Most game screens have only a few editable controls. Fill in one scan;
    // large forms retain the same unbounded fallback as the public query API.
    weva_element_t local[32];
    weva_element_t* elements = local;
    size_t count = weva_document_query_all(doc_, "[data-model]", local, 32);
    if (count == 0) return 0;
    std::vector<weva_element_t> overflow;
    if (count > 32) {
        overflow.resize(count);
        elements = overflow.data();
        count = weva_document_query_all(doc_, "[data-model]", elements, count);
    }

    applying_models_ = true;
    int changed = 0;
    for (size_t i = 0; i < count; ++i) {
        const String path = model_path_of(elements[i]);
        if (path.is_empty()) continue;
        String wanted;
        if (!resolve_binding(path, &wanted)) continue;
        const String previous = value_of(elements[i]);
        if (previous == wanted) continue;
        const auto version = weva_element_form_version(doc_, elements[i]);
        const bool composing = weva_document_composition(doc_, nullptr, nullptr) == elements[i];
        const CharString v = wanted.utf8();
        if (weva_element_set_value(doc_, elements[i], v.get_data()) != WEVA_OK) continue;
        // Compare actual inputs, not raw model spelling (true -> on, range
        // rounding/clamping). A validity/edit-source change or composition
        // commit still needs publication even when the public text is equal.
        if (weva_element_form_version(doc_, elements[i]) != version || composing) ++changed;
    }
    applying_models_ = false;
    return changed;
}

// Control -> data. The type already at the path wins: a script that put a
// float in `data.Volume` gets a float back, not the "0.7" the control reports,
// so its own arithmetic keeps working after the first drag.
bool WevaDocument::write_data_path(const godot::String& path, const godot::String& text) {
    const PackedStringArray parts = path.split(".");
    if (parts.is_empty()) return false;

    // Walk to the container that holds the last segment, making the
    // dictionaries a path names but that the data does not have yet.
    Variant current = data_;
    for (int i = 0; i < parts.size() - 1; ++i) {
        Variant next;
        if (!step(current, parts[i], &next) || next.get_type() == Variant::NIL) {
            if (current.get_type() != Variant::DICTIONARY) return false;
            next = Dictionary();
            Dictionary holder = current;
            holder[parts[i]] = next;
        }
        current = next;
    }
    const String leaf = parts[parts.size() - 1];

    Variant value = text;
    Variant existing;
    if (step(current, leaf, &existing)) {
        switch (existing.get_type()) {
            case Variant::BOOL: {
                const String boolean = text.to_lower();
                value = boolean == "true" || boolean == "1" || boolean == "on";
                break;
            }
            case Variant::INT: value = static_cast<int64_t>(text.to_int()); break;
            case Variant::FLOAT: value = text.to_float(); break;
            default: break;
        }
        // Input already writes the live value; its later change/commit event
        // must not emit another data_changed signal or rescan all bindings.
        // Compare after conversion so boolean "on" and numeric spellings use
        // the model's actual type. The public commit event is still delivered.
        if (existing.get_type() == value.get_type() && existing == value) return false;
    }

    if (current.get_type() == Variant::DICTIONARY) {
        Dictionary holder = current;
        holder[leaf] = value;
        return true;
    }
    if (current.get_type() == Variant::ARRAY && leaf.is_valid_int()) {
        Array a = current;
        const int at = leaf.to_int();
        if (at < 0 || at >= a.size()) return false;
        a[at] = value;
        return true;
    }
    return false;
}

bool WevaDocument::set_element_html(const String& selector, const String& html) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString body = html.utf8();
    if (weva_element_set_html(doc_, e, body.get_data(),
                              static_cast<size_t>(body.length())) != WEVA_OK) {
        return false;
    }
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::append_html(const String& selector, const String& html) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString body = html.utf8();
    const weva_element_t added =
        weva_element_append_html(doc_, e, body.get_data(), static_cast<size_t>(body.length()));
    dirty_ = true;
    queue_redraw();
    return added != WEVA_ELEMENT_NONE;
}

bool WevaDocument::remove_element(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_remove(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

int WevaDocument::count_elements(const String& selector) {
    if (!doc_) return 0;
    ensure_updated();
    const CharString sel = selector.utf8();
    return static_cast<int>(weva_document_query_all(doc_, sel.get_data(), nullptr, 0));
}

bool WevaDocument::scroll_into_view(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_scroll_into_view(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

void WevaDocument::clear_pointer() {
    if (!doc_) return;
    weva_document_clear_pointer(doc_);
    pointer_ = Vector2(-1, -1);
    buttons_ = 0;
    dirty_ = true;
    queue_redraw();
    pump_events();
}

godot::String WevaDocument::id_of(uint32_t element) {
    if (!doc_ || element == WEVA_ELEMENT_NONE) return String();
    char buffer[128];
    const size_t n = weva_element_attribute(doc_, element, "id", buffer, sizeof(buffer));
    if (n == 0 || n >= sizeof(buffer)) return String();
    return String::utf8(buffer);
}

uint32_t WevaDocument::resolve(const godot::String& selector, bool flush) {
    if (!doc_ || selector.is_empty()) return WEVA_ELEMENT_NONE;
    // DOM and live pseudo-state queries need no layout. Ordinary writes can
    // accumulate until a read, interaction, explicit update or frame consumes
    // them; updating three HUD bars must not paint three intermediate frames.
    // Reads retain synchronous geometry/style semantics through the default.
    if (flush) ensure_updated();
    const CharString s = selector.utf8();
    return weva_document_query(doc_, s.get_data());
}

void WevaDocument::pump_events() {
    if (!doc_ || pumping_events_) return;
    // A handler may update layout or mutate the document. Nested updates must
    // not poll again and apply a cancel default before that handler can veto it.
    pumping_events_ = true;
    weva_event e{};
    while (weva_document_poll_event(doc_, &e)) {
        String event_text;
        if (e.kind == WEVA_EVENT_TEXT_INPUT || e.kind == WEVA_EVENT_COMPOSITION_START ||
            e.kind == WEVA_EVENT_COMPOSITION_UPDATE || e.kind == WEVA_EVENT_COMPOSITION_END) {
            const size_t size = weva_document_event_text(doc_, nullptr, 0);
            std::vector<char> text(size + 1, 0);
            weva_document_event_text(doc_, text.data(), text.size());
            event_text = String::utf8(text.data());
        }
        const String id = id_of(e.target);
        if (e.kind == WEVA_EVENT_RESET) write_back_form_models(e.target);
        // What the MARKUP called it, before what the element is called. A
        // controller with that method gets it, and `handler_invoked` carries
        // it either way -- so a script can dispatch by the name the designer
        // wrote rather than by which element it happened on.
        if (e.handler[0] != 0) {
            const String handler = String::utf8(e.handler);
            Object* controller = get_controller();
            if (controller && controller->has_method(handler)) {
                controller->call(handler, id);
            }
            emit_signal("handler_invoked", handler, id);
            // A repeated row usually has no id, so `handler_invoked` alone
            // cannot say WHICH row was worked. This carries the row's
            // position and its `data-key` identity, which is what a list
            // handler actually wants.
            int row_index = 0;
            char row_key[128] = {0};
            if (weva_element_row(doc_, e.target, &row_index, row_key, sizeof(row_key))) {
                emit_signal("row_activated", handler, row_index, String::utf8(row_key));
            }
        }
        switch (e.kind) {
            case WEVA_EVENT_CLICK: emit_signal("element_clicked", id); break;
            case WEVA_EVENT_POINTER_DOWN: emit_signal("element_pressed", id); break;
            case WEVA_EVENT_POINTER_UP: emit_signal("element_released", id); break;
            case WEVA_EVENT_POINTER_ENTER: emit_signal("element_entered", id); break;
            case WEVA_EVENT_POINTER_LEAVE: emit_signal("element_exited", id); break;
            case WEVA_EVENT_KEY_DOWN:
                emit_signal("key_pressed", id, e.key, static_cast<int>(e.modifiers));
                break;
            case WEVA_EVENT_TEXT_INPUT: emit_signal("text_entered", id, event_text); break;
            case WEVA_EVENT_COMPOSITION_START: emit_signal("composition_started", id, event_text); break;
            case WEVA_EVENT_COMPOSITION_UPDATE: emit_signal("composition_updated", id, event_text); break;
            case WEVA_EVENT_COMPOSITION_END: emit_signal("composition_ended", id, event_text); break;
            case WEVA_EVENT_FOCUS: emit_signal("element_focused", id); break;
            case WEVA_EVENT_BLUR: emit_signal("element_blurred", id); break;
            case WEVA_EVENT_VALUE_CHANGED:
                // Read from the HANDLE, not from "#" + id: a row a `data-each`
                // produced has no id, and a selector built from an empty one
                // matches nothing and reports an empty value.
                emit_signal("value_changed", id, value_of(e.target));
                write_back_model(e.target);
                break;
            case WEVA_EVENT_CHANGE:
                // The value the user settled on, once. A search field that
                // hits the disk on every keystroke wants this and not
                // `value_changed`.
                emit_signal("value_committed", id, value_of(e.target));
                write_back_model(e.target);
                break;
            case WEVA_EVENT_SUBMIT: emit_signal("form_submitted", id); break;
            case WEVA_EVENT_INVALID:
                emit_signal("element_invalid", id);
                break;
            case WEVA_EVENT_RESET: emit_signal("form_reset", id); break;
            case WEVA_EVENT_CLOSE:
                dirty_ = true;
                queue_redraw();
                emit_signal("dialog_closed", id);
                break;
            case WEVA_EVENT_CANCEL: emit_signal("dialog_cancel_requested", id); break;
            case WEVA_EVENT_CONTEXT_MENU:
                // Where the user asked for a menu. The engine has none of its
                // own to show -- a menu is markup -- so this is the signal to
                // position one and open it.
                emit_signal("context_menu_requested", id, Vector2(e.x, e.y));
                break;
            case WEVA_EVENT_BEFORE_TOGGLE:
                emit_signal("element_before_toggled", id, std::strcmp(e.text, "open") == 0);
                break;
            case WEVA_EVENT_TOGGLE:
                // Captured when queued, including popovers and events whose
                // handlers changed the element again before delivery.
                emit_signal("element_toggled", id, std::strcmp(e.text, "open") == 0);
                break;
            case WEVA_EVENT_SCROLL:
                // Where it scrolled TO, so a script can load more when a list
                // nears its end without asking the document again.
                emit_signal("element_scrolled", id, e.x, e.y);
                break;
            default: break;
        }
    }
    pumping_events_ = false;
    // A default action can move core focus after an invalid handler has already
    // flushed its own edits. Publish that input change before IME synchronization;
    // an INVALID notification alone has no visual effect and needs no refresh.
    if (consumed_interaction_version_ != weva_document_interaction_version(doc_)) {
        dirty_ = true;
        queue_redraw();
        sync_gui_focus();
    }
    sync_ime();
}

// One control's value into the data it is modelled on. Skipped while a model
// is being pushed the other way, so the two directions cannot chase each
// other, and skipped entirely when a resolver owns the data -- a Callable can
// answer a path but has nowhere to put an answer.
void WevaDocument::write_back_model(uint32_t element) {
    if (applying_models_ || data_source_.is_valid()) return;
    const String path = model_path_of(element);
    if (path.is_empty()) return;
    const String value = value_of(element);
    if (!write_data_path(path, value)) return;
    emit_signal("data_changed", path, value);
    // Anything else bound to the same path follows it -- a label beside the
    // slider, a class that turns on past a threshold.
    refresh_bindings();
}

void WevaDocument::write_back_form_models(uint32_t form) {
    if (applying_models_ || data_source_.is_valid()) return;
    const auto elements = matches("[data-model]");
    std::vector<std::pair<String, String>> changed;
    for (const uint32_t element : elements) {
        if (weva_element_form(doc_, element) != form) continue;
        const String path = model_path_of(element);
        if (path.is_empty()) continue;
        const String value = value_of(element);
        String previous;
        if (resolve_binding(path, &previous) && previous == value) continue;
        if (write_data_path(path, value)) changed.emplace_back(path, value);
    }
    // Update the entire form before bindings can reapply an old model value
    // to another control that reset just restored.
    for (const auto& entry : changed) emit_signal("data_changed", entry.first, entry.second);
    if (!changed.empty()) refresh_bindings();
}

bool WevaDocument::reset_form(const String& selector) {
    const uint32_t form = resolve(selector);
    if (form == WEVA_ELEMENT_NONE || weva_document_reset_form(doc_, form) != WEVA_OK) return false;
    dirty_ = true;
    pump_events();
    sync_ime();
    queue_redraw();
    return true;
}

bool WevaDocument::set_element_text(const godot::String& selector, const godot::String& text) {
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString t = text.utf8();
    if (weva_element_set_text(doc_, e, t.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

godot::String WevaDocument::get_element_text(const godot::String& selector) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const size_t needed = weva_element_text(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1, '\0');
    weva_element_text(doc_, e, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

godot::String WevaDocument::get_element_value(const godot::String& selector) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const size_t needed = weva_element_value(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1, '\0');
    weva_element_value(doc_, e, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

bool WevaDocument::set_element_value(const godot::String& selector, const godot::String& value) {
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString v = value.utf8();
    if (weva_element_set_value(doc_, e, v.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::has_element(const godot::String& selector) {
    return resolve(selector) != WEVA_ELEMENT_NONE;
}

// The class list, read-modify-write. A script toggling a state class is the
// ordinary way to drive a :hover-style rule from game logic, and doing it by
// hand through set_attribute means every caller reimplements the split.
bool WevaDocument::toggle_element_class(const godot::String& selector, const godot::String& name,
                                        bool on) {
    const uint32_t e = resolve(selector, false);
    if (e == WEVA_ELEMENT_NONE || name.is_empty()) return false;
    char buffer[512];
    const size_t n = weva_element_attribute(doc_, e, "class", buffer, sizeof(buffer));
    if (n >= sizeof(buffer)) return false;
    PackedStringArray tokens = String(buffer).split(" ", false);
    PackedStringArray kept;
    bool present = false;
    for (int i = 0; i < tokens.size(); ++i) {
        if (tokens[i] == name) { present = true; continue; }
        kept.push_back(tokens[i]);
    }
    if (on) kept.push_back(name);
    if (present == on) return true;   // already as asked
    const String joined = String(" ").join(kept);
    const CharString value = joined.utf8();
    if (weva_element_set_attribute(doc_, e, "class", value.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::add_element_class(const godot::String& selector, const godot::String& name) {
    return toggle_element_class(selector, name, true);
}

bool WevaDocument::remove_element_class(const godot::String& selector, const godot::String& name) {
    return toggle_element_class(selector, name, false);
}

godot::String WevaDocument::element_id_at(const Vector2& point) {
    if (!doc_) return String();
    ensure_updated();
    const weva_element_t e = weva_document_element_at(doc_, point.x, point.y);
    if (e == WEVA_ELEMENT_NONE) return String();
    // The handle is an index; a script wants something it can act on, and an
    // id is the one stable name the document has.
    char buffer[128];
    const size_t n = weva_element_attribute(doc_, e, "id", buffer, sizeof(buffer));
    if (n == 0 || n >= sizeof(buffer)) return String();
    return String::utf8(buffer);
}

bool WevaDocument::set_focus(const godot::String& selector) {
    if (!doc_) return false;
    // The core queues reveal when layout is pending. Opening a dialog then
    // focusing its first field can publish one final frame, including IME.
    if (selector.is_empty()) {
        const bool ok = weva_document_set_focus(doc_, WEVA_ELEMENT_NONE) == WEVA_OK;
        if (has_focus()) release_focus();
        dirty_ = true;
        queue_redraw();
        pump_events();
        return ok;
    }
    const CharString s = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, s.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_document_set_focus(doc_, e) != WEVA_OK) return false;
    sync_gui_focus();
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

// Animation and timed pointer gestures share the frame, but pausing CSS time
// must leave input responsive. Settled core updates still return immediately.
void WevaDocument::_process(double delta) {
    // Godot's delta is simulation time (including Engine.time_scale).
    // Captured input uses elapsed monotonic time, even in slow motion.
    const uint64_t now = Time::get_singleton()->get_ticks_usec();
    const double input_dt = last_input_tick_usec_ ? (now - last_input_tick_usec_) / 1000000.0 : 0;
    last_input_tick_usec_ = now;
    if (!doc_) return;
    repeat_navigation(now);
    if (paused_) {
        // Input time advances held gestures while CSS time stays fixed.
        if (dirty_ || paint_pending_ || weva_document_needs_input_tick(doc_)) ensure_updated(0,input_dt);
        pump_events();
        return;
    }
    pending_dt_ += delta;
    if (!dirty_ && !paint_pending_ && pending_dt_ <= 0 && !weva_document_needs_input_tick(doc_)) return;
    const double dt = pending_dt_;
    pending_dt_ = 0;
    ensure_updated(dt,input_dt);
    pump_events();
}

void WevaDocument::ensure_updated(double dt, double input_dt, bool geometry_only) {
    if (input_dt < 0) input_dt = dt;
    if (!doc_ || (!dirty_ && (geometry_only || !paint_pending_) && dt <= 0 && input_dt <= 0)) return;
    ensure_font_backend();
    // Not an error to update an empty document: a scene may set css before
    // html, and the next update picks both up.
    const auto t0 = std::chrono::steady_clock::now();
    const uint64_t previous_draw = weva_document_draw_serial(doc_);
    if (geometry_only) weva_document_update_geometry(doc_);
    else weva_document_update_with_input_time(doc_, dt, input_dt);
    if (weva_document_draw_serial(doc_) != previous_draw) queue_redraw();
    last_update_ms_ =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    total_core_update_ms_ += last_update_ms_;
    ++core_update_count_;
    consumed_interaction_version_ = weva_document_interaction_version(doc_);
    // Geometry is current for hit testing; a normal update must still publish
    // pending paint, including when automatic processing is disabled.
    dirty_ = false;
    paint_pending_ = geometry_only;

    // Texture views are published together with a new draw list. A settled
    // update leaves both unchanged: keep the host's map as well, avoiding a
    // map-node allocation and Ref churn for every retained texture each tick.
    if (weva_document_draw_serial(doc_) == previous_draw) return;

    // Mirror the document's textures by id: a texture already held is kept
    // (ids are never reused within a document), a new one is uploaded, one
    // the document dropped is dropped here. The ABI publishes whole textures,
    // and there are a handful per document rather than one per frame.
    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(doc_, &texture_count);
    std::map<uint64_t, Ref<ImageTexture>> next;
    for (size_t i = 0; i < texture_count; ++i) {
        const weva_texture& t = textures[i];
        const auto held = textures_.find(t.id);
        if (held != textures_.end()) {
            next[t.id] = held->second;
            continue;
        }
        const int w = t.width, h = t.height;
        if (w <= 0 || h <= 0 || !t.rgba) continue;
        PackedByteArray bytes;
        bytes.resize(static_cast<int64_t>(w) * h * 4);
        std::memcpy(bytes.ptrw(), t.rgba, static_cast<size_t>(w) * h * 4);
        const Ref<Image> img = Image::create_from_data(w, h, false, Image::FORMAT_RGBA8, bytes);
        next[t.id] = ImageTexture::create_from_image(img);
    }
    textures_.swap(next);
}

void WevaDocument::update_document(double dt) {
    dirty_ = true;
    ensure_updated(dt);
    // Events raised by whatever the caller just did are delivered here too, so
    // a script that drives the pointer and then updates does not have to wait
    // for a frame to hear about it.
    pump_events();
    // ensure_updated already schedules a redraw when the published draw serial
    // changes. A second serial cache here misses automatic/initial draws and
    // would queue a redundant redraw on the next explicit update.

}

// Evaluating a rounded box per pixel, which is what a rasterizer that only
// takes triangles cannot do.
//
// The core's coverage ramp approximates the edge with a one-pixel band of
// interpolated alpha. This computes the real thing: the signed distance to the
// rounded box, divided by its own screen-space derivative, which is the exact
// fractional coverage to well under a percent -- the same quantity Skia builds
// its coverage mask from. It is also correct under any scale, where a baked
// ramp is only correct at the scale it was baked for.
static const char* kRoundedShader = R"(shader_type canvas_item;
render_mode unshaded;

uniform vec2 half_size;
uniform vec4 radii;        // top-left, top-right, bottom-right, bottom-left
uniform vec4 fill;

varying vec2 local;

void vertex() {
    // UV carries the position within the quad, so the fragment stage can work
    // in the shape's own coordinates whatever the canvas transform is.
    local = (UV - vec2(0.5)) * (half_size * 2.0);
}

float sd_round_box(vec2 p, vec2 b, vec4 r) {
    r.xy = (p.x > 0.0) ? r.xy : r.zw;
    r.x = (p.y > 0.0) ? r.x : r.y;
    vec2 q = abs(p) - b + r.x;
    return min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r.x;
}

void fragment() {
    float d = sd_round_box(local, half_size, radii);
    // fwidth is how far d moves across one pixel, so d/fwidth(d) is the
    // distance to the edge measured IN pixels however the quad is scaled.
    float w = fwidth(d);
    float cov = w > 0.0 ? clamp(0.5 - d / w, 0.0, 1.0) : (d <= 0.0 ? 1.0 : 0.0);
    COLOR = vec4(fill.rgb, fill.a * cov);
}
)";

RID WevaDocument::rounded_rect_material(const weva_rounded_rect& s) {
    RenderingServer* rs = RenderingServer::get_singleton();
    if (!rounded_shader_.is_valid()) {
        rounded_shader_ = rs->shader_create();
        rs->shader_set_code(rounded_shader_, String(kRoundedShader));
    }
    // Pooled across frames: a material per rounded box per frame would churn
    // hundreds of RIDs on a dashboard.
    if (rounded_used_ >= rounded_materials_.size()) {
        const RID m = rs->material_create();
        rs->material_set_shader(m, rounded_shader_);
        rounded_materials_.push_back(m);
    }
    const RID m = rounded_materials_[rounded_used_++];

    rs->material_set_param(m, "half_size",
                           Vector2(static_cast<real_t>(s.width * 0.5),
                                   static_cast<real_t>(s.height * 0.5)));
    // One radius per corner: the SDF takes a circular corner, so an elliptical
    // one is approximated by its smaller axis. CSS rarely asks for an ellipse
    // and this path declines the shape when it does (see draw_rounded_rect).
    rs->material_set_param(m, "radii",
                           Color(static_cast<real_t>(s.radii[0][0]), static_cast<real_t>(s.radii[1][0]),
                                 static_cast<real_t>(s.radii[2][0]), static_cast<real_t>(s.radii[3][0])));
    rs->material_set_param(m, "fill", Color(s.r, s.g, s.b, s.a).linear_to_srgb());
    return m;
}

bool WevaDocument::draw_rounded_rect(const RID& item, const weva_draw& d) {
    if (!use_sdf_rects_) return false;
    const weva_rounded_rect& s = d.rounded_rect;
    if (s.width <= 0 || s.height <= 0) return false;
    // Elliptical corners are not what the SDF computes; hand those back so the
    // tessellation draws them correctly rather than nearly.
    for (int i = 0; i < 4; ++i) {
        if (std::fabs(s.radii[i][0] - s.radii[i][1]) > 0.01) return false;
    }

    // One pixel of margin so the coverage ramp has somewhere to land.
    const real_t pad = 1.0f;
    const real_t x0 = static_cast<real_t>(s.x) - pad, y0 = static_cast<real_t>(s.y) - pad;
    const real_t x1 = static_cast<real_t>(s.x + s.width) + pad;
    const real_t y1 = static_cast<real_t>(s.y + s.height) + pad;
    // UV runs 0..1 over the PADDED quad, and the shader maps it back through
    // half_size, so the padding has to be part of the size it is told.
    const real_t hx = (x1 - x0) * 0.5f, hy = (y1 - y0) * 0.5f;

    RenderingServer* rs = RenderingServer::get_singleton();
    const RID m = rounded_rect_material(s);
    rs->material_set_param(m, "half_size", Vector2(hx - pad, hy - pad));

    PackedVector2Array points;
    PackedColorArray colors;
    PackedVector2Array uvs;
    points.resize(4);
    colors.resize(4);
    uvs.resize(4);
    Vector2* pw = points.ptrw();
    Color* cw = colors.ptrw();
    Vector2* uw = uvs.ptrw();
    const Vector2 corners[4] = {Vector2(x0, y0), Vector2(x1, y0), Vector2(x1, y1), Vector2(x0, y1)};
    const Vector2 uv[4] = {Vector2(0, 0), Vector2(1, 0), Vector2(1, 1), Vector2(0, 1)};
    for (int i = 0; i < 4; ++i) {
        pw[i] = corners[i];
        cw[i] = Color(1, 1, 1, 1);
        uw[i] = uv[i];
    }
    PackedInt32Array idx;
    idx.resize(6);
    int32_t* iw = idx.ptrw();
    const int32_t order[6] = {0, 1, 2, 0, 2, 3};
    for (int i = 0; i < 6; ++i) iw[i] = order[i];

    // Its own canvas item, because a material is per item and the document is
    // otherwise one item. That is the cost of this path and what the
    // measurement has to weigh against the triangles it saves.
    const RID quad = rs->canvas_item_create();
    rs->canvas_item_set_parent(quad, item);
    rs->canvas_item_set_material(quad, m);
    rs->canvas_item_set_draw_index(quad, static_cast<int32_t>(layer_items_.size()));
    layer_items_.push_back(quad);
    rs->canvas_item_add_triangle_array(quad, idx, points, colors, uvs, PackedInt32Array(),
                                       PackedFloat32Array(), RID());
    return true;
}

namespace {
size_t triangle_run(const weva_draw* draws, size_t count, bool sdf_rects) {
    static const bool disabled = std::getenv("WEVA_GODOT_DISABLE_BATCHING") != nullptr;
    const auto ordinary = [sdf_rects](const weva_draw& d) {
        return d.kind == WEVA_DRAW_GEOMETRY ||
               (d.kind == WEVA_DRAW_ROUNDED_RECT && !sdf_rects);
    };
    if (!count) return 0;
    if (disabled || !ordinary(draws[0])) return 1;
    size_t vertices = draws[0].vertex_count;
    size_t run = 1;
    // Preserve painter order, texture and material boundaries. Bound each
    // upload without splitting any mesh that already exceeds this size.
    while (run < count && ordinary(draws[run]) &&
           draws[run].vertex_count && draws[run].index_count &&
           draws[run].texture_id == draws[0].texture_id &&
           draws[run].blend_mode == draws[0].blend_mode &&
           vertices + draws[run].vertex_count <= 65536) {
        vertices += draws[run].vertex_count;
        ++run;
    }
    return run;
}
}

void WevaDocument::sync_retained_uniforms() {
    // Shader-free documents avoid uniform enumeration and temporary arrays.
    const auto material = get_material();
    const RID material_rid = material.is_valid() ? material->get_rid() : RID();
    const bool parent_material = get_use_parent_material();
    auto* server = RenderingServer::get_singleton();
    // A changed material owner must refresh the internal item's dependency
    // tracking before instance uniform buffers can use the new shader.
    if (material_rid != retained_material_ || parent_material != retained_parent_material_ || parent_material) {
        for (const auto& batch : packed_batches_)
            if (batch.retained_item.is_valid())
                server->canvas_item_set_use_parent_material(batch.retained_item, true);
        retained_material_ = material_rid;
        retained_parent_material_ = parent_material;
    }
    const bool has_material = material.is_valid() || parent_material;
    if (!has_material && retained_uniforms_.empty()) return;
    std::vector<std::pair<StringName, Variant>> next;
    if (has_material) {
        const auto properties = server->canvas_item_get_instance_shader_parameter_list(get_canvas_item());
        next.reserve(properties.size());
        for (int64_t i = 0; i < properties.size(); ++i) {
            const Dictionary property = properties[i];
            const StringName name = property["name"];
            const Variant value = server->canvas_item_get_instance_shader_parameter(get_canvas_item(), name);
            next.emplace_back(name, value);
            const auto previous = std::find_if(retained_uniforms_.begin(), retained_uniforms_.end(),
                [&](const auto& entry) { return entry.first == name; });
            if (previous != retained_uniforms_.end() && previous->second == value) continue;
            for (const auto& batch : packed_batches_)
                if (batch.retained_item.is_valid())
                    server->canvas_item_set_instance_shader_parameter(batch.retained_item, name, value);
        }
    }
    for (const auto& previous : retained_uniforms_) {
        if (std::any_of(next.begin(), next.end(), [&](const auto& entry) { return entry.first == previous.first; })) continue;
        for (const auto& batch : packed_batches_)
            if (batch.retained_item.is_valid())
                server->canvas_item_set_instance_shader_parameter(batch.retained_item, previous.first, Variant());
    }
    retained_uniforms_.swap(next);
}

void WevaDocument::sync_retained_state() {
    sync_retained_uniforms();
    // CanvasItem self_modulate intentionally does not propagate to children.
    // Mirror it only to our internal items, including frames with no redraw.
    const Color color = get_self_modulate();
    const uint32_t mask = get_light_mask();
    const bool color_changed = color != retained_self_modulate_;
    const bool mask_changed = mask != retained_light_mask_;
    if (!color_changed && !mask_changed) return;
    retained_self_modulate_ = color;
    retained_light_mask_ = mask;
    auto* server = RenderingServer::get_singleton();
    for (const auto& batch : packed_batches_) {
        if (!batch.retained_item.is_valid()) continue;
        if (color_changed) server->canvas_item_set_self_modulate(batch.retained_item, color);
        if (mask_changed) server->canvas_item_set_light_mask(batch.retained_item, mask);
    }
}

void WevaDocument::release_retained_batches(size_t from) {
    auto* rs = RenderingServer::get_singleton();
    if (from == 0 && retained_sync_connected_) {
        if (rs->is_connected("frame_pre_draw", retained_sync_callback_))
            rs->disconnect("frame_pre_draw", retained_sync_callback_);
        retained_sync_connected_ = false;
    }
    for (size_t i = from; i < packed_batches_.size(); ++i) {
        auto& batch = packed_batches_[i];
        if (batch.retained_item.is_valid()) rs->free_rid(batch.retained_item);
        batch.retained_item = RID();
        batch.retained_texture = RID();
    }
}

void WevaDocument::add_triangles(const RID& item, const weva_draw* draws, size_t count,
                                const uint64_t* versions, bool retain) {
    const auto pack_start = draw_profile.enabled ? DrawClock::now() : DrawClock::time_point{};
    // draw_polygon takes a polygon OUTLINE and triangulates it, so feeding it a
    // triangle soup produces garbage where it does not fail outright ("Invalid
    // polygon data, triangulation failed"). canvas_item_add_triangle_array
    // takes the index buffer directly, which is exactly the shape the core
    // already produces — no expansion, and no triangulator second-guessing
    // geometry that is already triangles.
    if (packed_used_ == packed_batches_.size()) packed_batches_.emplace_back();
    PackedBatch& packed = packed_batches_[packed_used_++];
    auto& points = packed.points;
    auto& colors = packed.colors;
    auto& uvs = packed.uvs;
    auto& indices = packed.indices;
    size_t vertex_count = 0, index_count = 0;
    for (size_t i = 0; i < count; ++i) {
        vertex_count += draws[i].vertex_count;
        index_count += draws[i].index_count;
    }
    // Versions describe immutable command inputs. PackedArray copy-on-write
    // keeps earlier RenderingServer submissions valid when a cache slot changes.
    static const bool disable_cache = std::getenv("WEVA_GODOT_DISABLE_PACK_CACHE") != nullptr;
    const bool reuse = !disable_cache && versions && packed.versions.size() == count &&
                       std::equal(packed.versions.begin(), packed.versions.end(), versions);
    if (reuse) {
        if (draw_profile.enabled) ++draw_profile.reused_batches;
    } else {
        if (versions) packed.versions.assign(versions, versions + count);
        else packed.versions.clear();
        if (draw_profile.enabled) draw_profile.packed_vertices += vertex_count;
        points.resize(static_cast<int64_t>(vertex_count));
        colors.resize(static_cast<int64_t>(vertex_count));
        uvs.resize(static_cast<int64_t>(vertex_count));
        Vector2* pw = points.ptrw();
        Color* cw = colors.ptrw();
        Vector2* uw = uvs.ptrw();
        indices.resize(static_cast<int64_t>(index_count));
        int32_t* iw = indices.ptrw();
        float last_rgb[3]{};
        Color converted_rgb;
        bool have_rgb = false;
        size_t base = 0, index_base = 0;
        for (size_t i = 0; i < count; ++i) {
            const weva_draw& d = draws[i];
            for (size_t k = 0; k < d.vertex_count; ++k) {
                const weva_vertex& v = d.vertices[k];
                pw[base+k] = Vector2(v.x, v.y);
                // The core works in linear space; Godot's canvas expects sRGB, so the
                // conversion happens here rather than in the core, where it would be
                // wrong for a backend that wants linear.
                // Glyphs and flat fills repeat one RGB across many vertices. The
                // coverage ramp changes alpha only, which is already linear. Reuse
                // the exact conversion for consecutive identical RGB bit patterns.
                const float rgb[3]{v.r,v.g,v.b};
                if (!have_rgb || std::memcmp(rgb,last_rgb,sizeof(rgb)) != 0) {
                    converted_rgb = Color(v.r,v.g,v.b,1).linear_to_srgb();
                    std::memcpy(last_rgb,rgb,sizeof(rgb));
                    have_rgb = true;
                }
                cw[base+k] = Color(converted_rgb.r,converted_rgb.g,converted_rgb.b,v.a);
                uw[base+k] = Vector2(v.u, v.v);
            }
            for (size_t k = 0; k < d.index_count; ++k)
                iw[index_base+k] = static_cast<int32_t>(base+d.indices[k]);
            base += d.vertex_count;
            index_base += d.index_count;
        }
    }

    RID texture;
    if (draws[0].texture_id != 0) {
        const auto it = textures_.find(draws[0].texture_id);
        if (it != textures_.end() && it->second.is_valid()) texture = it->second->get_rid();
    }
    RID destination = item;
    auto* server = RenderingServer::get_singleton();
    if (retain) {
        const bool created = !packed.retained_item.is_valid();
        if (created) {
            packed.retained_item = server->canvas_item_create();
            server->canvas_item_set_parent(packed.retained_item, item);
            // Draw before user-owned child CanvasItems, preserving paint order
            // within our children without changing z relative to the parent.
            server->canvas_item_set_draw_index(packed.retained_item,
                -2147483647 + static_cast<int32_t>(packed_used_ - 1));
            server->canvas_item_set_use_parent_material(packed.retained_item, true);
            server->canvas_item_set_self_modulate(packed.retained_item, get_self_modulate());
            server->canvas_item_set_light_mask(packed.retained_item, get_light_mask());
            for (const auto& uniform : retained_uniforms_)
                server->canvas_item_set_instance_shader_parameter(packed.retained_item, uniform.first, uniform.second);
            if (!retained_sync_connected_) {
                retained_self_modulate_ = get_self_modulate();
                retained_light_mask_ = get_light_mask();
                if (retained_sync_callback_.is_null())
                    retained_sync_callback_ = callable_mp(this, &WevaDocument::sync_retained_state);
                if (!server->is_connected("frame_pre_draw", retained_sync_callback_))
                    server->connect("frame_pre_draw", retained_sync_callback_);
                retained_sync_connected_ = true;
            }
        }
        destination = packed.retained_item;
        if (!created && reuse && packed.retained_texture == texture) return;
        server->canvas_item_clear(destination);
        packed.retained_texture = texture;
    }
    const auto submit_start = draw_profile.enabled ? DrawClock::now() : DrawClock::time_point{};
    if (draw_profile.enabled) {
        draw_profile.packing_ms += std::chrono::duration<double,std::milli>(submit_start-pack_start).count();
        draw_profile.vertices += vertex_count;
        ++draw_profile.submissions;
        draw_profile.max_batch_vertices = std::max(draw_profile.max_batch_vertices,vertex_count);
    }
    RenderingServer::get_singleton()->canvas_item_add_triangle_array(
        destination, indices, points, colors, uvs, PackedInt32Array(), PackedFloat32Array(), texture);
    if (draw_profile.enabled) draw_profile.submit_ms += draw_elapsed(submit_start);
}

// The shader behind `backdrop-filter`. It reads the back buffer, which is what
// the core cannot do, blurs it and applies the colour matrix the core composed
// — so nothing here parses CSS, and the matrix arrives as three row vectors
// rather than a mat3 because Godot's mat3 uniform binding leaves the row/column
// convention ambiguous and a transposed saturate is not obviously wrong on
// screen.
//
// `blend_disabled` because a backdrop filter REPLACES what is behind the
// element rather than drawing over it; the element's own background is a
// separate draw that lands on top.
static const char* kBackdropShader = R"(shader_type canvas_item;
render_mode blend_disabled, unshaded;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear;
uniform float sigma = 0.0;
uniform vec3 mat_r = vec3(1.0, 0.0, 0.0);
uniform vec3 mat_g = vec3(0.0, 1.0, 0.0);
uniform vec3 mat_b = vec3(0.0, 0.0, 1.0);
uniform vec3 mat_add = vec3(0.0);
uniform float out_alpha = 1.0;

void fragment() {
    vec3 c;
    if (sigma <= 0.0) {
        c = texture(screen_tex, SCREEN_UV).rgb;
    } else {
        // A 7x7 Gaussian, spaced so the taps span about two sigma: enough of
        // the kernel to look like a blur rather than a smear, in one pass.
        // The reference rasteriser runs three box passes instead, so the two
        // agree on shape and not to the last few units.
        float d = sigma * 0.66;
        vec3 acc = vec3(0.0);
        float wsum = 0.0;
        for (int y = -3; y <= 3; y++) {
            for (int x = -3; x <= 3; x++) {
                vec2 o = vec2(float(x), float(y)) * d;
                float w = exp(-dot(o, o) / (2.0 * sigma * sigma));
                acc += texture(screen_tex, SCREEN_UV + o * SCREEN_PIXEL_SIZE).rgb * w;
                wsum += w;
            }
        }
        c = acc / wsum;
    }
    c = vec3(dot(mat_r, c), dot(mat_g, c), dot(mat_b, c)) + mat_add;
    COLOR = vec4(clamp(c, vec3(0.0), vec3(1.0)), out_alpha);
}
)";

RID WevaDocument::backdrop_material() {
    RenderingServer* rs = RenderingServer::get_singleton();
    if (!backdrop_shader_.is_valid()) {
        backdrop_shader_ = rs->shader_create();
        rs->shader_set_code(backdrop_shader_, String(kBackdropShader));
    }
    const RID m = rs->material_create();
    rs->material_set_shader(m, backdrop_shader_);
    layer_materials_.push_back(m);
    return m;
}

void WevaDocument::draw_layered(const weva_draw* draws, size_t count, const uint64_t* versions) {
    RenderingServer* rs = RenderingServer::get_singleton();
    release_layers();

    // One item per run of ordinary geometry, and one per backdrop filter, in z
    // order. The node's own item draws nothing: children render after their
    // parent's commands, so anything left on it would land under everything.
    const auto new_layer = [&](bool backdrop) {
        const RID item = rs->canvas_item_create();
        rs->canvas_item_set_parent(item, get_canvas_item());
        rs->canvas_item_set_z_index(item, static_cast<int32_t>(layer_items_.size()));
        rs->canvas_item_set_draw_index(item, static_cast<int32_t>(layer_items_.size()));
        (void)backdrop;
        layer_items_.push_back(item);
        return item;
    };

    RID current = new_layer(false);
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;

        if (d.kind != WEVA_DRAW_BACKDROP_FILTER) {
            const size_t run = triangle_run(draws+i, count-i, false);
            add_triangles(current, draws+i, run, versions ? versions+i : nullptr);
            i += run-1;
            continue;
        }

        // The region to copy: the shape, grown by the blur's reach, because a
        // pixel at the shape's edge is blurred against its neighbours OUTSIDE
        // it. Copying only the shape would blur it against nothing and rim the
        // panel with a dark halo.
        double lo_x = d.vertices[0].x, hi_x = lo_x;
        double lo_y = d.vertices[0].y, hi_y = lo_y;
        for (size_t k = 1; k < d.vertex_count; ++k) {
            lo_x = std::min<double>(lo_x, d.vertices[k].x);
            hi_x = std::max<double>(hi_x, d.vertices[k].x);
            lo_y = std::min<double>(lo_y, d.vertices[k].y);
            hi_y = std::max<double>(hi_y, d.vertices[k].y);
        }
        const double sigma = d.backdrop.blur_radius / 2.0;
        const double pad = sigma > 0 ? sigma * 3.0 + 1.0 : 0.0;
        const Rect2 region(static_cast<real_t>(lo_x - pad), static_cast<real_t>(lo_y - pad),
                           static_cast<real_t>(hi_x - lo_x + 2 * pad),
                           static_cast<real_t>(hi_y - lo_y + 2 * pad));

        const RID item = new_layer(true);
        rs->canvas_item_set_copy_to_backbuffer(item, true, region);
        const RID mat = backdrop_material();
        rs->material_set_param(mat, "sigma", sigma);
        rs->material_set_param(mat, "mat_r",
                               Vector3(d.backdrop.color_matrix[0], d.backdrop.color_matrix[1],
                                       d.backdrop.color_matrix[2]));
        rs->material_set_param(mat, "mat_g",
                               Vector3(d.backdrop.color_matrix[3], d.backdrop.color_matrix[4],
                                       d.backdrop.color_matrix[5]));
        rs->material_set_param(mat, "mat_b",
                               Vector3(d.backdrop.color_matrix[6], d.backdrop.color_matrix[7],
                                       d.backdrop.color_matrix[8]));
        rs->material_set_param(mat, "mat_add",
                               Vector3(d.backdrop.color_offset[0], d.backdrop.color_offset[1],
                                       d.backdrop.color_offset[2]));
        rs->material_set_param(mat, "out_alpha", d.backdrop.color_alpha);
        rs->canvas_item_set_material(item, mat);
        add_triangles(item, &d, 1, versions ? versions+i : nullptr);

        current = new_layer(false);
    }
}

void WevaDocument::_draw() {
    GodotDrawScope profile;
    ensure_updated();
    if (!doc_) return;

    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc_, &count);
    size_t version_count = 0;
    const uint64_t* versions = weva_document_draw_versions(doc_, &version_count);
    if (version_count != count) versions = nullptr;
    packed_used_ = 0;

    rounded_used_ = 0;
    bool any_backdrop = false;
    for (size_t i = 0; i < count && !any_backdrop; ++i) {
        any_backdrop = draws[i].kind == WEVA_DRAW_BACKDROP_FILTER;
    }
    if (any_backdrop) {
        release_retained_batches();
        draw_layered(draws, count, versions);
        packed_batches_.resize(packed_used_);
        return;
    }
    release_layers();
    // Diagnostic prototype only. Child CanvasItems require further validation
    // of root self_modulate and other inherited rendering properties.
    static const bool retain_requested = std::getenv("WEVA_GODOT_RETAIN_BATCHES") != nullptr;
    const bool retain = retain_requested && !use_sdf_rects_;
    if (!retain) release_retained_batches();
    else sync_retained_uniforms();

    // The core clips scissored geometry before publishing it, so every draw
    // goes on this one canvas item in order. (Per-item clipping via
    // canvas_item_set_clip was tried: the compatibility renderer dropped every
    // draw after a clipped sibling item.)
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;
        if (d.kind == WEVA_DRAW_ROUNDED_RECT && draw_rounded_rect(get_canvas_item(), d)) continue;
        const size_t run = triangle_run(draws+i, count-i, use_sdf_rects_);
        // A blended run gets its own item and material; the retained-batch
        // prototype keeps to normal draws.
        const bool blended = d.blend_mode != WEVA_BLEND_NORMAL;
        add_triangles(blended ? blend_item(d.blend_mode) : get_canvas_item(), draws+i, run,
                      versions ? versions+i : nullptr, retain && !blended);
        i += run-1;
    }
    release_retained_batches(packed_used_);
    packed_batches_.resize(packed_used_);
}

Vector2 WevaDocument::get_content_size() {
    if (!doc_) return Vector2();
    ensure_updated();
    double w = 0, h = 0;
    if (weva_document_content_size(doc_, &w, &h) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(w), static_cast<real_t>(h));
}

Rect2 WevaDocument::query_bounds(const String& selector) {
    if (!doc_) return Rect2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Rect2();
    double x = 0, y = 0, w = 0, h = 0;
    if (weva_element_bounds(doc_, e, &x, &y, &w, &h) != WEVA_OK) return Rect2();
    return Rect2(static_cast<real_t>(x), static_cast<real_t>(y), static_cast<real_t>(w),
                 static_cast<real_t>(h));
}

String WevaDocument::query_text(const String& selector) {
    if (!doc_) return String();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return String();
    // The two-call pattern the ABI is built for: size, then fill.
    const size_t needed = weva_element_text(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1);
    weva_element_text(doc_, e, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

bool WevaDocument::set_element_attribute(const String& selector, const String& name,
                                         const String& value) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString n = name.utf8();
    const CharString v = value.utf8();
    if (weva_element_set_attribute(doc_, e, n.get_data(), v.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    if (name.nocasecmp_to("popover") == 0) pump_events();
    return true;
}

bool WevaDocument::remove_element_attribute(const String& selector, const String& name) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString n = name.utf8();
    // A null value is what the ABI reads as "remove"; GDScript has no way to
    // express one, which is why this is its own method.
    if (weva_element_set_attribute(doc_, e, n.get_data(), nullptr) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    if (name.nocasecmp_to("popover") == 0) pump_events();
    return true;
}

double WevaDocument::get_last_update_ms() const {
    return last_update_ms_;
}

double WevaDocument::get_total_core_update_ms() const {
    return total_core_update_ms_;
}
int64_t WevaDocument::get_core_update_count() const {
    return core_update_count_;
}

int WevaDocument::get_draw_count() const {
    if (!doc_) return 0;
    size_t count = 0;
    weva_document_draws(doc_, &count);
    return static_cast<int>(count);
}

int WevaDocument::get_triangle_count() const {
    if (!doc_) return 0;
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc_, &count);
    size_t total = 0;
    for (size_t i = 0; i < count; ++i) total += draws[i].index_count / 3;
    return static_cast<int>(total);
}

} // namespace weva_godot
