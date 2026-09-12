#include "weva/binding.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <utility>
#include <vector>

namespace weva {

namespace {

std::string_view trim(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
    while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
    return s.substr(b, e - b);
}

// What a stylesheet and a script both mean by a true value. The Unity engine
// interpolates a bool as "True"/"False", so both spellings count, and so does
// the "false" a JSON-shaped host would produce.
bool truthy(std::string_view value) {
    const std::string_view v = trim(value);
    if (v.empty()) return false;
    if (v == "0" || v == "false" || v == "False" || v == "FALSE") return false;
    return true;
}

// Binding values control the PRESENCE of HTML boolean attributes. A literal
// disabled="false" still disables a button; only templated values use this.
bool boolean_attribute(std::string_view name) {
    for (const auto candidate : {"allowfullscreen", "async", "autofocus", "autoplay",
             "checked", "controls", "default", "defer", "disabled", "formnovalidate",
             "inert", "ismap", "loop", "multiple", "muted", "nomodule", "novalidate",
             "open", "playsinline", "readonly", "required", "reversed", "selected"}) {
        if (name == candidate) return true;
    }
    return false;
}

const char* kClassPrefix = "data-class-";
// Stamped on every row a repeat produced, so the next refresh knows which
// siblings are its and which the author wrote.
const char* kCloneMark = "data-weva-row";
// A row's position in the list, and its identity from `data-key` (or its
// position again, when the template names no key).
const char* kRowIndex = "data-weva-index";
const char* kRowKey = "data-weva-key";

// `Items as alias`, or just `Items`, in which case there is no alias and only
// `$index` and absolute paths resolve inside the row.
void parse_each(std::string_view spec, std::string* list, std::string* alias) {
    const std::string_view s = trim(spec);
    const size_t at = s.find(" as ");
    if (at == std::string_view::npos) {
        *list = std::string(trim(s));
        alias->clear();
        return;
    }
    *list = std::string(trim(s.substr(0, at)));
    *alias = std::string(trim(s.substr(at + 4)));
}

// One row's view of the data: `stage.Name` is `Stages.3.Name`, `$index` is 3,
// and everything else falls through to the controller's own paths -- which is
// what lets a row read a global setting beside its own fields.
class RowResolver : public BindingResolver {
public:
    RowResolver(const BindingResolver& base, std::string alias, std::string prefix, int index)
        : base_(base), alias_(std::move(alias)), prefix_(std::move(prefix)), index_(index) {}

    bool resolve(std::string_view path, std::string* out) const override {
        if (path == "$index") {
            *out = std::to_string(index_);
            return true;
        }
        if (!alias_.empty()) {
            if (path == alias_) return base_.resolve(prefix_, out);
            if (path.size() > alias_.size() + 1 && path.compare(0, alias_.size(), alias_) == 0 &&
                path[alias_.size()] == '.') {
                return base_.resolve(prefix_ + std::string(path.substr(alias_.size())), out);
            }
        }
        return base_.resolve(path, out);
    }

