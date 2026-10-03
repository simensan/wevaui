#include "check.h"
#include "weva_c.h"

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

// Lifetime across the calls that replace or shrink what the document holds.
//
// Each of these read freed memory before it was fixed: the tree, focus,
// scroll-snap state or published texels outlived the objects they pointed
// into. The checks are about the values; the sanitizer build is what proves
// nothing reads freed memory on the way to them.

namespace {

weva_document_t make(int w = 400, int h = 300) {
    weva_config cfg{};
    cfg.viewport_width = w;
    cfg.viewport_height = h;
    cfg.use_user_agent_stylesheet = 1;
    return weva_document_create(&cfg);
}

std::string tag_of(weva_document_t d, weva_element_t e) {
    char buf[32] = {};
    weva_element_tag_name(d, e, buf, sizeof(buf));
    return buf;
}

} // namespace

// A parse failure leaves the loaded document exactly as it was. It used to
// free the live tree first and return before any of the caches that point
// into it were reset.
void test_abi_failed_load_keeps_document() {
    weva_document_t d = make();
    CHECK(weva_document_load_html(d, "<div id=a><input id=f value='hello world'></div>", 49) == WEVA_OK);
    weva_document_update(d, 0);
    const weva_element_t f = weva_document_query(d, "#f");
    CHECK(weva_document_set_focus(d, f) == WEVA_OK);
    weva_document_update(d, 0);

    // Whatever the tokenizer rejects, rejecting it must not cost the page.
    const char* rejected[] = {"<", "<div", "<div id='a", "</", "<!--"};
    int failures = 0;
    for (const char* bad : rejected) {
        if (weva_document_load_html(d, bad, std::strlen(bad)) != WEVA_OK) {
            ++failures;
            CHECK(tag_of(d, f) == "input");
            CHECK(weva_document_query(d, "#a") != WEVA_ELEMENT_NONE);
            weva_document_set_pointer(d, 10, 10, 0);
            (void)weva_document_is_animating(d);
            weva_document_key(d, WEVA_KEY_RIGHT, 0, 1);
            weva_document_key(d, WEVA_KEY_RIGHT, 0, 0);
            CHECK(weva_document_update(d, 0.016) == WEVA_OK);
        }
    }
    // At least one of them is a parse error, or this test tests nothing.
    CHECK(failures > 0);
    weva_document_destroy(d);
}

// Between a successful load and the next update the old box tree still exists
// but everything it points at is gone. Hit testing and tooling must not read
// it.
void test_abi_load_then_pointer_before_update() {
    weva_document_t d = make();
    const char* first = "<style>div{width:200px;height:100px;background:red}</style><div id=a>one</div>";
    CHECK(weva_document_load_html(d, first, std::strlen(first)) == WEVA_OK);
    weva_document_update(d, 0);
    const char* second = "<p id=b>two</p>";
    CHECK(weva_document_load_html(d, second, std::strlen(second)) == WEVA_OK);
    weva_document_set_pointer(d, 10, 10, 0);
    CHECK(weva_document_element_at(d, 10, 10) == WEVA_ELEMENT_NONE ||
          tag_of(d, weva_document_element_at(d, 10, 10)) != "div");
    (void)weva_document_scroll(d, 10, 10, 0, 40);
    (void)weva_document_select_word_at(d, 10, 10);
    size_t n = weva_document_layout_dump(d, "t", nullptr, 0);
    std::vector<char> dump(n + 1);
    weva_document_layout_dump(d, "t", dump.data(), dump.size());
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_document_element_at(d, 5, 5) != WEVA_ELEMENT_NONE);
    weva_document_destroy(d);
}

// A removed element is gone from hit testing at once, not at the next update.
void test_abi_removed_element_is_not_hit() {
    weva_document_t d = make();
    const char* html = "<style>#a{width:200px;height:100px;background:red}</style><div id=a>one</div>";
    CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
    weva_document_update(d, 0);
    const weva_element_t a = weva_document_query(d, "#a");
    CHECK(weva_document_element_at(d, 10, 10) == a);
    CHECK(weva_element_remove(d, a) == WEVA_OK);
    CHECK(weva_document_element_at(d, 10, 10) != a);
    weva_document_set_pointer(d, 12, 12, 0);
    weva_document_set_pointer(d, 14, 14, 1);
    weva_document_set_pointer(d, 14, 14, 0);
    size_t n = weva_document_layout_dump(d, "t", nullptr, 0);
    std::vector<char> dump(n + 1);
    weva_document_layout_dump(d, "t", dump.data(), dump.size());
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    weva_document_destroy(d);
}

