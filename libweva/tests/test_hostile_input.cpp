#include "check.h"
#include "weva/at_import.h"
#include "weva/css_rule.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/image_decode.h"
#include "weva/selector.h"
#include "weva_c.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

using namespace weva;

// Markup and stylesheets a page author -- or a mod, or a save file -- controls.
// Each of these crashed or hung the engine: unbounded recursion on nesting the
// input chooses, or work exponential or cubic in its size. The checks pin the
// bound; the sanitizer build is what shows the stack survives.

namespace {

weva_document_t load(const std::string& css, const std::string& html) {
    weva_config cfg{};
    cfg.viewport_width = 400;
    cfg.viewport_height = 300;
    cfg.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&cfg);
    if (!css.empty()) weva_document_add_css(d, css.data(), css.size());
    weva_document_load_html(d, html.data(), html.size());
    return d;
}

std::string computed(weva_document_t d, const char* selector, const char* property) {
    char buf[256] = {};
    weva_element_computed_style(d, weva_document_query(d, selector), property, buf, sizeof(buf));
    return buf;
}

size_t tree_depth(const Node& root) {
    size_t deepest = 0;
    struct Frame { const Node* n; size_t depth; };
    std::vector<Frame> stack{{&root, 0}};
    while (!stack.empty()) {
        const Frame f = stack.back();
        stack.pop_back();
        if (f.depth > deepest) deepest = f.depth;
        for (const auto& c : f.n->children()) stack.push_back({c.get(), f.depth + 1});
    }
    return deepest;
}

// LSB-first bit writer for hand-built DEFLATE streams.
struct Bits {
    std::vector<uint8_t> bytes;
    int used = 8;
    void put(uint32_t value, int count) {
        for (int i = 0; i < count; ++i) {
            if (used == 8) { bytes.push_back(0); used = 0; }
            if (value & (1u << i)) bytes.back() |= static_cast<uint8_t>(1u << used);
            ++used;
        }
    }
    // Huffman codes go most significant bit first.
    void code(uint32_t value, int count) {
        for (int i = count - 1; i >= 0; --i) put((value >> i) & 1u, 1);
    }
};

} // namespace

// Past 512 open elements, new nodes attach to the current node's parent, as in
// Chrome: the markup stays whole and the tree stays 512 deep.
void test_hostile_html_depth() {
    std::string html;
    for (int i = 0; i < 200000; ++i) html += "<div>";
    html += "deep";
    SymbolTable symbols;
    ParseOptions opts;
    opts.strict = false;
    HtmlParseError err;
    Ref<Document> doc = parse_html(html, &symbols, opts, &err);
    CHECK(static_cast<bool>(doc));
    if (doc) {
        const size_t depth = tree_depth(*doc);
        CHECK(depth > 500);
        CHECK(depth <= 515);
    }
    doc = Ref<Document>();   // destruction recursed once per level

    // Shallow markup is untouched by the limit.
    Ref<Document> small = parse_html("<div><p><b>x</b></p></div>", &symbols, opts, &err);
    CHECK(static_cast<bool>(small) && tree_depth(*small) == 6);

    // And the whole pipeline renders the capped tree.
    std::string page;
    for (int i = 0; i < 3000; ++i) page += "<div>";
    page += "deep";
    weva_document_t d = load("", page);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    weva_document_destroy(d);
}

// Every text token and formatting tag reconstructs the formatting list; with
// ten thousand open `<b>`s that was ten thousand stack scans per token.
void test_hostile_formatting_elements() {
    std::string html;
    for (int i = 0; i < 5000; ++i) html += "<b>x";
    SymbolTable symbols;
    ParseOptions opts;
    opts.strict = false;
    HtmlParseError err;
    // Cubic, this did not finish; there is no clock check, which a
    // sanitizer build on a busy machine would make flaky.
    Ref<Document> doc = parse_html(html, &symbols, opts, &err);
    CHECK(static_cast<bool>(doc));
}

// `:is(` nested past the limit drops the rule instead of recursing without end.
void test_hostile_selector_nesting() {
    std::string deep;
    for (int i = 0; i < 50000; ++i) deep += ":is(";
    deep += "p";
    for (int i = 0; i < 50000; ++i) deep += ")";
    CompiledSelector compiled;
    SelectorParseError err;
    CHECK(!parse_selector(deep, &compiled, &err));
    // Realistic nesting still parses.
    CHECK(parse_selector(":is(:not(:where(.a, :is(.b))))", &compiled, &err));

    weva_document_t d = load(deep + "{color:red}", "<p id=p>x</p>");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    weva_document_destroy(d);
}

