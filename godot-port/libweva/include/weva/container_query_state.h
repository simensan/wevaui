#pragma once
#include "weva/cascade.h"
#include "weva/box_builder.h"
#include "weva/style_resolver.h"
#include <unordered_map>

namespace weva {

// Layout-owned query inputs. Refresh after layout, then restyle changed_roots()
// through the normal style-diff/invalidation path. No refresh on idle frames.
class ContainerQueryState : public ContainerQueryProvider {
public:
    void attach(CascadeEngine* engine);
    bool refresh(const Document& document, const BoxTree& tree, BoxId root,
                 StyleProvider& styles, const LayoutContext& context);
    const std::vector<const Element*>& changed_roots() const { return changed_; }
    size_t element_count() const { return results_.size(); }
    bool matches(const Element& element, size_t query) const override;
    uint64_t version(const Element& element) const override;
private:
    struct Result {
        std::vector<uint8_t> values;
        uint64_t signature = 0, seen = 0;
    };
    void index(const BoxTree& tree, BoxId root);
    void visit(const Node& node, bool covered, const BoxTree& tree,
               StyleProvider& styles, const LayoutContext& context);
    bool evaluate(const Element& element, const CompiledContainerQuery& query,
                  const BoxTree& tree, StyleProvider& styles, const LayoutContext& context) const;
    CascadeEngine* engine_ = nullptr;
    uint64_t generation_ = 0, epoch_ = 0;
    double root_font_size_ = 16, root_line_height_ = 0;
    struct BoxInput { BoxId id=kNoBox; uint64_t seen=0; };
    std::unordered_map<const Element*, BoxInput> boxes_;
    std::unordered_map<const Element*, Result> results_;
    std::vector<const Element*> changed_;
};
} // namespace weva