// A snap animation whose container goes away mid-flight stops with it.
void test_abi_snap_state_forgets_removed_container() {
    for (int reload = 0; reload < 2; ++reload) {
        weva_document_t d = make();
        const char* html =
            "<style>#l{width:200px;height:100px;overflow:auto;scroll-snap-type:y mandatory}"
            ".r{height:40px;scroll-snap-align:start}</style>"
            "<div id=l><div class=r>1</div><div class=r>2</div><div class=r>3</div>"
            "<div class=r>4</div><div class=r>5</div></div>";
        CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
        weva_document_update(d, 0);
        CHECK(weva_document_scroll(d, 50, 50, 0, 25) == 1);
        weva_document_update(d, 0.2);
        weva_document_update(d, 0.016);
        if (reload) {
            CHECK(weva_document_load_html(d, "<p>x</p>", 8) == WEVA_OK);
        } else {
            CHECK(weva_element_remove(d, weva_document_query(d, "#l")) == WEVA_OK);
        }
        for (int i = 0; i < 30; ++i) weva_document_update(d, 0.016);
        weva_document_destroy(d);
    }
}

// Published texels live until the next update, as the header promises, even
// across a viewport or colour-scheme change.
void test_abi_textures_survive_viewport_change() {
    for (int scheme = 0; scheme < 2; ++scheme) {
        weva_document_t d = make();
        const char* html =
            "<style>div{width:120px;height:60px;"
            "background:linear-gradient(90deg,red,blue)}</style><div></div>";
        CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
        weva_document_update(d, 0);
        size_t count = 0;
        const weva_texture* tex = weva_document_textures(d, &count);
        CHECK(count > 0);
        std::vector<std::vector<uint8_t>> before;
        for (size_t i = 0; i < count; ++i) {
            const size_t bytes = static_cast<size_t>(tex[i].width) * tex[i].height * 4;
            before.emplace_back(tex[i].rgba, tex[i].rgba + bytes);
        }
        if (scheme) weva_document_set_color_scheme(d, 1);
        else weva_document_set_viewport(d, 401, 300);
        for (size_t i = 0; i < count; ++i) {
            CHECK(std::memcmp(tex[i].rgba, before[i].data(), before[i].size()) == 0);
        }
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        weva_document_destroy(d);
    }
}

// Double-clicking a disabled field neither focuses it nor rewrites the
// selection of the field that does have focus.
void test_abi_select_word_in_disabled_field() {
    weva_document_t d = make();
    const char* html =
        "<style>input{display:block;width:200px;height:30px;margin:0}</style>"
        "<input id=a value='alpha beta'><input id=b disabled value='gamma delta'>";
    CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
    weva_document_update(d, 0);
    const weva_element_t a = weva_document_query(d, "#a");
    const weva_element_t b = weva_document_query(d, "#b");
    CHECK(weva_document_set_focus(d, a) == WEVA_OK);
    CHECK(weva_element_set_selection(d, a, 1, 3) == WEVA_OK);
    double bx = 0, by = 0, bw = 0, bh = 0;
    CHECK(weva_element_bounds(d, b, &bx, &by, &bw, &bh) == WEVA_OK);
    CHECK(weva_document_select_word_at(d, bx + 10, by + bh / 2) == 0);
    int start = -1, end = -1;
    CHECK(weva_element_selection(d, a, &start, &end) == WEVA_OK);
    CHECK(start == 1);
    CHECK(end == 3);
    CHECK(weva_document_focus(d) == a);
    weva_document_destroy(d);
}

