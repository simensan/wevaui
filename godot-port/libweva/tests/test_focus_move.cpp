// Directional focus movement -- the gamepad question a tab order cannot answer.
#include "check.h"
#include "weva_c.h"

#include <string>

namespace {

// Driven through the C ABI rather than the C++ internals: focus movement
// reads laid-out geometry, and the ABI is the one path that assembles the
// cascade, the boxes and the focus state the same way a host does.
struct Doc {
    weva_document_t doc = nullptr;
    explicit Doc(const char* html, const char* css, int w = 400, int h = 300) {
        weva_config c{};
        c.viewport_width = w;
        c.viewport_height = h;
        c.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&c);
        weva_document_add_css(doc, css, std::char_traits<char>::length(css));
        weva_document_load_html(doc, html, std::char_traits<char>::length(html));
        weva_document_update(doc, 0);
    }
    ~Doc() { weva_document_destroy(doc); }

    void focus(const char* selector) {
        weva_document_set_focus(doc, weva_document_query(doc, selector));
    }
    std::string move(double dx, double dy) {
        const weva_element_t e = weva_document_focus_move(doc, dx, dy);
        if (e == WEVA_ELEMENT_NONE) return "";
        char id[64] = {0};
        weva_element_attribute(doc, e, "id", id, sizeof(id));
        return id;
    }
};

} // namespace

void test_focus_move() {
    // The grid, by id:   a b c
    //                    d e g
    //                    h i j
    const char* kCss =
        "#grid { display: grid; grid-template-columns: repeat(3, 80px); gap: 10px; }"
        " button { width: 80px; height: 40px; }";
    const char* kHtml =
        "<body><div id=grid>"
        "<button id=a>a</button><button id=b>b</button><button id=c>c</button>"
        "<button id=d>d</button><button id=e>e</button><button id=g>g</button>"
        "<button id=h>h</button><button id=i>i</button><button id=j>j</button>"
        "</div></body>";
    Doc f(kHtml, kCss);

    f.focus("#e");
    CHECK_EQ(f.move(-1, 0), std::string("d"));
    f.focus("#e");
    CHECK_EQ(f.move(1, 0), std::string("g"));
    f.focus("#e");
    CHECK_EQ(f.move(0, -1), std::string("b"));
    f.focus("#e");
    CHECK_EQ(f.move(0, 1), std::string("i"));

    // Straight down from a corner, not diagonally to whatever happens to
    // be nearest below-right. This is what the off-axis penalty is for,
    // and without it the cursor walks diagonally across a grid.
    f.focus("#a");
    CHECK_EQ(f.move(0, 1), std::string("d"));
    f.focus("#c");
    CHECK_EQ(f.move(0, 1), std::string("g"));
    f.focus("#a");
    CHECK_EQ(f.move(1, 0), std::string("b"));

    // Past the edge STAYS. A menu that wraps from its last row to its
    // first under a held stick is worse than one that stops.
    f.focus("#a");
    CHECK_EQ(f.move(-1, 0), std::string("a"));
    CHECK_EQ(f.move(0, -1), std::string("a"));
    f.focus("#j");
    CHECK_EQ(f.move(1, 0), std::string("j"));
    CHECK_EQ(f.move(0, 1), std::string("j"));

    // Nothing focused: the first press picks something up rather than
    // doing nothing, which is what opening a screen should feel like.
    weva_document_set_focus(f.doc, WEVA_ELEMENT_NONE);
    CHECK(!f.move(0, 1).empty());

    // A full-width button above a row of three: "down" lands on the one
    // directly BELOW where you were, which for a centred origin is the middle.
    //
    // The first version of this asserted the leftmost, which was a guess
    // rather than a rule -- and it was inline-block, so the exact columns
    // depended on collapsed whitespace. Flex with stated widths makes the
    // three columns 0-100, 100-200 and 200-300, so the wide button's centre
    // at 150 is unambiguously inside the second.
    const char* kRowsCss =
        "#rows { width: 300px; } .wide { display: block; width: 300px; height: 30px; }"
        " .row { display: flex; } .narrow { width: 100px; height: 30px; }";
    const char* kRowsHtml =
        "<body><div id=rows>"
        "<button class=wide id=top>top</button>"
        "<div class=row>"
        "<button class=narrow id=n1>1</button><button class=narrow id=n2>2</button>"
        "<button class=narrow id=n3>3</button>"
        "</div>"
        "</div></body>";
    Doc r(kRowsHtml, kRowsCss);
    r.focus("#top");
    CHECK_EQ(r.move(0, 1), std::string("n2"));   // the column under its centre
    r.focus("#n3");
    CHECK_EQ(r.move(0, -1), std::string("top"));
    r.focus("#n1");
    CHECK_EQ(r.move(1, 0), std::string("n2"));
}
