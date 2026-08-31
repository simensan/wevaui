#include "weva/dom.h"

#include <algorithm>
#include <cctype>

namespace weva {

// ---------------------------------------------------------------- AttributeMap

std::string AttributeMap::canonicalize(std::string_view s) {
    // ASCII-lowercase. The C# side returns the original instance when it is
    // already lowercase to dodge an allocation; here the copy is unavoidable
    // because we own the key, so the fast path only skips the transform.
    bool needs = false;
    for (char c : s) {
        if (c >= 'A' && c <= 'Z') { needs = true; break; }
    }
    std::string out(s);
    if (needs) {
        for (char& c : out) {
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        }
    }
    return out;
}

bool AttributeMap::contains(std::string_view name) const {
    return values_.find(canonicalize(name)) != values_.end();
}

std::string_view AttributeMap::get(std::string_view name) const {
    auto it = values_.find(canonicalize(name));
    return it == values_.end() ? std::string_view{} : std::string_view(it->second);
}

std::string_view AttributeMap::value_at(std::size_t i) const {
    auto it = values_.find(order_[i]);
    return it == values_.end() ? std::string_view{} : std::string_view(it->second);
}

void AttributeMap::set(std::string_view name, std::string_view value) {
    std::string key = canonicalize(name);
    auto it = values_.find(key);
    if (it != values_.end()) {
        if (it->second == value) return;   // no-op writes must not bump versions
        std::string old = it->second;
        it->second = std::string(value);
        if (on_changed_) {
            std::string nv = it->second;
            on_changed_(key, &old, &nv);
        }
        return;
    }
    order_.push_back(key);
    values_.emplace(key, std::string(value));
    if (on_changed_) {
        std::string nv{value};
        on_changed_(key, nullptr, &nv);
    }
}

bool AttributeMap::remove(std::string_view name) {
    std::string key = canonicalize(name);
    auto it = values_.find(key);
    if (it == values_.end()) return false;
    std::string old = it->second;
    values_.erase(it);
    order_.erase(std::remove(order_.begin(), order_.end(), key), order_.end());
    if (on_changed_) on_changed_(key, &old, nullptr);
    return true;
}

// ------------------------------------------------------------------------ Node

int Node::index_of(const Node* child) const {
    for (std::size_t i = 0; i < children_.size(); ++i) {
        if (children_[i].get() == child) return static_cast<int>(i);
    }
    return -1;
}

bool Node::has_ancestor(const Node* candidate) const {
    for (const Node* n = parent_; n != nullptr; n = n->parent_) {
        if (n == candidate) return true;
    }
    return false;
}

void Node::raise_bubbling(const DomMutation& m) {
    for (Node* n = this; n != nullptr; n = n->parent_) {
        // Copy: an observer may detach itself or mutate the tree.
        auto snapshot = n->observers_;
        for (auto& fn : snapshot) fn(m);
    }
}

void Node::propagate_owner_document(Node* n) {
    for (auto& c : n->children_) {
        c->owner_document_ = n->owner_document_;
        propagate_owner_document(c.get());
    }
}

Status Node::append_child(Node* child) {
    if (!child) return Status::NotFound;
    if (child == this) return Status::OutOfRange;      // cannot append to itself
    if (has_ancestor(child)) return Status::OutOfRange; // cannot append an ancestor

    if (child->parent_ == this) {
        int old = index_of(child);
        if (old < 0) return Status::NotFound;
        if (old == static_cast<int>(children_.size()) - 1) return Status::Ok;  // already last
        Ref<Node> keep = children_[old];
        children_.erase(children_.begin() + old);
        children_.push_back(keep);
        bump_version();
        child->bump_version();
        raise_bubbling(DomMutation{MutationKind::ChildAdded, this, child, {}, {}, {}});
        return Status::Ok;
    }

    // Hold a reference across the unlink: remove_child drops the parent's only
    // reference, and without this the node would be freed mid-move.
    Ref<Node> keep = Ref<Node>::retain(child);
    if (child->parent_) child->parent_->remove_child(child);

    child->parent_ = this;
    child->owner_document_ = owner_document_;
    children_.push_back(keep);
    propagate_owner_document(child);
    bump_version();
    child->bump_version();
    raise_bubbling(DomMutation{MutationKind::ChildAdded, this, child, {}, {}, {}});
    return Status::Ok;
}

Status Node::insert_before(Node* child, Node* reference_child) {
    if (!child) return Status::NotFound;
    if (!reference_child) return append_child(child);
    if (reference_child->parent_ != this) return Status::NotFound;
    if (child == reference_child) return Status::Ok;
    if (child == this) return Status::OutOfRange;
    if (has_ancestor(child)) return Status::OutOfRange;

    if (child->parent_ == this) {
        int old = index_of(child);
        int ref_idx = index_of(reference_child);
        if (old < 0 || ref_idx < 0) return Status::NotFound;
        if (old == ref_idx || old + 1 == ref_idx) return Status::Ok;  // already in place

        Ref<Node> keep = children_[old];
        children_.erase(children_.begin() + old);
        if (old < ref_idx) --ref_idx;
        children_.insert(children_.begin() + ref_idx, keep);
        bump_version();
        child->bump_version();
        raise_bubbling(DomMutation{MutationKind::ChildAdded, this, child, {}, {}, {}});
        return Status::Ok;
    }

    Ref<Node> keep = Ref<Node>::retain(child);
    if (child->parent_) child->parent_->remove_child(child);

    int idx = index_of(reference_child);
    if (idx < 0) return Status::NotFound;   // an observer detached it during unlink
    child->parent_ = this;
    child->owner_document_ = owner_document_;
    children_.insert(children_.begin() + idx, keep);
    propagate_owner_document(child);
    bump_version();
    child->bump_version();
    raise_bubbling(DomMutation{MutationKind::ChildAdded, this, child, {}, {}, {}});
    return Status::Ok;
}

bool Node::remove_child(Node* child) {
    if (!child) return false;
    int idx = index_of(child);
    if (idx < 0) return false;

    // Keep the node alive for the duration: erasing drops the parent's only
    // reference, and observers still need a valid target.
    Ref<Node> keep = children_[idx];

    // Fire BEFORE unlinking so the parent chain is intact for bubbling.
    bump_version();
    child->bump_version();
    raise_bubbling(DomMutation{MutationKind::ChildRemoved, this, child, {}, {}, {}});

    // The observer may already have moved or removed it.
    idx = index_of(child);
    if (idx < 0) return true;

    children_.erase(children_.begin() + idx);
    child->parent_ = nullptr;
    // Detached subtrees report a null document rather than the old one, so
    // document-keyed lookups can't confuse an orphan with a live tree.
    // Re-attaching re-propagates the destination.
    child->owner_document_ = nullptr;
    propagate_owner_document(child);
    return true;
}

// -------------------------------------------------------------------- TextNode

void TextNode::set_data(std::string_view value) {
    if (data_ == value) return;
    std::string old = data_;
    data_ = std::string(value);
    bump_version();
    DomMutation m{MutationKind::TextChanged, this, nullptr, {}, old, data_};
    raise_bubbling(m);
}

// --------------------------------------------------------------------- Element

Element::Element(std::string_view tag_name)
    : Node(NodeType::Element), tag_name_(tag_name) {
    attributes_.set_change_handler(
        [this](std::string_view n, const std::string* o, const std::string* v) {
            on_attribute_changed(n, o, v);
        });
}

void Element::on_attribute_changed(std::string_view name, const std::string* old_v,
                                   const std::string* new_v) {
    bump_version();
    DomMutation m{};
    m.target = this;
    m.name = std::string(name);
    if (!old_v) {
        m.kind = MutationKind::AttributeAdded;
        m.new_value = *new_v;
    } else if (!new_v) {
        m.kind = MutationKind::AttributeRemoved;
        m.old_value = *old_v;
    } else {
        m.kind = MutationKind::AttributeChanged;
        m.old_value = *old_v;
        m.new_value = *new_v;
    }
    raise_bubbling(m);
}

std::vector<std::string_view> Element::class_list() const {
    std::vector<std::string_view> out;
    std::string_view c = class_name();
    std::size_t i = 0;
    auto is_ws = [](char ch) {
        return ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '\f';
    };
    while (i < c.size()) {
        while (i < c.size() && is_ws(c[i])) ++i;
        std::size_t start = i;
        while (i < c.size() && !is_ws(c[i])) ++i;
        if (i > start) out.push_back(c.substr(start, i - start));
    }
    return out;
}

// -------------------------------------------------------------------- Document

bool has_class_token(std::string_view class_attr, std::string_view token) {
    if (class_attr.empty() || token.empty()) return false;
    auto is_ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    std::size_t i = 0;
    while (i < class_attr.size()) {
        while (i < class_attr.size() && is_ws(class_attr[i])) ++i;
        std::size_t start = i;
        while (i < class_attr.size() && !is_ws(class_attr[i])) ++i;
        if (i - start == token.size() && class_attr.compare(start, token.size(), token) == 0) {
            return true;
        }
    }
    return false;
}

namespace {

template <typename Pred>
Element* find_first(Node* node, Pred pred) {
    for (const auto& c : node->children()) {
        if (c->is_element()) {
            auto* e = static_cast<Element*>(c.get());
            if (pred(e)) return e;
        }
        if (Element* found = find_first(c.get(), pred)) return found;
    }
    return nullptr;
}

template <typename Pred>
void find_all(Node* node, Pred pred, std::vector<Element*>* out) {
    for (const auto& c : node->children()) {
        if (c->is_element()) {
            auto* e = static_cast<Element*>(c.get());
            if (pred(e)) out->push_back(e);
        }
        find_all(c.get(), pred, out);
    }
}

} // namespace

Element* Document::get_element_by_id(std::string_view id) {
    return find_first(this, [&](Element* e) { return e->get_attribute("id") == id; });
}

std::vector<Element*> Document::get_elements_by_tag_name(std::string_view tag) {
    std::string lowered(tag);
    for (char& c : lowered) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    std::vector<Element*> out;
    find_all(this, [&](Element* e) { return e->tag_name() == lowered; }, &out);
    return out;
}

std::vector<Element*> Document::get_elements_by_class_name(std::string_view cls) {
    std::vector<Element*> out;
    find_all(this, [&](Element* e) { return has_class_token(e->class_name(), cls); }, &out);
    return out;
}

} // namespace weva
