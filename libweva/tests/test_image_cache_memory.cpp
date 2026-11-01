#include "weva_c.h"
#include "weva/image_store.h"
#include <map>
#include <chrono>
#include <thread>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstdio>
#include <cstdint>
#include <new>
#include <string>
#include <cstring>
#include <array>
#include <cstddef>
#include "tracked_allocations.h"
using weva::test_allocations::live;
using weva::test_allocations::allocations;

static int checks = 0, failures = 0;
static void check(bool passed, const char* what) {
    ++checks;
    if (!passed) { ++failures; std::printf("FAIL %s\n", what); }
}


#include "image_cache_fixtures.inc"
struct Reader {
    std::map<std::string, int> calls;
    bool blue = false;
};
static size_t read_image(void* data, const char* path, uint8_t* bytes, size_t capacity) {
    auto& reader = *static_cast<Reader*>(data);
    ++reader.calls[path];
    const bool blue = reader.blue && (std::strcmp(path, "sprite0") == 0 ||
        std::strcmp(path, "bg0") == 0 || std::strcmp(path, "border0") == 0);
    if (reader.blue && std::strcmp(path, "mask0") == 0) {
        if (bytes && capacity >= sizeof(k_transparent)) std::memcpy(bytes, k_transparent, sizeof(k_transparent));
        return sizeof(k_transparent);
    }
    const auto* source = blue ? k_blue : k_red;
    const size_t size = blue ? sizeof(k_blue) : sizeof(k_red);
    if (bytes && capacity >= size) std::memcpy(bytes, source, size);
    return size;
}
static weva_texture sprite_texture(weva_document_t doc, float x = 0, float y = 0) {
    size_t draws = 0; const auto* list = weva_document_draws(doc, &draws);
    uint64_t id = 0;
    for (size_t i = 0; i < draws && !id; ++i) {
        if (!list[i].texture_id) continue;
        for (size_t v = 0; v < list[i].vertex_count; ++v) {
            const auto& vertex = list[i].vertices[v];
            if (vertex.x >= x && vertex.x < x + 8.01f && vertex.y >= y && vertex.y < y + 8.01f) {
                id = list[i].texture_id; break;
            }
        }
    }
    size_t textures = 0; const auto* views = weva_document_textures(doc, &textures);
    for (size_t i = 0; i < textures; ++i) if (views[i].id == id) return views[i];
    return {};
}
static void test_store_policy() {
    using weva::ImageStore;
    std::map<std::string, int> reads;
    ImageStore store;
    store.set_reader([&](const std::string& path, std::vector<uint8_t>* bytes) {
        ++reads[path]; bytes->assign(k_red, k_red + sizeof(k_red)); return true;
    });
    const auto* a = store.get("a");
    const auto a_version = a ? a->resource_version : 0;
    const auto* b = store.get("b");
    const auto b_version = b ? b->resource_version : 0;
    store.get("c"); store.get("a"); store.get("d");
    store.trim_unused({}, ImageStore::kCacheBytes, 3);
    check(store.size() == 3, "entry budget evicts the least recently used source");
    check(store.get("a") && reads["a"] == 1, "recent inactive source is reused");
    b = store.get("b");
    check(b && b->resource_version != b_version && reads["b"] == 2, "evicted source has a fresh resource identity");
    store.trim_unused({"a", "b"}, 1, 1);
    check(store.size() == 2 && store.retained_bytes() > 1, "live working set is retained above a soft budget");
    check(store.get("a") && store.get("a")->resource_version == a_version, "pinned borrowed source stays valid");
    const auto retained = store.retained_bytes();
    ImageStore moved(std::move(store));
    check(moved.retained_bytes() == retained && store.retained_bytes() == 0, "move transfers accounting and clears the source");
    store.set_reader([](const std::string&, std::vector<uint8_t>*) { return false; });
    check(!store.get("reused") && store.missing_count() == 1, "moved-from store can accept a new reader");
    moved.clear(); store.clear();
    check(moved.retained_bytes() == 0 && store.retained_bytes() == 0, "clear releases charged cache storage");

    int live_fail_reads = 0;
    ImageStore failures;
    failures.set_base_path("assets");
    failures.set_reader([&](const std::string& path, std::vector<uint8_t>*) {
        if (path == "assets/live-missing") ++live_fail_reads;
        return false;
    });
    failures.get("live-missing");
    for (int i = 0; i < 256; ++i) {
        failures.get("missing" + std::to_string(i));
        if (i % 8 == 7) failures.trim_unused({"assets/live-missing"}, ImageStore::kCacheBytes, 8);
    }
    check(failures.size() == 8 && failures.missing_count() == 8, "negative cache history obeys entry budget");
    check(!failures.get("live-missing") && live_fail_reads == 1, "live failure stays cached without repeated IO");
    const auto missing = failures.missing();
    check(std::find(missing.begin(), missing.end(), "assets/live-missing") != missing.end(), "live missing URL remains diagnosable");

    ImageStore pending;
    int pending_reads = 0;
    pending.set_reader([&](const std::string&, std::vector<uint8_t>*) { ++pending_reads; return false; });
    for (int i = 0; i < 1100; ++i) pending.get("obsolete" + std::to_string(i), true);
    pending.get("current", true);
    check(pending.needs_trim() && pending_reads == 0, "URL admission alone triggers metadata pressure before reading");
    pending.trim_unused({"current"}, ImageStore::kCacheBytes, 1);
    check(pending.pending() && pending_reads == 0, "trim retains only current deferred request without IO");
    const auto pending_bytes = pending.retained_bytes();
    ImageStore moved_pending(std::move(pending));
    check(!pending.pending() && pending.retained_bytes() == 0 && moved_pending.retained_bytes() == pending_bytes,
          "move transfers deferred URLs and accounting");
    pending = std::move(moved_pending);
    check(!moved_pending.pending() && moved_pending.retained_bytes() == 0, "move assignment releases source state");
    pending.poll();
    check(!pending.pending() && pending_reads == 1 && pending.missing_count() == 1,
          "obsolete queued URLs never invoke host reader and pending accounting drains");
    pending.clear();
    check(pending.retained_bytes() == 0, "pending URL accounting resets cleanly");

    ImageStore async;
    async.set_reader([](const std::string&, std::vector<uint8_t>* bytes) {
        bytes->assign(k_red, k_red + sizeof(k_red)); return true;
    });
    async.get("sampled", true);
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (async.pending() && std::chrono::steady_clock::now() < deadline) {
        async.poll(); std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    const auto* sampled = async.get("sampled");
    check(sampled && !sampled->block_sums.empty(), "deferred image has prepared sampling storage");
    const auto version = sampled ? sampled->resource_version : 0;
    if (sampled) check(async.retained_bytes() >= sampled->rgba.capacity() + sampled->block_sums.capacity() * sizeof(uint64_t),
                       "budget includes prepared sampling capacity");
    async.trim_unused({}, 1, 1);
    check(async.size() == 0 && async.retained_bytes() == 0, "eviction releases pixels and sampling tables together");
    sampled = async.get("sampled");
    check(sampled && sampled->resource_version != version, "reload after async eviction has a fresh identity");
}
int main() {
    const auto before_nothrow = live.load();
    void* ordinary = ::operator new(257, std::nothrow);
    check(ordinary && live.load() == before_nothrow + 257, "nothrow allocation is tracked");
    ::operator delete(ordinary, std::nothrow);
    void* aligned = ::operator new[](257, std::align_val_t{64}, std::nothrow);
    check(aligned && reinterpret_cast<uintptr_t>(aligned) % 64 == 0 && live.load() == before_nothrow + 257,
          "aligned nothrow allocation uses the tracking header");
    ::operator delete[](aligned, std::align_val_t{64}, std::nothrow);
    check(live.load() == before_nothrow, "all nothrow allocation forms balance on deletion");
    Reader reader;
    weva_config config{};
    config.viewport_width = 128; config.viewport_height = 64; config.use_user_agent_stylesheet = 1;
    const auto doc = weva_document_create(&config);
    check(doc != nullptr, "create image-cache document");
    if (!doc) return 1;
    check(weva_document_set_asset_reader(doc, read_image, &reader) == WEVA_OK, "install image reader");
    const std::string html =
        "<body style='margin:0'>"
        "<img id='changing' src='sprite0' style='position:absolute;left:0;top:0;width:8px;height:8px'>"
        "<img src='pinned' style='position:absolute;left:20px;top:0;width:8px;height:8px'>"
        "<div style='position:absolute;left:40px;top:0;width:8px;height:8px;background-image:url(background)'></div>"
        "<div style='position:absolute;left:60px;top:0;width:8px;height:8px;border:2px solid transparent;border-image-source:url(border);border-image-slice:1 fill'></div>"
        "<div style='position:absolute;left:80px;top:0;width:8px;height:8px;background:red;mask-image:url(mask)'></div>"
        "<ul style='position:absolute;left:500px;top:0'><li style='list-style-image:url(marker)'>x</li></ul>"
        "<div id='bg-changing' style='position:absolute;left:0;top:20px;width:8px;height:8px;background-image:url(bg0)'></div>"
        "<div id='border-changing' style='position:absolute;left:40px;top:20px;width:8px;height:8px;border:2px solid transparent;border-image-source:url(border0);border-image-slice:1 fill'></div>"
        "<div id='mask-changing' style='position:absolute;left:20px;top:20px;width:8px;height:8px;background:red;mask-image:url(mask0)'></div>"
        "</body>";
    check(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK, "load live image consumers");
    check(weva_document_update(doc, 0) == WEVA_OK, "publish initial image frame");
    const auto changing = weva_document_query(doc, "#changing");
    const auto old_texture = sprite_texture(doc);
    check(old_texture.rgba && old_texture.rgba[0] > 200 && old_texture.rgba[2] < 20, "initial image is red");
    const auto serial = weva_document_draw_serial(doc);
    const auto bg_target = weva_document_query(doc, "#bg-changing");
    const auto border_target = weva_document_query(doc, "#border-changing");
    const auto mask_target = weva_document_query(doc, "#mask-changing");
    for (const char* url : {"pinned", "background", "border", "mask", "marker"})
        check(reader.calls[url] == 2, "each live image source is read once");
    const auto idle_allocations = allocations.load();
    for (int i = 0; i < 128; ++i)
        check(weva_document_update(doc, 0) == WEVA_OK, "settled image HUD stays usable");
    check(allocations.load() == idle_allocations, "under-budget settled image updates allocate nothing");
    for (auto target : {bg_target, border_target, mask_target})
        check(weva_element_set_attribute(doc, target, "style", "display:none") == WEVA_OK, "retire a textured consumer");
    check(weva_document_update_geometry(doc) == WEVA_OK, "hide old background/border/mask without repainting");
    const size_t warm = live.load();
    for (int i = 1; i <= 640; ++i) {
        const std::string url = "sprite" + std::to_string(i);
        check(weva_element_set_attribute(doc, changing, "src", url.c_str()) == WEVA_OK, "change image source");
        check(weva_document_update_geometry(doc) == WEVA_OK, "layout-only image churn");
    }
    const size_t after = live.load();
    check(after <= warm + 64 * 1024 * 1024 + 256 * 1024, "inactive decoded sources obey the 64 MiB cache budget");
    check(weva_document_draw_serial(doc) == serial, "cache trimming does not publish a geometry-only frame");
    check(old_texture.rgba && old_texture.rgba[0] > 200, "published texture pixels survive decoded-source eviction");
    std::printf("image cache: warm %zu bytes, after 640 source swaps %zu bytes, added %zu bytes\n", warm, after, after - warm);
    reader.blue = true;
    check(weva_element_set_attribute(doc, changing, "src", "sprite0") == WEVA_OK, "return to evicted source URL");
    check(weva_element_set_attribute(doc, bg_target, "style", "position:absolute;left:0;top:20px;width:8px;height:8px;background-image:url(bg0)") == WEVA_OK, "restore background consumer");
    check(weva_element_set_attribute(doc, border_target, "style", "position:absolute;left:40px;top:20px;width:8px;height:8px;border:2px solid transparent;border-image-source:url(border0);border-image-slice:1 fill") == WEVA_OK, "restore border consumer");
    check(weva_element_set_attribute(doc, mask_target, "style", "position:absolute;left:20px;top:20px;width:8px;height:8px;background:red;mask-image:url(mask0)") == WEVA_OK, "restore mask consumer");
    check(weva_document_update(doc, 0) == WEVA_OK, "publish reloaded source");
    const auto replacement = sprite_texture(doc);
    check(reader.calls["sprite0"] == 4, "evicted source is fetched again");
    check(replacement.rgba && replacement.rgba[0] < 20 && replacement.rgba[2] > 200, "reloaded source cannot reuse stale filtered pixels");
    for (const char* url : {"bg0", "border0", "mask0"})
        check(reader.calls[url] == 4, "retired textured source reloads after eviction");
    for (float x : {0.f, 40.f}) {
        const auto texture = sprite_texture(doc, x, 20);
        check(texture.rgba && texture.rgba[0] < 20 && texture.rgba[2] > 200,
              "background/border reload cannot reuse old raster pixels");
    }
    const auto mask = sprite_texture(doc, 20, 20);
    check(mask.rgba && mask.rgba[3] == 0, "mask reload invalidates old opaque raster pixels");
    weva_document_set_viewport(doc, 129, 64);
    check(weva_document_update(doc, 0) == WEVA_OK, "repaint live resource consumers");
    for (const char* url : {"pinned", "background", "border", "mask", "marker"})
        check(reader.calls[url] == 2, "live sources remain decoded under cache pressure");
    weva_document_destroy(doc);
    test_store_policy();
    std::printf("image cache memory: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
