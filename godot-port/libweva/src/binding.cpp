#include "weva/binding.h"

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

const char* kClassPrefix = "data-class-";

}   // namespace

bool has_binding(std::string_view text) {
    return text.find("{{") != std::string_view::npos;
}

std::string substitute_bindings(std::string_view text, const BindingResolver& resolver) {
    std::string out;
    out.reserve(text.size());
    size_t at = 0;
    while (at < text.size()) {
        const size_t open = text.find("{{", at);
        if (open == std::string_view::npos) {
            out.append(text.substr(at));
            break;
        }
        const size_t close = text.find("}}", open + 2);
        if (close == std::string_view::npos) {
            // An unclosed brace is text, not a broken binding: the author sees
            // what they wrote rather than losing the rest of the line.
            out.append(text.substr(at));
            break;
        }
        out.append(text.substr(at, open - at));
        const std::string_view path = trim(text.substr(open + 2, close - open - 2));
        std::string value;
        if (!path.empty() && resolver.resolve(path, &value)) out.append(value);
        at = close + 2;
    }
    return out;
}

int apply_bindings(Node& root, const BindingResolver& resolver, BindingTemplates* templates) {
    int changed = 0;
    if (root.node_type() == NodeType::Text) {
        TextNode& text = static_cast<TextNode&>(root);
        // The SOURCE, not the current data: substituting what a previous pass
        // produced would lose the braces after the first refresh.
        if (has_binding(text.source())) {
            const std::string filled = substitute_bindings(text.source(), resolver);
            if (filled != text.data()) {
                text.set_data(filled);
                ++changed;
            }
        }
        return changed;
    }
    if (root.node_type() == NodeType::Element) {
        Element& e = static_cast<Element&>(root);
        AttributeMap& attrs = e.attributes();
        // Collected first: setting an attribute while walking the map is a
        // mutation of the thing being walked.
        std::vector<std::pair<std::string, std::string>> writes;
        std::vector<std::pair<std::string, bool>> classes;
        for (std::size_t i = 0; i < attrs.size(); ++i) {
            const std::string name(attrs.name_at(i));
            const std::string value(attrs.value_at(i));

            // `data-class-<name>="Path"` toggles ONE class and leaves the rest
            // of the attribute alone, which is what makes it composable with
            // classes the author wrote by hand.
            if (name.rfind(kClassPrefix, 0) == 0 && name.size() > std::strlen(kClassPrefix)) {
                std::string path = value;
                if (has_binding(value)) path = substitute_bindings(value, resolver);
                std::string resolved;
                const bool on = resolver.resolve(trim(path), &resolved) && truthy(resolved);
                classes.emplace_back(name.substr(std::strlen(kClassPrefix)), on);
                continue;
            }

            // The template is whatever was there the first time this ran, kept
            // because the substitution overwrites it.
            const std::string* tmpl = nullptr;
            if (templates) {
                auto& per_element = (*templates)[&e];
                auto it = per_element.find(name);
                if (it != per_element.end()) {
                    tmpl = &it->second;
                } else if (has_binding(value)) {
                    tmpl = &(per_element[name] = value);
                }
            }
            if (!tmpl && has_binding(value)) {
                const std::string filled = substitute_bindings(value, resolver);
                if (filled != value) writes.emplace_back(name, filled);
                continue;
            }
            if (!tmpl) continue;
            const std::string filled = substitute_bindings(*tmpl, resolver);
            if (filled != value) writes.emplace_back(name, filled);
        }
        for (const auto& kv : writes) {
            attrs.set(kv.first, kv.second);
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
    for (const Ref<Node>& child : root.children()) {
        changed += apply_bindings(const_cast<Node&>(*child), resolver, templates);
    }
    return changed;
}

}   // namespace weva
