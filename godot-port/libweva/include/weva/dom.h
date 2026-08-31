#pragma once
#include "weva/ref.h"
#include "weva/status.h"

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

// Ports Runtime/Dom/{Node,Element,TextNode,Document,AttributeMap}.cs.
//
// Two C# behaviours are load-bearing and preserved exactly:
//   - Every node carries a monotonic Version. The whole invalidation
//     architecture keys caches on input versions rather than dirty bits, which
//     is why a :hover flip costs 0.08ms against 8.3ms for a full cascade.
//   - Mutations bubble to every ancestor, with target always the originally
//     mutated node. Observers on the Document therefore see the whole subtree.
//
// Errors return Status where C# threw (docs/CONVENTIONS.md).

namespace weva {

class Node;
class Element;
class Document;

// Explicit node tag instead of dynamic_cast: docs/CONVENTIONS.md forbids the
// core from depending on RTTI, and a tag is cheaper than a type-info walk on a
// hot tree traversal anyway.
enum class NodeType { Document, Element, Text };

enum class MutationKind {
    ChildAdded, ChildRemoved, TextChanged,
    AttributeAdded, AttributeChanged, AttributeRemoved,
};

struct DomMutation {
    MutationKind kind;
    Node* target = nullptr;      // the node that changed, never the bubbler
    Node* related = nullptr;     // added/removed child, when applicable
    std::string name;            // attribute name
    std::string old_value;
    std::string new_value;
};

// Insertion-ordered, name-canonicalised (ASCII-lowercased) attribute store.
class AttributeMap {
public:
    using ChangeFn = std::function<void(std::string_view name,
                                        const std::string* old_value,
                                        const std::string* new_value)>;

    std::size_t size() const { return order_.size(); }
    bool contains(std::string_view name) const;
    // Empty string when absent; use contains() to distinguish.
    std::string_view get(std::string_view name) const;
    void set(std::string_view name, std::string_view value);
    bool remove(std::string_view name);

    std::string_view name_at(std::size_t i) const { return order_[i]; }
    std::string_view value_at(std::size_t i) const;

    void set_change_handler(ChangeFn fn) { on_changed_ = std::move(fn); }

private:
    static std::string canonicalize(std::string_view s);

    std::vector<std::string> order_;
    std::unordered_map<std::string, std::string> values_;
    ChangeFn on_changed_;
};

class Node : public RefCounted {
public:
    NodeType node_type() const { return type_; }
    bool is_element() const { return type_ == NodeType::Element; }

    Node* parent() const { return parent_; }
    Document* owner_document() const { return owner_document_; }
    const std::vector<Ref<Node>>& children() const { return children_; }
    int64_t version() const { return version_; }

    Status append_child(Node* child);
    Status insert_before(Node* child, Node* reference_child);
    bool remove_child(Node* child);

    // Observers fire after the mutation is applied. Registered per node;
    // a handler on the Document sees its whole subtree.
    void add_observer(std::function<void(const DomMutation&)> fn) {
        observers_.push_back(std::move(fn));
    }

protected:
    explicit Node(NodeType t) : type_(t) {}
    void bump_version() { ++version_; }
    void raise_bubbling(const DomMutation& m);
    void set_owner_document(Document* d) { owner_document_ = d; }

private:
    friend class Document;
    static void propagate_owner_document(Node* n);
    bool has_ancestor(const Node* candidate) const;
    int index_of(const Node* child) const;

    NodeType type_;
    std::vector<Ref<Node>> children_;
    std::vector<std::function<void(const DomMutation&)>> observers_;
    Node* parent_ = nullptr;               // raw: children own parents' lifetime, not vice versa
    Document* owner_document_ = nullptr;
    int64_t version_ = 0;
};

class TextNode : public Node {
public:
    explicit TextNode(std::string_view data)
        : Node(NodeType::Text), data_(data), source_(data_) {}

    const std::string& data() const { return data_; }
    void set_data(std::string_view value);

    // Parse-time raw text. Survives set_data so the binding scanner can still
    // find `{{ }}` markers after the rendered value has replaced them.
    const std::string& source() const { return source_; }

private:
    std::string data_;
    std::string source_;
};

class Element : public Node {
public:
    explicit Element(std::string_view tag_name);

    const std::string& tag_name() const { return tag_name_; }
    AttributeMap& attributes() { return attributes_; }
    const AttributeMap& attributes() const { return attributes_; }

    std::string_view get_attribute(std::string_view name) const { return attributes_.get(name); }
    void set_attribute(std::string_view name, std::string_view value) { attributes_.set(name, value); }
    bool remove_attribute(std::string_view name) { return attributes_.remove(name); }
    bool has_attribute(std::string_view name) const { return attributes_.contains(name); }

    std::string_view id() const { return attributes_.get("id"); }
    std::string_view class_name() const { return attributes_.get("class"); }
    std::vector<std::string_view> class_list() const;

private:
    void on_attribute_changed(std::string_view name, const std::string* old_v, const std::string* new_v);

    std::string tag_name_;
    AttributeMap attributes_;
};

class Document : public Node {
public:
    Document() : Node(NodeType::Document) { set_owner_document(this); }

    Element* get_element_by_id(std::string_view id);
    std::vector<Element*> get_elements_by_tag_name(std::string_view tag);
    std::vector<Element*> get_elements_by_class_name(std::string_view cls);
};

// Whitespace-separated class-token membership, allocation-free.
bool has_class_token(std::string_view class_attr, std::string_view token);

} // namespace weva
