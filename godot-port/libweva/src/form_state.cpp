#include "weva/form_state.h"
#include "weva/form_values.h"

namespace weva {
struct FormControlState {
    std::string value;
    bool value_valid = false, value_dirty = false;
    bool checked = false, checked_valid = false, checked_dirty = false;
    bool selected = false, selected_valid = false, selected_dirty = false;
};

namespace {
bool ascii_equal(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char c = a[i];
        if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
        if (c != b[i]) return false;
    }
    return true;
}
std::string_view canonical_type(std::string_view raw) {
    if (raw.empty() || raw == "text") return "text";
    for (const auto type : {"text", "hidden", "search", "tel", "url", "email", "password", "date",
         "month", "week", "time", "datetime-local", "number", "range", "color", "checkbox", "radio",
         "file", "submit", "image", "reset", "button"}) if (ascii_equal(raw, type)) return type;
    return "text";
}
bool space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f'; }
std::string_view trim(std::string_view value) {
    while (!value.empty() && space(value.front())) value.remove_prefix(1);
    while (!value.empty() && space(value.back())) value.remove_suffix(1);
    return value;
}
Node* root_of(const Node& node) {
    auto* root = const_cast<Node*>(&node);
    while (root->parent()) root = root->parent();
    return root;
}
template<class F> void walk(Node& node, const F& visit) {
    if (node.is_element()) visit(static_cast<Element&>(node));
    for (const auto& child : node.children()) walk(*child, visit);
}
bool radio(const Element& e) { return e.tag_name() == "input" && form_input_type(e) == "radio"; }
bool value_mode(std::string_view type) {
    return type != "hidden" && type != "submit" && type != "reset" && type != "button" &&
           type != "image" && type != "checkbox" && type != "radio";
}
std::string child_text(const Element& e) {
    std::string value;
    for (const auto& child : e.children())
        if (child->node_type() == NodeType::Text) value += static_cast<const TextNode&>(*child).data();
    return value;
}
std::string sanitize_value(const Element& e, std::string_view raw) {
    const bool textarea = e.tag_name() == "textarea";
    const auto type = form_input_type(e);
    if (!textarea && type == "range") return form_number_text(RangeValue(e, raw).value);
    if (!textarea && type == "number") {
        // HTML's valid floating-point syntax, without a leading plus, spaces,
        // NaN/Inf, or a trailing decimal point.
        double v = 0;
        if (raw.empty()) return {};
        const auto p = std::from_chars(raw.data(), raw.data() + raw.size(), v);
        if (p.ec != std::errc{} || p.ptr != raw.data() + raw.size() || !std::isfinite(v) ||
            raw.front() == '+' || raw.back() == '.' || raw.find_first_of("nNiI ") != std::string_view::npos) return {};
        return std::string(raw);
    }
    if (!textarea && type != "text" && type != "search" && type != "tel" &&
        type != "password" && type != "url" && type != "email") return std::string(raw);
    std::string value;
    value.reserve(raw.size());
    for (size_t i = 0; i < raw.size(); ++i) {
        if (raw[i] == '\r') {
            if (textarea) {
                value += '\n';
                if (i + 1 < raw.size() && raw[i + 1] == '\n') ++i;
            }
        } else if (textarea || raw[i] != '\n') value += raw[i];
    }
    if (!textarea && (type == "email" || type == "url")) {
        if (type == "email" && e.has_attribute("multiple")) {
            std::string result;
            std::string_view rest(value);
            for (;;) {
                const size_t comma = rest.find(',');
                result += trim(rest.substr(0, comma));
                if (comma == std::string_view::npos) break;
                result += ',';
                rest.remove_prefix(comma + 1);
            }
            return result;
        }
        return std::string(trim(value));
    }
    return value;
}
void reconcile_radio(Element& e) {
    if (!radio(e) || !e.form_checked() || e.get_attribute("name").empty()) return;
    const auto name = e.get_attribute("name");
    Element* owner = form_owner(e);
    walk(*root_of(e), [&](Element& other) {
        if (&other != &e && radio(other) && other.get_attribute("name") == name &&
            form_owner(other) == owner && other.form_checked()) other.set_form_checked(false, false);
    });
}
}
bool option_disabled(const Element& e) {
    if (e.has_attribute("disabled")) return true;
    const Node* p = e.parent();
    return p && p->is_element() && static_cast<const Element*>(p)->tag_name() == "optgroup" &&
           static_cast<const Element*>(p)->has_attribute("disabled");
}
std::string_view form_input_type(const Element& e) {
    return canonical_type(e.get_attribute("type"));
}
bool form_is_text_entry(const Element& e) {
    if (e.tag_name() != "input") return false;
    const auto type = form_input_type(e);
    return type != "hidden" && type != "checkbox" && type != "radio" && type != "range" &&
           type != "file" && type != "color" && type != "image" &&
           type != "submit" && type != "reset" && type != "button";
}
int select_display_size(const Element& select) {
    const int fallback = select.has_attribute("multiple") ? 4 : 1;
    auto raw = trim(select.get_attribute("size"));
    if (!raw.empty() && raw.front() == '+') raw.remove_prefix(1);
    if (raw.empty() || raw.front() < '0' || raw.front() > '9') return fallback;
    uint64_t value = 0;
    for (char c : raw) {
        if (c < '0' || c > '9') break;
        value = value * 10 + static_cast<unsigned>(c - '0');
        if (value > UINT32_MAX) return fallback;
    }
    return value == 0 ? fallback : static_cast<int>(std::min<uint64_t>(value, INT32_MAX));
}
bool select_is_listbox(const Element& select) {
    return select.has_attribute("multiple") || select_display_size(select) > 1;
}
Element* form_owner(const Element& e) {
    if (e.has_attribute("form")) {
        const auto id = e.get_attribute("form");
        if (id.empty()) return nullptr;
        Element* match = nullptr;
        walk(*root_of(e), [&](Element& candidate) { if (!match && candidate.id() == id) match = &candidate; });
        return match && match->tag_name() == "form" ? match : nullptr;
    }
    for (Node* p = e.parent(); p; p = p->parent())
        if (p->is_element() && static_cast<Element*>(p)->tag_name() == "form") return static_cast<Element*>(p);
    return nullptr;
}
Element* option_owner(const Element& e) {
    Node* p = e.parent();
    if (p && p->is_element() && static_cast<Element*>(p)->tag_name() == "optgroup") p = p->parent();
    return p && p->is_element() && static_cast<Element*>(p)->tag_name() == "select" ? static_cast<Element*>(p) : nullptr;
}
std::vector<const Element*> form_options(const Element& select) {
    std::vector<const Element*> out;
    for (const auto& child : select.children()) {
        if (!child->is_element()) continue;
        const auto& e = static_cast<const Element&>(*child);
        if (e.tag_name() == "option") out.push_back(&e);
        else if (e.tag_name() == "optgroup")
            for (const auto& grouped : e.children())
                if (grouped->is_element() && static_cast<const Element&>(*grouped).tag_name() == "option")
                    out.push_back(static_cast<const Element*>(grouped.get()));
    }
    return out;
}
std::vector<const Element*> select_rows(const Element& select) {
    std::vector<const Element*> rows;
    for (const auto& child : select.children()) {
        if (!child->is_element()) continue;
        const auto* e = static_cast<const Element*>(child.get());
        if (e->tag_name() == "option") rows.push_back(e);
        else if (e->tag_name() == "optgroup") {
            rows.push_back(e);
            for (const auto& option : e->children())
                if (option->is_element() && static_cast<const Element*>(option.get())->tag_name() == "option")
                    rows.push_back(static_cast<const Element*>(option.get()));
        }
    }
    return rows;
}
namespace {
void option_descendant_text(const Node& node, std::string& out) {
    if (node.node_type() == NodeType::Text) out += static_cast<const TextNode&>(node).data();
    else if (node.is_element() && static_cast<const Element&>(node).tag_name() == "script") return;
    else for (const auto& child : node.children()) option_descendant_text(*child, out);
}
std::string option_text(const Element& e) {
    // HTML option text strips and collapses ASCII whitespace.
    std::string out;
    std::string raw;
    option_descendant_text(e, raw);
    bool gap = false;
    for (char c : raw) {
        if (space(c)) { gap = !out.empty(); continue; }
        if (gap) out += ' ';
        gap = false; out += c;
    }
    return out;
}
}
std::string option_value(const Element& e) {
    return e.has_attribute("value") ? std::string(e.get_attribute("value")) : option_text(e);
}
std::string option_label(const Element& e) {
    const auto label = e.get_attribute("label");
    return label.empty() ? option_text(e) : std::string(label);
}