// Generic function and parenthesis nesting, and long calc() chains.
void test_hostile_value_nesting() {
    std::string fn = "#a{width:";
    for (int i = 0; i < 100000; ++i) fn += "f(";
    fn += "}";
    std::string parens = "#a{width:";
    for (int i = 0; i < 100000; ++i) parens += "(";
    parens += "}";
    std::string chain = "#a{width:calc(1px";
    for (int i = 0; i < 100000; ++i) chain += " + 1px";
    chain += ")}";
    for (const std::string* css : {&fn, &parens, &chain}) {
        weva_document_t d = load(*css, "<div id=a>x</div>");
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        weva_document_destroy(d);
    }
    // A chain of reasonable length still computes.
    std::string ten = "#a{width:calc(1px";
    for (int i = 0; i < 9; ++i) ten += " + 1px";
    ten += ")}";
    weva_document_t d = load(ten, "<div id=a>x</div>");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    double w = 0;
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), nullptr, nullptr, &w, nullptr) == WEVA_OK);
    CHECK(w == 10);
    weva_document_destroy(d);
}

// Each custom property doubles the one before; thirty of them asked for a
// gigabyte per element. Past Chrome's 2 MiB the value is invalid.
void test_hostile_var_expansion() {
    std::string css = ":root{--a0:xxxxxxxx;";
    for (int i = 1; i <= 30; ++i) {
        css += "--a" + std::to_string(i) + ":var(--a" + std::to_string(i - 1) + ")var(--a" +
               std::to_string(i - 1) + ");";
    }
    css += "}#a{font-family:var(--a30)}#b{font-family:var(--a3)}";
    weva_document_t d = load(css, "<div id=a>x</div><div id=b>y</div>");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    // Small expansions are unaffected.
    CHECK(computed(d, "#b", "font-family") == std::string(64, 'x'));
    weva_document_destroy(d);
}

// nth coefficients past int saturate instead of overflowing.
void test_hostile_nth_coefficients() {
    CompiledSelector compiled;
    SelectorParseError err;
    for (const char* s : {":nth-child(-2147483648n)", ":nth-child(n-2147483648)",
                          ":nth-child(99999999999n+99999999999)", ":nth-child(-99999999999n)",
                          ":nth-child(2147483647n-2147483647)"}) {
        CHECK(parse_selector(s, &compiled, &err));
    }
    weva_document_t d = load(":nth-child(-99999999999n+2){color:red}"
                             ":nth-child(n-99999999999){color:blue}",
                             "<p>1</p><p>2</p><p>3</p>");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    weva_document_destroy(d);
    const NthExpression extreme{-2147483647 - 1, -2147483647 - 1};
    CHECK(!extreme.matches(1));
    const NthExpression largest{2147483647, 1};
    CHECK(largest.matches(1));
}

// A DEFLATE stream of long back-references stops at the caller's limit.
void test_hostile_inflate_limit() {
    Bits b;
    b.put(1, 1);        // final block
    b.put(1, 2);        // fixed Huffman
    b.code(0x30 + 'a', 8);
    for (int i = 0; i < 100000; ++i) {
        b.code(0xC0 + (285 - 280), 8);   // length 258
        b.code(0, 5);                    // distance 1
    }
    b.code(0, 7);       // end of block
    std::vector<uint8_t> out;
    CHECK(inflate_deflate(b.bytes.data(), b.bytes.size(), &out, 4096));
    CHECK(out.size() == 4096);
    CHECK(out.back() == 'a');
    out.clear();
    CHECK(inflate_deflate(b.bytes.data(), b.bytes.size(), &out));
    CHECK(out.size() == 1 + 258u * 100000);
}

// An import graph that fans out is bounded in total, not only in depth.
void test_hostile_import_fanout() {
    Stylesheet sheet;
    CssParseError err;
    std::string root;
    for (int i = 0; i < 20; ++i) root += "@import \"a1.css\";";
    CHECK(parse_stylesheet(root, false, &sheet, &err));
    int loads = 0;
    const int resolved = expand_imports(&sheet, [&](std::string_view url, std::string* css) {
        ++loads;
        const int level = std::atoi(std::string(url.substr(1)).c_str());
        css->clear();
        for (int i = 0; i < 20; ++i) *css += "@import \"a" + std::to_string(level + 1) + ".css\";";
        *css += "p{color:red}";
        return true;
    });
    CHECK(loads <= 300);
    CHECK(resolved <= 300);
    CHECK(resolved > 0);
}

// `&#0;` is U+FFFD, not a NUL that cuts the value short.
void test_hostile_null_character_reference() {
    weva_document_t d = load("", "<p id=p title='a&#0;b'>c&#0;d</p>");
    weva_document_update(d, 0);
    char buf[32] = {};
    const size_t n = weva_element_attribute(d, weva_document_query(d, "#p"), "title", buf, sizeof(buf));
    CHECK(n == 5);
    CHECK(std::string(buf) == "a\xEF\xBF\xBD" "b");
    weva_document_destroy(d);
}
