#include "weva/container_query_state.h"
#include <algorithm>

namespace weva {
namespace {
bool ws(char c) { return c==' ' || c=='\t' || c=='\n' || c=='\r' || c=='\f'; }
bool token(std::string_view list, std::string_view word, bool insensitive=false) {
    while (!list.empty()) {
        while (!list.empty() && ws(list.front())) list.remove_prefix(1);
        const auto end=list.find_first_of(" \t\r\n\f");
        const auto part=list.substr(0,end);
        bool equal=part.size()==word.size();
        for (size_t i=0; equal && i<part.size(); ++i) {
            char c=part[i];
            if (insensitive && c>='A' && c<='Z') c+='a'-'A';
            equal=c==word[i];
        }
        if (equal) return true;
        if (end==std::string_view::npos) break;
        list.remove_prefix(end);
    }
    return false;
}
}

void ContainerQueryState::attach(CascadeEngine* engine) {
    engine_=engine;
    generation_=0;
    results_.clear(); boxes_.clear(); changed_.clear();
    engine_->set_container_provider(this);
}

bool ContainerQueryState::matches(const Element& element,size_t query) const {
    if (!engine_ || generation_!=engine_->container_generation()) return false;
    const auto it=results_.find(&element);
    return it!=results_.end() && query<it->second.values.size() && it->second.values[query];
}

uint64_t ContainerQueryState::version(const Element& element) const {
    if (!engine_ || generation_!=engine_->container_generation()) return 0;
    const auto it=results_.find(&element);
    return it==results_.end() ? 0 : it->second.signature;
}

void ContainerQueryState::index(const BoxTree& tree,BoxId id) {
    if (!tree.valid(id)) return;
    const auto& box=tree[id];
    if (box.element && box.kind!=BoxKind::Text && box.kind!=BoxKind::Line) {
        auto& input=boxes_[box.element];
        if (input.seen!=epoch_) input={id,epoch_};
    }
    for (auto child:tree.children(id)) index(tree,child);
}

bool ContainerQueryState::evaluate(const Element& element,const CompiledContainerQuery& query,
    const BoxTree& tree,StyleProvider& styles,const LayoutContext& context) const {
    for (const Node* ancestor=element.parent(); ancestor; ancestor=ancestor->parent()) {
        if (!ancestor->is_element()) continue;
        const auto& e=static_cast<const Element&>(*ancestor);
        const auto* style=styles.style_of(e);
        if (!style) continue;
        if (!query.name.empty() && !token(style->get("container-name"),query.name)) continue;
        const bool vertical=style->get("writing-mode").find("vertical")!=std::string_view::npos ||
            style->get("writing-mode").find("sideways")!=std::string_view::npos;
        const auto type=style->get("container-type");
        const uint8_t axes=token(type,"size",true) ? 3 : token(type,"inline-size",true) ? (vertical ? 2 : 1) : 0;
        const auto required=query.condition.required_axes(vertical);
        if (!axes || (axes&required)!=required) continue;
        const auto found=boxes_.find(&e);
        // A selected container with no principal box has unknown size. Do not
        // substitute the dimensions of a more distant ancestor.
        if (found==boxes_.end()) return false;
        const auto& box=tree[found->second.id];
        if (box.kind!=BoxKind::Block) return false;
        ContainerSizeContext c;
        c.width=box.content_width(); c.height=box.content_height(); c.vertical=vertical;
        const double font_size=font_size_px(style,style->inherit_parent(),context);
        c.lengths=context.to_length_context(font_size, std::nullopt,
                                            line_height_px(style,font_size,context));
        c.lengths.root_font_size_px=root_font_size_;
        c.lengths.root_line_height_px=root_line_height_;
        return query.condition.evaluate(c)==QueryTruth::True;
    }
    return false;
}

void ContainerQueryState::visit(const Node& node,bool covered,const BoxTree& tree,
    StyleProvider& styles,const LayoutContext& context) {
    if (node.is_element()) {
        const auto& element=static_cast<const Element&>(node);
        auto& result=results_[&element];
        result.seen=epoch_;
        const auto& queries=engine_->container_queries();
        result.values.resize(queries.size(),0);
        bool changed=false, any=false;
        uint64_t signature=14695981039346656037ULL;
        for (size_t i=0; i<queries.size(); ++i) {
            const uint8_t next=evaluate(element,queries[i],tree,styles,context);
            changed|=next!=result.values[i]; any|=next!=0;
            result.values[i]=next;
            signature^=next; signature*=1099511628211ULL;
        }
        // A signature of the complete query inputs permits reuse when a panel
        // crosses back over a breakpoint, without accumulating cache entries
        // for every toggle. Identical result sets may safely share matches.
        result.signature=any ? signature : 0;
        if (changed && !covered) { changed_.push_back(&element); covered=true; }
    }
    for (const auto& child:node.children()) visit(*child,covered,tree,styles,context);
}

bool ContainerQueryState::refresh(const Document& document,const BoxTree& tree,BoxId root,
    StyleProvider& styles,const LayoutContext& context) {
    changed_.clear();
    if (generation_!=engine_->container_generation()) {
        generation_=engine_->container_generation(); results_.clear();
    }
    if (engine_->container_queries().empty()) return false;
    ++epoch_;
    index(tree,root);
    for (auto it=boxes_.begin(); it!=boxes_.end();) {
        if (it->second.seen!=epoch_) it=boxes_.erase(it); else ++it;
    }
    root_font_size_=context.root_font_size_px;
    root_line_height_=context.root_line_height_px;
    // rem uses the document root's computed font, rather than the default used
    // while that root itself was being resolved.
    for (const auto& child:document.children()) {
        if (!child->is_element()) continue;
        if (static_cast<const Element&>(*child).tag_name()!="html") break;
        const auto found=boxes_.find(static_cast<const Element*>(child.get()));
        if (found!=boxes_.end()) {
            const auto* style=tree[found->second.id].style;
            root_font_size_=font_size_px(style,nullptr,context);
            root_line_height_=line_height_px(style,root_font_size_,context);
        }
        break;
    }
    visit(document,false,tree,styles,context);
    for (auto it=results_.begin(); it!=results_.end();) {
        if (it->second.seen!=epoch_) it=results_.erase(it); else ++it;
    }
    return !changed_.empty();
}
} // namespace weva