    int count(std::string_view path) const override {
        if (!alias_.empty() && path.size() > alias_.size() + 1 &&
            path.compare(0, alias_.size(), alias_) == 0 && path[alias_.size()] == '.') {
            return base_.count(prefix_ + std::string(path.substr(alias_.size())));
        }
        return base_.count(path);
    }

private:
    const BindingResolver& base_;
    std::string alias_;
    std::string prefix_;
    int index_;
};

Ref<Node> clone_node(const Node& source);

void clone_children(const Node& source, Node* into) {
    for (const Ref<Node>& c : source.children()) {
        Ref<Node> copy = clone_node(*c);
        if (copy) into->append_child(copy.get());
    }
}

Ref<Node> clone_node(const Node& source) {
    if (source.node_type() == NodeType::Text) {
        const TextNode& t = static_cast<const TextNode&>(source);
        // The SOURCE, so the copy is a template like its original and refills
        // rather than freezing whatever the first pass produced.
        return Ref<Node>(new TextNode(t.source()));
    }
    if (source.node_type() != NodeType::Element) return Ref<Node>(nullptr);
    const Element& e = static_cast<const Element&>(source);
    Ref<Element> copy = make_ref<Element>(e.tag_name());
    const AttributeMap& attrs = e.attributes();
    for (std::size_t i = 0; i < attrs.size(); ++i) {
        copy->set_attribute(attrs.name_at(i), attrs.value_at(i));
    }
    clone_children(source, copy.get());
    copy->copy_form_state_from(e);
    // `retain`, not the constructor: that one ADOPTS a reference the caller
    // owns, and `copy` already owns the only one there is.
    return Ref<Node>::retain(copy.get());
}

}   // namespace

bool has_binding(std::string_view text) {
    return text.find("{{") != std::string_view::npos;
}

namespace {
template <typename Append>
void visit_binding_parts(std::string_view text, const BindingResolver& resolver, Append&& append) {
    size_t at = 0;
    while (at < text.size()) {
        const size_t open = text.find("{{", at);
        if (open == std::string_view::npos) {
            append(text.substr(at));
            break;
        }
        const size_t close = text.find("}}", open + 2);
        if (close == std::string_view::npos) {
            // An unclosed brace is text, not a broken binding: the author sees
            // what they wrote rather than losing the rest of the line.
            append(text.substr(at));
            break;
        }
        append(text.substr(at, open - at));
        const std::string_view path = trim(text.substr(open + 2, close - open - 2));
        std::string value;
        if (!path.empty() && resolver.resolve(path, &value)) append(value);
        at = close + 2;
    }
}

// Read every binding in order, but materialize a new output only after its
// first differing byte range. No resolved values survive this refresh.
bool substitute_changed(std::string_view text, const BindingResolver& resolver,
                        std::string_view current, std::string* output) {
    size_t matched = 0;
    bool changed = false;
    visit_binding_parts(text, resolver, [&](std::string_view part) {
        if (!changed && part.size() <= current.size() - matched &&
            current.substr(matched, part.size()) == part) {
            matched += part.size();
            return;
        }
        if (!changed) {
            output->assign(current.substr(0, matched));
            changed = true;
        }
        output->append(part);
    });
    if (!changed && matched != current.size()) {
        output->assign(current.substr(0, matched));
        changed = true;
    }
    return changed;
}
} // namespace

std::string substitute_bindings(std::string_view text, const BindingResolver& resolver) {
    std::string out;
    // Placeholder length is unrelated to output length: keep short values
    // in the string's inline storage and grow only for actual output.
    visit_binding_parts(text, resolver, [&](std::string_view part) { out.append(part); });
    return out;
}

namespace {

// Expands one `<template data-each>` into rows beside it. Returns how many
// nodes it changed; zero means the list is the same list it was.
int expand_repeat(Element& tmpl, const BindingResolver& resolver, BindingTemplates* templates,
                  BindingRepeats* repeats) {
    Node* parent = tmpl.parent();
    if (!parent) return 0;
    std::string list, alias;
    parse_each(tmpl.get_attribute("data-each"), &list, &alias);
    if (list.empty()) return 0;
    const std::string key_field(tmpl.get_attribute("data-key"));
    const int n = std::max(0, resolver.count(list));

    // The identity of each row, so a list whose VALUES moved is refilled in
    // place and one whose items moved is rebuilt. Rebuilding unconditionally
    // would throw away the focus, the scroll and the selection inside a row
    // every time a number next to it changed.
    std::vector<std::string> keys;
    keys.reserve(static_cast<size_t>(n));
    bool explicit_keys = !key_field.empty();
    for (int i = 0; i < n; ++i) {
        const std::string item = list + "." + std::to_string(i);
        std::string key;
        if (key_field.empty() || !resolver.resolve(item + "." + key_field, &key)) {
            key = std::to_string(i);
            explicit_keys = false;
        }
        keys.push_back(key);
    }

    std::vector<std::string>* previous = repeats ? &(*repeats)[&tmpl] : nullptr;
    const bool same = previous && *previous == keys;

    // The rows this template made last time, in order.
    std::vector<Element*> rows;
    for (const Ref<Node>& c : parent->children()) {
        if (c->node_type() != NodeType::Element) continue;
        Element& e = static_cast<Element&>(const_cast<Node&>(*c));
        if (e.get_attribute(kCloneMark) == tmpl.get_attribute("data-each")) rows.push_back(&e);
    }

    int changed = 0;
    // A permutation of unique explicit keys moves the existing DOM rows.
    // Preserve live controls, handles and binding templates; duplicate/missing
    // identities and membership changes keep the ordinary reconstruction path.
    bool reordered = false;
    if (!same && previous && explicit_keys && rows.size() == keys.size() &&
        previous->size() == keys.size() && !rows.empty()) {
        std::map<std::string, Element*> keyed;
        bool unique = true;
        for (size_t i = 0; i < rows.size(); ++i)
            unique = keyed.emplace((*previous)[i], rows[i]).second && unique;
        std::vector<Element*> ordered;
        for (const auto& key : keys) {
            const auto found = keyed.find(key);
            if (found == keyed.end()) { unique = false; break; }
            ordered.push_back(found->second);
            keyed.erase(found);
        }
        // Leave unrelated siblings in place. Generated rows are contiguous;
        // externally interleaved rows use the conservative path below.
        size_t first = 0;
        while (first < parent->children().size() && parent->children()[first].get() != rows.front()) ++first;
        for (size_t i = 0; i < rows.size(); ++i)
            if (first + i >= parent->children().size() || parent->children()[first + i].get() != rows[i]) unique = false;
        if (unique) {
            for (size_t i = 0; i < ordered.size(); ++i) {
                Node* at = parent->children()[first + i].get();
                if (at != ordered[i]) {
                    parent->insert_before(ordered[i], at);
                    ++changed;
                }
                ordered[i]->set_attribute(kRowIndex, std::to_string(i));
            }
            rows = std::move(ordered);
            *previous = keys;
            reordered = true;
        }
    }
    if (!same && !reordered) {
        for (Element* row : rows) {
            if (templates) templates->erase(row);
            parent->remove_child(row);
            ++changed;
        }
        rows.clear();
        // Appended after the template, in order, so the rows read in the
        // document the way they read in the data.
        for (int i = 0; i < n; ++i) {
            Ref<Element> row = make_ref<Element>(tmpl.tag_name() == "template" ? "div"
                                                                              : tmpl.tag_name());
            // A single element in the template body IS the row; anything else
            // is wrapped, which is the only way to keep the count right.
            const Element* only = nullptr;
            int elements = 0;
            for (const Ref<Node>& c : tmpl.children()) {
                if (c->node_type() != NodeType::Element) continue;
                only = static_cast<const Element*>(c.get());
                ++elements;
            }
            Ref<Node> made;
            if (elements == 1 && only) {
                made = clone_node(*only);
            } else {
                clone_children(tmpl, row.get());
                made = Ref<Node>::retain(row.get());
            }
            if (!made) continue;
            Element& made_row = static_cast<Element&>(*made);
            made_row.set_attribute(kCloneMark, tmpl.get_attribute("data-each"));
            // Its position and its identity, stamped on the row itself.
            //
            // A repeated row usually has no id -- the template wrote one
            // element and the data decides how many there are -- so a click on
            // one arrived at a script with nothing to say WHICH row it was.
            // These make the row addressable without the author having to
            // thread an id through the data, and a stylesheet can select on
            // them too.
            made_row.set_attribute(kRowIndex, std::to_string(i));
            made_row.set_attribute(kRowKey, keys[static_cast<size_t>(i)]);
            parent->append_child(made.get());
            rows.push_back(&static_cast<Element&>(*made));
            ++changed;
        }
        if (previous) *previous = keys;
    }

    // Filled either way: the values inside a row change far more often than
    // the list does.
    for (size_t i = 0; i < rows.size(); ++i) {
        const RowResolver row_resolver(resolver, alias, list + "." + std::to_string(i),
                                       static_cast<int>(i));
        changed += apply_bindings(*rows[i], row_resolver, templates, repeats);
    }
    return changed;
}

}   // namespace

int apply_bindings(Node& root, const BindingResolver& resolver, BindingTemplates* templates,
                   BindingRepeats* repeats) {
    int changed = 0;
    if (root.node_type() == NodeType::Text) {
        TextNode& text = static_cast<TextNode&>(root);
        // The SOURCE, not the current data: substituting what a previous pass
        // produced would lose the braces after the first refresh.
        if (has_binding(text.source())) {
            std::string filled;
            if (substitute_changed(text.source(), resolver, text.data(), &filled)) {
                text.set_data(filled);
                ++changed;
            }
        }
        return changed;
    }
    if (root.node_type() == NodeType::Element) {
        Element& e = static_cast<Element&>(root);
        // A template is not content: it is the shape of the rows beside it,
        // and nothing inside it is filled in place.
        if (e.has_attribute("data-each")) {
            return expand_repeat(e, resolver, templates, repeats);
        }
        AttributeMap& attrs = e.attributes();
        // Collected first: setting an attribute while walking the map is a
        // mutation of the thing being walked.
        std::vector<std::pair<std::string, std::string>> writes;
        std::vector<std::string> removals;
        std::vector<std::pair<std::string, bool>> classes;
        const auto saved = templates ? templates->find(&e) : BindingTemplates::iterator{};
        for (std::size_t i = 0; i < attrs.size(); ++i) {
            const std::string_view attribute_name = attrs.name_at(i);
            const std::string_view value = attrs.value_at(i);

            // `data-class-<name>="Path"` toggles ONE class and leaves the rest
            // of the attribute alone, which is what makes it composable with
            // classes the author wrote by hand.
            if (attribute_name.rfind(kClassPrefix, 0) == 0 && attribute_name.size() > std::strlen(kClassPrefix)) {
                // Attribute names are interned; literal paths need no owned
                // copy during this synchronous read. Only expansion owns text.
                std::string expanded_path;
                std::string_view path = value;
                if (has_binding(value)) {
                    expanded_path = substitute_bindings(value, resolver);
                    path = expanded_path;
                }
                std::string resolved;
                const bool on = resolver.resolve(trim(path), &resolved) && truthy(resolved);
                const std::string_view token = attribute_name.substr(std::strlen(kClassPrefix));
                // Most signal-driven refreshes leave class membership alone.
                // Avoid allocating the pending toggle and copying the full
                // class list when this specific token already has its value.
                if (has_class_token(e.class_name(), token) != on)
                    classes.emplace_back(std::string(token), on);
                continue;
            }
            const std::string_view name = attribute_name;

            // The template is whatever was there the first time this ran, kept
            // because the substitution overwrites it.
            std::string_view tmpl;
            if (templates && saved != templates->end()) {
                auto it = saved->second.find(name);
                if (it != saved->second.end()) tmpl = it->second;
            }
            if (tmpl.empty() && has_binding(value)) {
                tmpl = templates ? std::string_view((*templates)[&e][std::string(name)] = std::string(value)) : value;
            }
            if (tmpl.empty()) continue;
            if (boolean_attribute(name)) {
                const std::string filled = substitute_bindings(tmpl, resolver);
                if (!truthy(filled)) removals.emplace_back(name);
                else if (!value.empty()) writes.emplace_back(name, "");
            } else {
                std::string filled;
                if (substitute_changed(tmpl, resolver, value, &filled))
                    writes.emplace_back(name, std::move(filled));
            }
        }
        // A false boolean is absent from the live attribute map, but its
        // owned template must be evaluated again so it can become true later.
        if (templates) {
            const auto found = templates->find(&e);
            if (found != templates->end()) {
                for (const auto& entry : found->second) {
                    if (boolean_attribute(entry.first) && !attrs.contains(entry.first) &&
                        truthy(substitute_bindings(entry.second, resolver))) {
                        writes.emplace_back(entry.first, "");
                    }
                }
            }
        }
        for (const auto& kv : writes) {
            attrs.set(kv.first, kv.second);
            ++changed;
        }
        for (const auto& name : removals) {
            attrs.remove(name);
            ++changed;
        }
        for (const auto& kv : classes) {
            const std::string_view current = e.get_attribute("class");
            std::string list(current);
            const bool present = [&] {
                size_t at = 0;
                while (at < list.size()) {
                    while (at < list.size() && std::isspace(static_cast<unsigned char>(list[at]))) ++at;
                    size_t end = at;
                    while (end < list.size() && !std::isspace(static_cast<unsigned char>(list[end]))) ++end;
                    if (list.compare(at, end - at, kv.first) == 0) return true;
                    at = end;
                }
                return false;
            }();
            if (kv.second == present) continue;
            if (kv.second) {
                if (!list.empty()) list += ' ';
                list += kv.first;
            } else {
                std::string rebuilt;
                size_t at = 0;
                while (at < list.size()) {
                    while (at < list.size() && std::isspace(static_cast<unsigned char>(list[at]))) ++at;
                    size_t end = at;
                    while (end < list.size() && !std::isspace(static_cast<unsigned char>(list[end]))) ++end;
                    if (end > at && list.compare(at, end - at, kv.first) != 0) {
                        if (!rebuilt.empty()) rebuilt += ' ';
                        rebuilt.append(list, at, end - at);
                    }
                    at = end;
                }
                list = rebuilt;
            }
            e.set_attribute("class", list);
            ++changed;
        }
    }
    // Only a direct repeat can mutate this child vector. Ordinary binding
    // walks need no temporary vector or retain/release of every child.
    std::vector<Ref<Node>> snapshot;
    const bool expands = std::any_of(root.children().begin(), root.children().end(), [](const Ref<Node>& child) {
        return child->is_element() && static_cast<const Element&>(*child).has_attribute("data-each");
    });
    if (expands) snapshot.assign(root.children().begin(), root.children().end());
    const auto& children = expands ? snapshot : root.children();
    for (const Ref<Node>& child : children) {
        // A row belongs to the template that made it, and is filled by the
        // template's pass with the row's own scope. Walking into one here
        // would fill it a second time with the CONTROLLER's scope, where
        // `quest.Title` and `$index` mean nothing -- which emptied every row
        // it had just filled.
        if (child->node_type() == NodeType::Element &&
            static_cast<const Element&>(*child).has_attribute(kCloneMark)) {
            continue;
        }
        changed += apply_bindings(const_cast<Node&>(*child), resolver, templates, repeats);
    }
    return changed;
}

}   // namespace weva
