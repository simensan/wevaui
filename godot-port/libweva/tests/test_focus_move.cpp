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
    for (int mode=0; mode<4; ++mode) {
        Doc skipped("<div id=panel tabindex=0><input id=field value=abc></div>", "");
        const auto d=skipped.doc;
        const auto panel=weva_document_query(d,"#panel"), field=weva_document_query(d,"#field");
        skipped.focus("#field");
        weva_element_set_style(d,mode==2 ? field : panel,"content-visibility",mode==3 ? "auto" : "hidden");
        if (mode==1) weva_element_set_style(d,field,"content-visibility","visible");
        const bool available=mode>=2;
        CHECK(weva_document_set_focus(d,field)==(available ? WEVA_OK : WEVA_ERR_INVALID_STATE));
        weva_document_update(d,0);
        CHECK(weva_document_focus(d)==(available ? field : WEVA_ELEMENT_NONE));
        CHECK(weva_document_text_input_target(d)==(mode==3 ? field : WEVA_ELEMENT_NONE));
        weva_document_text_input(d,"x");
        char value[16]{};
        weva_element_value(d,field,value,sizeof(value));
        CHECK_EQ(std::string(value),mode==3 ? std::string("xabc") : std::string("abc"));
        CHECK(weva_document_set_focus(d,panel)==WEVA_OK);
    }
    {
        Doc skipped("<div id=panel><dialog id=dialog><button>Confirm</button></dialog></div>", "");
        const auto d=skipped.doc;
        const auto panel=weva_document_query(d,"#panel"), dialog=weva_document_query(d,"#dialog");
        weva_element_show_dialog(d,dialog,1); weva_document_update(d,0);
        weva_element_set_style(d,panel,"content-visibility","hidden"); weva_document_update(d,0);
        double x=0,y=0,w=0,h=0;
        CHECK(weva_element_bounds(d,dialog,&x,&y,&w,&h)!=WEVA_OK || h==0);
        CHECK(weva_document_focus(d)==WEVA_ELEMENT_NONE);
    }
    for (int modal=0; modal<2; ++modal) for (int mode=0; mode<4; ++mode) {
        Doc shown("<button id=before>Open</button><button id=outside>Outside</button>"
                  "<dialog id=dialog><button id=first>First</button><button id=second>Second</button></dialog>", "");
        const auto d = shown.doc;
        const auto dialog = weva_document_query(d,"#dialog"), first = weva_document_query(d,"#first");
        const auto before = weva_document_query(d,"#before"), outside = weva_document_query(d,"#outside");
        if (mode==1) weva_element_set_attribute(d,first,"inert","");
        if (mode==2) weva_element_set_style(d,first,"display","none");
        shown.focus("#before");
        CHECK(weva_element_show_dialog(d,dialog,modal)==WEVA_OK);
        CHECK(weva_document_focus(d)==weva_document_query(d,(mode==1 || mode==2) ? "#second" : "#first"));
        if (mode==3) weva_document_set_focus(d,outside);
        weva_element_close_dialog(d,dialog);
        CHECK(weva_document_focus(d)==((mode==3 && !modal) ? outside : before));
    }
    {
        Doc blocked("<input id=current value=abcdef><input id=blocked inert value=x>", "");
        const auto d=blocked.doc;
        const auto current=weva_document_query(d,"#current");
        weva_element_set_selection(d,current,1,4);
        CHECK(weva_element_set_selection(d,weva_document_query(d,"#blocked"),0,0)==WEVA_ERR_INVALID_STATE);
        int start=0,end=0;
        weva_element_selection(d,current,&start,&end);
        CHECK(start==1 && end==4 && weva_document_focus(d)==current);
    }
    {
        Doc inert("<button id=under>Under</button><div id=panel><button id=field>Above</button></div>",
                  "button{position:absolute;left:0;top:0;width:100px;height:40px}#panel{position:absolute;left:0;top:0;width:100px;height:40px;z-index:2;pointer-events:none}#field{pointer-events:auto}");
        const auto d = inert.doc;
        const auto panel = weva_document_query(d,"#panel"), field = weva_document_query(d,"#field");
        const auto under = weva_document_query(d,"#under");
        inert.focus("#field");
        CHECK(weva_document_element_at(d,20,20)==field);
        weva_document_set_pointer(d,20,20,WEVA_BUTTON_PRIMARY);
        weva_element_set_attribute(d,panel,"inert","false"); // Boolean: presence wins.
        CHECK(weva_document_set_focus(d,field)==WEVA_ERR_INVALID_STATE);
        weva_document_update(d,0);
        CHECK(weva_document_focus(d)==WEVA_ELEMENT_NONE);
        CHECK(weva_document_element_at(d,20,20)==under);
        weva_document_set_pointer(d,20,20,0);
        weva_event event{};
        bool clicked = false;
        while (weva_document_poll_event(d,&event))
            if (event.kind == WEVA_EVENT_CLICK) clicked = true;
        CHECK(!clicked); // Inertness cancels the press instead of activating underneath.
        weva_element_set_attribute(d,panel,"inert",nullptr);
        weva_document_update(d,0);
        CHECK(weva_document_focus(d)==WEVA_ELEMENT_NONE);
        CHECK(weva_document_set_focus(d,field)==WEVA_OK);
        CHECK(weva_document_element_at(d,20,20)==field);
    }
    for (int modal=0; modal<2; ++modal) {
        Doc inert("<div inert><dialog id=dialog><button id=inside>Confirm</button></dialog>"
                  "<div popover=manual id=popup><button id=popbutton>Action</button></div></div>", "");
        const auto d = inert.doc;
        const auto dialog = weva_document_query(d,"#dialog"), inside = weva_document_query(d,"#inside");
        if (modal) {
            CHECK(weva_element_show_dialog(d,dialog,1)==WEVA_OK);
            weva_document_update(d,0);
            CHECK(weva_document_set_focus(d,inside)==WEVA_OK);
            weva_element_set_attribute(d,dialog,"inert","");
            weva_document_update(d,0);
            CHECK(weva_document_focus(d)==WEVA_ELEMENT_NONE);
            CHECK(weva_document_set_focus(d,inside)==WEVA_ERR_INVALID_STATE);
        } else {
            weva_element_show_popover(d,weva_document_query(d,"#popup"));
            weva_document_update(d,0);
            CHECK(weva_document_set_focus(d,weva_document_query(d,"#popbutton"))==WEVA_ERR_INVALID_STATE);
        }
    }
    for (int mode=0; mode<5; ++mode) {
        Doc hidden("<div id=panel><input id=field value=abc></div><aside id=status>Status</aside>",
                   "#status{width:73px}#panel:focus-within + #status{width:37px}");
        const auto d = hidden.doc;
        const auto panel = weva_document_query(d,"#panel"), field = weva_document_query(d,"#field");
        hidden.focus("#field"); weva_document_update(d,0);
        if (mode==0) weva_element_set_style(d,panel,"display","none");
        if (mode==1 || mode==2) weva_element_set_style(d,panel,"visibility","hidden");
        if (mode==2) weva_element_set_style(d,field,"visibility","visible");
        if (mode==3) weva_element_set_attribute(d,panel,"hidden","");
        if (mode==4) weva_element_set_style(d,panel,"opacity","0");
        weva_document_update(d,0);
        const bool available = mode==2 || mode==4;
        CHECK(weva_document_focus(d)==(available ? field : WEVA_ELEMENT_NONE));
        char width[32]{};
        weva_element_computed_style(d,weva_document_query(d,"#status"),"width",width,sizeof(width));
        CHECK_EQ(std::string(width),available ? std::string("37px") : std::string("73px"));
    }
    for (int mode=0; mode<4; ++mode) {
        Doc restored("<button id=game>Inventory</button><dialog id=first><input id=one value=one></dialog>"
                     "<dialog id=second><input id=two value=two></dialog>", "");
        const auto d = restored.doc;
        const auto first = weva_document_query(d, "#first"), second = weva_document_query(d, "#second");
        const auto one = weva_document_query(d, "#one"), two = weva_document_query(d, "#two");
        restored.focus("#game");
        weva_element_show_dialog(d, first, 1); weva_element_show_dialog(d, second, 1);
        if (mode == 1) weva_element_close_dialog(d, first);
        if (mode == 2) weva_element_set_attribute(d, one, "disabled", "");
        if (mode == 3) weva_element_set_style(d, one, "display", "none");
        CHECK(weva_document_focus(d) == two);
        weva_element_close_dialog(d, second);
        CHECK(weva_document_focus(d) == (mode == 0 ? one : two));
        weva_document_update(d, 0);
        CHECK(weva_document_focus(d) == (mode == 0 ? one : WEVA_ELEMENT_NONE));
    }
    {
        Doc m("<button id=behind>Inventory</button><dialog id=modal><button id=inside>Confirm</button></dialog>"
              "<dialog id=second><button id=other autofocus>Apply</button></dialog>",
              "html,body{margin:0}#behind{position:absolute;left:10px;top:10px;width:100px;height:40px}"
              "dialog{position:fixed;left:200px;top:100px;width:150px;height:100px;margin:0;padding:0}");
        const auto behind = weva_document_query(m.doc, "#behind");
        const auto modal = weva_document_query(m.doc, "#modal");
        const auto inside = weva_document_query(m.doc, "#inside");
        const auto second = weva_document_query(m.doc, "#second");
        const auto other = weva_document_query(m.doc, "#other");
        m.focus("#behind");
        CHECK(weva_element_show_dialog(m.doc, modal, 1) == WEVA_OK);
        CHECK(weva_document_focus(m.doc) == inside);
        weva_document_update(m.doc, 0);
        CHECK(weva_document_set_focus(m.doc, behind) == WEVA_ERR_INVALID_STATE);
        CHECK(weva_document_focus(m.doc) == inside);
        CHECK(weva_document_element_at(m.doc, 30, 30) == modal);
        CHECK(weva_document_focus_next(m.doc, 0) == inside);
        CHECK(weva_document_focus_next(m.doc, 1) == inside);
        CHECK(weva_document_focus_move(m.doc, -1, 0) == inside);
        CHECK(weva_element_show_dialog(m.doc, second, 1) == WEVA_OK);
        CHECK(weva_document_focus(m.doc) == other);
        weva_document_update(m.doc, 0);
        CHECK(weva_document_set_focus(m.doc, inside) == WEVA_ERR_INVALID_STATE);
        CHECK(weva_document_element_at(m.doc, 30, 30) == second);
        CHECK(weva_element_close_dialog(m.doc, second) == WEVA_OK);
        CHECK(weva_document_focus(m.doc) == inside);
        CHECK(weva_element_close_dialog(m.doc, modal) == WEVA_OK);
        CHECK(weva_document_focus(m.doc) == behind);
        weva_document_update(m.doc, 0);
        CHECK(weva_document_element_at(m.doc, 30, 30) == behind);
        // Last opened wins even when it occurs first in DOM order.
        weva_element_show_dialog(m.doc, second, 1);
        weva_element_show_dialog(m.doc, modal, 1);
        weva_document_update(m.doc, 0);
        double x = 0, y = 0, w = 0, h = 0;
        CHECK(weva_element_bounds(m.doc, inside, &x, &y, &w, &h) == WEVA_OK);
        CHECK(weva_document_element_at(m.doc, x+w/2, y+h/2) == inside);
        weva_document_set_viewport(m.doc, 401, 300);
        weva_document_set_viewport(m.doc, 400, 300);
        weva_document_update(m.doc, 0);
        CHECK(weva_document_element_at(m.doc, x+w/2, y+h/2) == inside);
    }
    {
        Doc promoted("<div id=clip><dialog id=modal><button id=inside>Confirm</button></dialog></div>"
                     "<div id=cover>Ordinary overlay</div>",
                     "#clip{transform:translate(90px,80px);opacity:0;overflow:hidden;width:10px;height:10px}"
                     "#cover{position:fixed;inset:0;z-index:2147483647;background:red}"
                     "dialog{position:fixed;left:200px;top:100px;width:150px;height:100px;margin:0;padding:0}");
        const auto modal = weva_document_query(promoted.doc, "#modal");
        const auto inside = weva_document_query(promoted.doc, "#inside");
        weva_element_show_dialog(promoted.doc, modal, 1);
        weva_document_update(promoted.doc, 0);
        double x = 0, y = 0, w = 0, h = 0;
        CHECK(weva_element_bounds(promoted.doc, modal, &x, &y, &w, &h) == WEVA_OK);
        CHECK(x == 200 && y == 100);
        CHECK(weva_element_bounds(promoted.doc, inside, &x, &y, &w, &h) == WEVA_OK);
        CHECK(weva_document_element_at(promoted.doc, x+w/2, y+h/2) == inside);
    }
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