Element::Element(std::string_view tag_name) : Node(NodeType::Element), tag_name_(tag_name) {
    attributes_.set_change_handler([this](std::string_view n, const std::string* o, const std::string* v) {
        on_attribute_changed(n, o, v);
    });
}
Element::~Element() = default;
FormControlState& Element::control_state() const {
    if (!form_state_) form_state_ = std::make_unique<FormControlState>();
    return *form_state_;
}
std::string_view Element::form_value() const {
    if (tag_name_ != "textarea" && (tag_name_ != "input" || !value_mode(form_input_type(*this))))
        return get_attribute("value");
    auto& state = control_state();
    if (!state.value_valid) {
        state.value = sanitize_value(*this, tag_name_ == "textarea" ? child_text(*this) : std::string(get_attribute("value")));
        state.value_valid = true;
    }
    return state.value;
}
void Element::form_changed() {
    ++form_version_;
    bump_version();
    raise_bubbling({MutationKind::FormStateChanged, this, nullptr, {}, {}, {}});
    if (tag_name_ == "option") if (Element* select = option_owner(*this)) select->form_changed();
    if (tag_name_ == "optgroup" && parent() && parent()->is_element() &&
        static_cast<Element*>(parent())->tag_name() == "select") static_cast<Element*>(parent())->form_changed();
}
void Element::set_form_value(std::string_view value, bool sanitize) {
    if (tag_name_ != "textarea" && (tag_name_ != "input" || !value_mode(form_input_type(*this)))) {
        set_attribute("value", value); return;
    }
    auto& state = control_state();
    std::string next = sanitize ? sanitize_value(*this, value) : std::string(value);
    const bool changed = form_value() != next;
    // A no-op assignment must keep the buffer used by retained text runs.
    if (changed) state.value = std::move(next);
    state.value_valid = true; state.value_dirty = true;
    if (changed) form_changed();
}
bool Element::form_checked() const {
    return form_state_ && form_state_->checked_valid ? form_state_->checked : has_attribute("checked");
}
void Element::set_form_checked(bool checked, bool dirty) {
    const bool changed = form_checked() != checked;
    auto& state = control_state();
    state.checked = checked; state.checked_valid = true;
    if (dirty) state.checked_dirty = true;
    if (changed) form_changed();
    if (checked) reconcile_radio(*this);
}
bool Element::form_selected() const {
    return form_state_ && form_state_->selected_valid ? form_state_->selected : has_attribute("selected");
}
void Element::set_selected_raw(bool selected, bool dirty) {
    const bool changed = form_selected() != selected;
    auto& state = control_state();
    state.selected = selected; state.selected_valid = true;
    if (dirty) state.selected_dirty = true;
    if (changed) form_changed();
}
void Element::set_form_selected(bool selected, bool dirty) {
    const bool changed = form_selected() != selected;
    set_selected_raw(selected, dirty);
    Element* select = option_owner(*this);
    if (!select || select->has_attribute("multiple")) return;
    if (selected) {
        for (const auto* option : form_options(*select))
            if (option != this) const_cast<Element*>(option)->set_selected_raw(false, false);
    } else if (changed) normalize_select(*select);
}
void normalize_select(Element& select, bool fallback) {
    if (select.has_attribute("multiple")) return;
    const auto options = form_options(select);
    const Element* chosen = nullptr;
    for (const auto* option : options) if (option->form_selected()) chosen = option;
    if (!chosen && fallback && select_display_size(select) <= 1)
        for (const auto* option : options) if (!option_disabled(*option)) { chosen = option; break; }
    for (const auto* option : options) const_cast<Element*>(option)->set_selected_raw(option == chosen, false);
}
void set_select_value(Element& select, std::string_view value) {
    bool found = false;
    for (const auto* option : form_options(select)) {
        const std::string candidate = option_value(*option);
        bool chosen = !found && candidate == value;
        if (select.has_attribute("multiple")) {
            std::string_view rest = value;
            for (;;) {
                const auto comma = rest.find(',');
                if (rest.substr(0, comma) == candidate) { chosen = true; break; }
                if (comma == std::string_view::npos) break;
                rest.remove_prefix(comma + 1);
            }
        }
        auto* mutable_option = const_cast<Element*>(option);
        mutable_option->set_selected_raw(chosen, chosen);
        found = found || chosen;
    }
}
void Element::reset_form_control() {
    if (tag_name_ == "select") {
        for (const auto* option : form_options(*this)) {
            auto* o = const_cast<Element*>(option);
            o->control_state().selected_dirty = false;
            o->set_selected_raw(o->has_attribute("selected"), false);
        }
        normalize_select(*this); return;
    }
    if (tag_name_ != "input" && tag_name_ != "textarea") return;
    if (form_state_) {
        form_state_->value_dirty = false;
        form_state_->value_valid = false;
        form_state_->checked_dirty = false;
    }
    if (tag_name_ == "input") set_form_checked(has_attribute("checked"), false);
    form_changed();
}
void reset_form(Element& form) {
    if (form.tag_name() != "form") return;
    walk(*root_of(form), [&](Element& e) {
        if ((e.tag_name() == "input" || e.tag_name() == "textarea" || e.tag_name() == "select") && form_owner(e) == &form)
            e.reset_form_control();
    });
}
void Element::copy_form_state_from(const Element& source) {
    // HTML clones input/textarea live state. Options clone their attributes,
    // so a select clone gets its default selection rather than the live one.
    if (tag_name_ != "input" && tag_name_ != "textarea") return;
    if (!source.form_state_ && !form_state_) return;
    if (source.form_state_) form_state_ = std::make_unique<FormControlState>(*source.form_state_);
    else form_state_.reset();
    form_changed();
}
void Element::form_attribute_changed(std::string_view name, const std::string* old_v) {
    if (tag_name_ == "input") {
        if (name == "value" && (!form_state_ || !form_state_->value_dirty)) {
            if (form_state_) form_state_->value_valid = false;
            form_changed();
        }
        if (name == "checked" && (!form_state_ || !form_state_->checked_dirty)) {
            set_form_checked(has_attribute("checked"), false); form_changed();
        }
        if (name == "type") {
            const auto previous = canonical_type(old_v ? std::string_view(*old_v) : std::string_view());
            if (form_state_ && form_state_->value_valid) {
                // Leaving value mode reflects the edited value into markup;
                // entering it starts a clean value from that markup.
                if (value_mode(previous) && !value_mode(form_input_type(*this))) set_attribute("value", form_state_->value);
                if (!value_mode(previous) && value_mode(form_input_type(*this))) form_state_->value_dirty = false;
                if (form_state_->value_dirty) form_state_->value = sanitize_value(*this, form_state_->value);
                else form_state_->value_valid = false;
            }
            form_changed();
        }
        if (name == "min" || name == "max" || name == "step" || name == "multiple") {
            if (form_state_ && form_state_->value_valid) form_state_->value = sanitize_value(*this, form_state_->value);
            form_changed();
        }
        if (name == "name" || name == "form" || name == "type") reconcile_radio(*this);
    }
    if (tag_name_ == "option") {
        if (name == "label") ++form_label_version_;
        if (name == "selected" && (!form_state_ || !form_state_->selected_dirty)) {
            set_form_selected(has_attribute("selected"), false);
            if (Element* select = option_owner(*this)) normalize_select(*select);
        }
        if (name == "selected" || name == "value" || name == "label" || name == "disabled") form_changed();
    }
    if (tag_name_ == "optgroup" && name == "label") {
        ++form_label_version_;
        form_changed();
    }
    if (tag_name_ == "select" && (name == "multiple" || name == "size")) {
        normalize_select(*this);
        form_changed();
    }
    if (name == "id" && parent()) {
        // Explicit form owners can change when any element gains/loses an ID.
        walk(*root_of(*this), [](Element& e) { if (radio(e) && e.has_attribute("form")) reconcile_radio(e); });
    }
}
void form_children_changed(Node& parent) {
    if (!parent.is_element()) return;
    auto& e = static_cast<Element&>(parent);
    for (Node* n = &parent; n; n = n->parent()) {
        if (!n->is_element()) continue;
        auto& ancestor = static_cast<Element&>(*n);
        if (ancestor.tag_name() == "select") break;
        if (ancestor.tag_name() == "option") {
            ++ancestor.form_label_version_;
            ancestor.form_changed();
            break;
        }
    }
    if (e.tag_name() == "textarea" && (!e.form_state_ || !e.form_state_->value_dirty)) {
        if (e.form_state_) e.form_state_->value_valid = false;
        e.form_changed();
    }
    if (e.tag_name() == "select") normalize_select(e);
    else if (e.tag_name() == "optgroup") {
        if (Node* p = e.parent(); p && p->is_element() && static_cast<Element*>(p)->tag_name() == "select")
            normalize_select(*static_cast<Element*>(p));
    }
}
void form_subtree_inserted(Node& node) {
    walk(node, [](Element& e) {
        if (radio(e)) reconcile_radio(e);
        if (e.tag_name() == "option" && e.form_selected()) e.set_form_selected(true, false);
    });
}
} // namespace weva
