#include "weva/components.h"

#include <algorithm>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace weva {
namespace {

// The reference marks an expanded host with an attribute rather than a side
// table, so a re-expansion pass over the same document is a no-op. Kept
// byte-identical to ScopeMarkers.ExpandedAttribute's value.
constexpr const char* kExpandedAttribute = "data-uui-expanded";

std::string lower_ascii(std::string_view s) {
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

bool is_tag(const Node* n, const char* tag) {
    if (!n || n->node_type() != NodeType::Element) return false;
    return lower_ascii(static_cast<const Element*>(n)->tag_name()) == tag;
}

Ref<Node> clone_node(const Node* src) {
    if (src->node_type() == NodeType::Text) {
        const auto* t = static_cast<const TextNode*>(src);
        Ref<TextNode> copy = make_ref<TextNode>(t->data());
        // Ref(T*) ADOPTS; an upcast needs a retain or the clone is double-freed.
        return Ref<Node>::retain(copy.get());
    }
    if (src->node_type() != NodeType::Element) return nullptr;
    const auto* e = static_cast<const Element*>(src);
    Ref<Element> copy = make_ref<Element>(e->tag_name());
    const AttributeMap& attrs = e->attributes();
    for (std::size_t i = 0; i < attrs.size(); ++i) {
        copy->set_attribute(attrs.name_at(i), attrs.value_at(i));
    }
    for (const Ref<Node>& c : e->children()) {
        if (Ref<Node> cc = clone_node(c.get())) copy->append_child(cc.get());
    }
    return Ref<Node>::retain(copy.get());
}

struct Registry {
    // Tag name (lowercased) -> the `<template>` element that renders it.
    std::unordered_map<std::string, const Element*> templates;
};

// Registers every `<template id=...>` that is not itself inside a template, and
// collects them so they can be moved to the end of their parent's child list.
// That move is what the reference does so a depth-first search finds an
// expanded clone before the literal template body it was cloned from.
void walk_and_register(Node* node, bool in_template, Registry* reg,
                       std::vector<Element*>* top_level_templates) {
    // Snapshot: registration does not mutate, but expansion later will.
    std::vector<Ref<Node>> kids(node->children());
    for (const Ref<Node>& child : kids) {
        if (child->node_type() == NodeType::Element) {
            auto* e = static_cast<Element*>(child.get());
            const bool is_template = lower_ascii(e->tag_name()) == "template";
            if (is_template && !in_template) {
                const std::string_view id = e->get_attribute("id");
                if (!id.empty()) reg->templates[lower_ascii(id)] = e;
                top_level_templates->push_back(e);
            }
            walk_and_register(e, in_template || is_template, reg, top_level_templates);
        } else {
            walk_and_register(child.get(), in_template, reg, top_level_templates);
        }
    }
}

void collect_slots(Node* node, std::vector<Element*>* sink) {
    if (is_tag(node, "slot")) {
        // A slot's own children are its FALLBACK content, not a place to find
        // more slots — stop here so nested slots inside fallback are only
        // reached if that fallback is actually used.
        sink->push_back(static_cast<Element*>(node));
        return;
    }
    for (const Ref<Node>& c : node->children()) collect_slots(c.get(), sink);
}

int index_of_child(Node* parent, Node* child) {
    const std::vector<Ref<Node>>& kids = parent->children();
    for (std::size_t i = 0; i < kids.size(); ++i) {
        if (kids[i].get() == child) return static_cast<int>(i);
    }
    return -1;
}

// Replaces `slot` with `nodes` at the slot's own position among its siblings.
void replace_slot_with(Element* slot, const std::vector<Ref<Node>>& nodes) {
    Node* parent = slot->parent();
    if (!parent) return;
    // Hold a reference across the removal: the parent owns the only one, and
    // Ref(T*) adopts rather than retains.
    Ref<Element> keep_alive = Ref<Element>::retain(slot);
    const int at = index_of_child(parent, slot);
    Node* anchor = nullptr;
    if (at >= 0 && at + 1 < static_cast<int>(parent->children().size())) {
        anchor = parent->children()[at + 1].get();
    }
    parent->remove_child(slot);
    for (const Ref<Node>& n : nodes) {
        if (anchor) {
            parent->insert_before(n.get(), anchor);
        } else {
            parent->append_child(n.get());
        }
    }
}

// CSS-ish slotting: a light-dom child with `slot="name"` goes to the slot of
// that name, everything else to the unnamed slot. A slot with nothing to show
// falls back to its own children; a slot with neither disappears.
void project_slots(const std::vector<Ref<Node>>& cloned_roots,
                   const std::vector<Ref<Node>>& light_dom) {
    std::vector<Ref<Node>> default_children;
    std::unordered_map<std::string, std::vector<Ref<Node>>> named_children;
    for (const Ref<Node>& child : light_dom) {
        std::string slot;
        if (child->node_type() == NodeType::Element) {
            slot = std::string(static_cast<Element*>(child.get())->get_attribute("slot"));
        }
        if (slot.empty()) {
            default_children.push_back(child);
        } else {
            named_children[lower_ascii(slot)].push_back(child);
        }
    }

    std::vector<Element*> slots;
    for (const Ref<Node>& root : cloned_roots) collect_slots(root.get(), &slots);

    // A second slot with the same name cannot take the same nodes — appending
    // them again would re-parent them out of the first slot. It gets a clone.
    bool default_consumed = false;
    std::unordered_set<std::string> named_consumed;

    for (Element* slot : slots) {
        if (!slot->parent()) continue;
        const std::string name = lower_ascii(slot->get_attribute("name"));
        const std::vector<Ref<Node>>* source = nullptr;
        bool needs_clone = false;
        if (name.empty()) {
            if (!default_children.empty()) source = &default_children;
            needs_clone = default_consumed;
        } else {
            auto it = named_children.find(name);
            if (it != named_children.end() && !it->second.empty()) source = &it->second;
            needs_clone = named_consumed.count(name) != 0;
        }

        std::vector<Ref<Node>> payload;
        if (!source) {
            // Fallback content: the slot's own children, detached and put in
            // the slot's place.
            for (const Ref<Node>& c : slot->children()) payload.push_back(c);
            for (const Ref<Node>& c : payload) slot->remove_child(c.get());
        } else if (needs_clone) {
            for (const Ref<Node>& c : *source) {
                if (Ref<Node> cc = clone_node(c.get())) payload.push_back(cc);
            }
        } else {
            payload = *source;
            if (name.empty()) {
                default_consumed = true;
            } else {
                named_consumed.insert(name);
            }
        }
        replace_slot_with(slot, payload);
    }
}

void expand_node(Node* node, const Registry& reg, int depth, int max_depth);

void expand_children(Node* node, const Registry& reg, int depth, int max_depth) {
    // Snapshot, because expanding a child rewrites this child list.
    std::vector<Ref<Node>> kids(node->children());
    for (const Ref<Node>& child : kids) {
        expand_node(child.get(), reg, depth, max_depth);
    }
}

void expand_host(Element* host, const Element* tmpl, const Registry& reg, int depth,
                 int max_depth) {
    if (depth >= max_depth) return;  // template cycle; leave the host alone

    std::vector<Ref<Node>> light_dom(host->children());
    for (const Ref<Node>& c : light_dom) host->remove_child(c.get());

    std::vector<Ref<Node>> cloned_roots;
    for (const Ref<Node>& c : tmpl->children()) {
        if (Ref<Node> cc = clone_node(c.get())) cloned_roots.push_back(cc);
    }

    // Decided BEFORE projection mutates the clones: a template whose body root
    // repeats the HOST's tag is a render output, not another instance to
    // recurse into — `<template id="button"><button class="btn"><slot></slot>
    // </button></template>` renders a real <button>. Recursing would re-clone
    // the template inside itself until the depth guard fired. A BARE same-tag
    // clone with no attributes and no children is a genuine self-cycle, so it
    // is left for the guard.
    std::vector<Element*> self_referential;
    for (const Ref<Node>& r : cloned_roots) {
        if (r->node_type() != NodeType::Element) continue;
        auto* e = static_cast<Element*>(r.get());
        if (lower_ascii(e->tag_name()) == lower_ascii(host->tag_name()) &&
            (e->attributes().size() > 0 || !e->children().empty())) {
            self_referential.push_back(e);
        }
    }

    project_slots(cloned_roots, light_dom);

    for (const Ref<Node>& n : cloned_roots) host->append_child(n.get());
    host->set_attribute(kExpandedAttribute, "1");
    for (Element* e : self_referential) {
        if (!e->has_attribute(kExpandedAttribute)) e->set_attribute(kExpandedAttribute, "1");
    }

    for (const Ref<Node>& n : cloned_roots) {
        expand_node(n.get(), reg, depth + 1, max_depth);
    }
}

void expand_node(Node* node, const Registry& reg, int depth, int max_depth) {
    if (node->node_type() == NodeType::Element) {
        auto* e = static_cast<Element*>(node);
        const std::string tag = lower_ascii(e->tag_name());
        // A template's body is source, never rendered in place.
        if (tag == "template") return;
        auto it = reg.templates.find(tag);
        if (it != reg.templates.end() && !e->has_attribute(kExpandedAttribute)) {
            expand_host(e, it->second, reg, depth, max_depth);
            return;
        }
    }
    expand_children(node, reg, depth, max_depth);
}

} // namespace

void expand_components(Document* doc, int max_depth) {
    if (!doc || max_depth < 1) return;
    Registry reg;
    std::vector<Element*> top_level_templates;
    walk_and_register(doc, false, &reg, &top_level_templates);
    if (reg.templates.empty()) return;

    // Move each template to the end of its parent's children, in registration
    // order, so a depth-first search reaches expanded clones before the
    // literal template body they came from.
    for (Element* t : top_level_templates) {
        Node* parent = t->parent();
        if (!parent) continue;
        Ref<Element> keep_alive = Ref<Element>::retain(t);
        parent->remove_child(t);
        parent->append_child(t);
    }

    expand_children(doc, reg, 0, max_depth);
}

} // namespace weva