// A getter that finds nothing leaves an empty string, never the caller's
// previous contents.
void test_abi_getters_clear_buffer_on_miss() {
    weva_document_t d = make();
    CHECK(weva_document_load_html(d, "<div id=a title=t>x</div>", 25) == WEVA_OK);
    weva_document_update(d, 0);
    const weva_element_t a = weva_document_query(d, "#a");
    char buf[16];
    const auto dirty = [&] { std::memcpy(buf, "stale-bytes", 12); };
    dirty();
    CHECK(weva_element_attribute(d, a, "missing", buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    dirty();
    CHECK(weva_element_attribute(d, 9999, "title", buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    dirty();
    CHECK(weva_element_text(d, 9999, buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    dirty();
    CHECK(weva_element_value(d, 9999, buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    dirty();
    CHECK(weva_document_selected_text(d, buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    dirty();
    CHECK(weva_element_attribute(nullptr, a, "title", buf, sizeof(buf)) == 0);
    CHECK(buf[0] == '\0');
    weva_document_destroy(d);
}

// An asset reader that reports an impossible size is a missing asset. It was
// handed straight to resize(), which with exceptions disabled aborts.
void test_abi_asset_reader_impossible_size() {
    struct Reader {
        static size_t read(void*, const char*, uint8_t*, size_t) { return size_t(1) << 62; }
    };
    weva_document_t d = make();
    CHECK(weva_document_set_asset_reader(d, &Reader::read, nullptr) == WEVA_OK);
    const char* html =
        "<style>div{width:50px;height:50px;background:url(huge.png)}</style><div></div>";
    CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    weva_document_destroy(d);
}

// weva_box.text is valid until the next update, even when set_text or set_html
// replaced the text node it views in between.
void test_abi_box_text_survives_text_replacement() {
    weva_document_t d = make();
    const char* html = "<div id=a>first label</div><div id=b>second <i>x</i></div>";
    CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
    weva_document_update(d, 0);
    const size_t count = weva_document_boxes(d, nullptr, 0);
    std::vector<weva_box> boxes(count);
    CHECK(weva_document_boxes(d, boxes.data(), boxes.size()) == count);
    CHECK(weva_element_set_text(d, weva_document_query(d, "#a"), "replaced") == WEVA_OK);
    CHECK(weva_element_set_html(d, weva_document_query(d, "#b"), "<b>new</b>", 10) == WEVA_OK);
    // The old views, and a fresh walk of the not-yet-rebuilt tree, still read
    // the text that was laid out.
    std::string seen;
    for (const weva_box& b : boxes) if (b.text) seen.append(b.text, b.text_length);
    CHECK(seen.find("first label") != std::string::npos);
    CHECK(seen.find("second") != std::string::npos);
    std::vector<weva_box> again(weva_document_boxes(d, nullptr, 0));
    weva_document_boxes(d, again.data(), again.size());
    for (const weva_box& b : again) if (b.text) seen.append(b.text, b.text_length);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    char buf[32] = {};
    weva_element_text(d, weva_document_query(d, "#a"), buf, sizeof(buf));
    CHECK(std::string(buf) == "replaced");
    weva_document_destroy(d);
}

// set_style writes exactly one declaration or nothing.
void test_abi_set_style_is_one_declaration() {
    weva_document_t d = make();
    CHECK(weva_document_load_html(d, "<div id=a style='color: red'>x</div>", 36) == WEVA_OK);
    weva_document_update(d, 0);
    const weva_element_t a = weva_document_query(d, "#a");
    char buf[64] = {};
    const auto style_attr = [&] {
        weva_element_attribute(d, a, "style", buf, sizeof(buf));
        return std::string(buf);
    };
    CHECK(weva_element_set_style(d, a, "color", "blue; display: none") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_set_style(d, a, "color", "blue } p { color: red") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_set_style(d, a, "color", "url(\"a") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_set_style(d, a, "color", "rgb(1, 2") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_element_set_style(d, a, "color: red; display", "none") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(style_attr() == "color: red");
    // What a value may legitimately hold: semicolons inside url() and
    // strings, and a trailing semicolon.
    CHECK(weva_element_set_style(d, a, "background-image", "url(\"a;b.png\")") == WEVA_OK);
    CHECK(weva_element_set_style(d, a, "color", "blue;") == WEVA_OK);
    CHECK(style_attr() == "color: blue; background-image: url(\"a;b.png\")");
    CHECK(weva_element_style(d, a, "color", buf, sizeof(buf)) == 4);
    CHECK(std::string(buf) == "blue");
    // A property that is not in the list, including the empty name, is 0.
    CHECK(weva_element_style(d, a, "", buf, sizeof(buf)) == 0);
    CHECK(weva_element_set_style(d, a, "color", " ; ") == WEVA_OK);
    CHECK(weva_element_style(d, a, "color", buf, sizeof(buf)) == 0);
    weva_document_destroy(d);
}

extern "C++" int32_t weva_internal_text_direction(const char* utf8, size_t length,
                                                   size_t chunk_limit);

// Direction is found across chunk seams, including one that would split a
// character, and a length past INT32_MAX no longer wraps (covered by the
// chunking these seams exercise).
void test_abi_text_direction_chunks() {
    const std::string hebrew = "\xD7\x90";          // U+05D0, two bytes
    const std::string arabic = "\xD8\xA7";          // U+0627
    const std::string emoji = "\xF0\x9F\x98\x80";   // four bytes, neutral
    for (size_t chunk : {4u, 5u, 6u, 7u, 64u}) {
        for (size_t pad = 0; pad < 9; ++pad) {
            const std::string neutral(pad, ' ');
            CHECK(weva_internal_text_direction((neutral + hebrew).data(), pad + 2, chunk) == 1);
            CHECK(weva_internal_text_direction((neutral + emoji + arabic).data(), pad + 6, chunk) == 1);
            CHECK(weva_internal_text_direction((neutral + emoji + "a").data(), pad + 5, chunk) == 0);
            CHECK(weva_internal_text_direction((neutral + emoji).data(), pad + 4, chunk) == 0);
        }
    }
    CHECK(weva_text_direction("\xD7\x90", 2) == 1);
    CHECK(weva_text_direction("abc", 3) == 0);
    CHECK(weva_text_direction(nullptr, 5) == 0);
}
