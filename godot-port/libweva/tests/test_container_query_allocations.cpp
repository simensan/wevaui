// Query input refresh must reuse its storage once the document is warm.
#include "weva/container_query_state.h"
#include "weva/block_layout.h"
#include "weva/html.h"
#include <cstdio>
#include <cstdlib>
#include <new>
#include <map>
#include <memory>
#include <chrono>
using namespace weva;
namespace {
bool counting = false;
size_t allocations = 0;
}

void* operator new(size_t size) {
    if (counting) ++allocations;
    void* p = std::malloc(size ? size : 1);
    if (!p) std::abort();
    return p;
}
void* operator new[](size_t size) { return ::operator new(size); }
void* operator new(size_t size,const std::nothrow_t&) noexcept { return ::operator new(size); }
void* operator new[](size_t size,const std::nothrow_t&) noexcept { return ::operator new(size); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }
void operator delete(void* p,const std::nothrow_t&) noexcept { std::free(p); }
void operator delete[](void* p,const std::nothrow_t&) noexcept { std::free(p); }


struct Styles : StyleProvider {
    CascadeEngine engine;
    NullStateProvider state;
    std::map<const Element*,std::unique_ptr<ComputedStyle>> values;
    void walk(const Node& node,const ComputedStyle* parent=nullptr) {
        if (node.is_element()) {
            const auto& e=static_cast<const Element&>(node);
            auto style=std::make_unique<ComputedStyle>();
            engine.compute(e,state,parent,style.get());
            parent=style.get(); values[&e]=std::move(style);
        }
        for (const auto& child:node.children()) walk(*child,parent);
    }
    const ComputedStyle* style_of(const Element& e) override {
        const auto found=values.find(&e);
        return found==values.end() ? nullptr : found->second.get();
    }
};
int main() {
    SymbolTable symbols; ParseOptions options; options.strict=false; HtmlParseError he;
    std::string html="<div id=panel>";
    for (int i=0;i<200;++i) html+="<div class=item>HUD</div>";
    html+="</div>";
    auto doc=parse_html(html,&symbols,options,&he);
    if (!doc) return 1;
    Styles styles; Stylesheet sheet; CssParseError ce;
    if (!parse_stylesheet("#panel{display:block;container:Hud / inline-size;width:300px;font-size:20px}"
        "@container Hud (width>=15em){.item{color:red}}",false,&sheet,&ce)) return 1;
    styles.engine.add_stylesheet(&sheet,DeclarationOrigin::Author);
    ContainerQueryState queries; queries.attach(&styles.engine);
    styles.walk(*doc);
    LayoutContext context; MonoFontMetrics font; context.register_font("mono",&font);
    BoxTree tree; BoxBuilder builder(&tree,&styles); const auto root=builder.build_document(*doc);
    BlockLayout layout(&tree,context,&font); layout.layout_root(root,400,300);
    auto* panel=doc->get_element_by_id("panel"); BoxId panel_box=kNoBox;
    for (int i=0;i<tree.size();++i) if (tree[i].element==panel) {panel_box=i;break;}
    if (panel_box==kNoBox) return 1;
    for (int i=0;i<4;++i) {
        tree[panel_box].width=i%2 ? 200 : 300;
        queries.refresh(*doc,tree,root,styles,context);
    }
    counting=true;
    styles.values[panel]->set("--allocation-counter-positive-control","set");
    counting=false;
    bool valid=allocations>0; allocations=0;
    const auto start=std::chrono::steady_clock::now();
    counting=true;
    for (int i=0;i<1000;++i) {
        tree[panel_box].width=i%2 ? 200 : 300;
        valid &= queries.refresh(*doc,tree,root,styles,context);
        valid &= !queries.refresh(*doc,tree,root,styles,context);
    }
    counting=false;
    const double us=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count()/2000;
    std::printf("container queries: 2000 refreshes, 200 descendants, %zu allocations, %.2f us/refresh, %s\n",
        allocations,us,valid ? "correct" : "WRONG");
    return valid && allocations==0 ? 0 : 1;
}
