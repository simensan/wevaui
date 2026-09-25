#include "weva/form_state.h"
#include "weva/form_values.h"
#include "weva/text_classes.h"
#include "weva/temporal_value.h"
#include "../../third_party/decimal/decimal.h"
#include <atomic>
#include <charconv>

namespace weva {
struct FormControlState {
    std::string value;
    std::string custom_validity;
    std::string number_public;
    int64_t number_public_version = -1;
    bool value_valid = false, value_dirty = false;
    bool value_user_edited = false;
    bool checked = false, checked_valid = false, checked_dirty = false;
    bool selected = false, selected_valid = false, selected_dirty = false;
    bool modal = false, popover_open = false;
    uint64_t top_layer_order = 0;
};

namespace {
using decimal_detail::Decimal;
Decimal parse_form_decimal(std::string_view raw, bool sanitize_value = false) {
    if (raw.empty() || raw.front() == '+') return Decimal::Nan();
    if (sanitize_value) {
        // Decimal saturates large exponents before reading the whole string.
        // Value sanitization must reject any suffix, even after saturation.
        // Constraint attributes retain Blink Decimal's parsing behavior.
        const size_t marker = raw.find_first_of("eE");
        if (marker != std::string_view::npos) {
            size_t digits = marker + 1;
            if (digits < raw.size() && (raw[digits] == '+' || raw[digits] == '-')) ++digits;
            if (digits == raw.size() || raw.find_first_not_of("0123456789", digits) != std::string_view::npos)
                return Decimal::Nan();
        }
    }
    const Decimal value = Decimal::FromString(raw);
    static const Decimal maximum(Decimal::kPositive, 292, UINT64_C(17976931348623157));
    return value.IsFinite() && value.Abs() <= maximum ? value : Decimal::Nan();
}
std::string number_public_value(std::string_view value, bool user_edited) {
    std::string normalized(value);
    if (user_edited && value.find_first_of("eE") == std::string_view::npos) {
        for (char& c : normalized) if (c == ',') c = '.';
        if (normalized.size() > 1 && normalized.front() == '+' &&
            normalized[1] != '+' && normalized[1] != '-') normalized.erase(0, 1);
    }
    if (!parse_form_decimal(normalized, true).IsFinite()) normalized.clear();
    return normalized;
}
bool decimal_step_mismatch(const Decimal& value, const Decimal& base, const Decimal& step, bool fractional_error) {
    const Decimal delta = (value - base).Abs();
    static const Decimal large_ratio(Decimal::kPositive, 0, UINT64_C(9007199254740992));
    if (!delta.IsFinite() || delta / large_ratio > step) return false;
    const Decimal remainder = (delta - step * (delta / step).Round()).Abs();
    static const Decimal error_scale(Decimal::kPositive, 0, UINT64_C(16777216));
    const Decimal tolerance = fractional_error ? step / error_scale : Decimal(0);
    return remainder > tolerance && remainder < step - tolerance;
}
std::atomic<uint64_t> next_top_layer_order{1};
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
        return parse_form_decimal(raw, true).IsFinite() ? std::string(raw) : std::string();
    }
    if (!textarea && is_temporal_type(type)) return sanitize_temporal_value(type, raw);
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
bool form_supports_disabled(const Element& e) {
    const auto tag = e.tag_name();
    return tag == "input" || tag == "button" || tag == "select" || tag == "textarea" ||
           tag == "option" || tag == "optgroup" || tag == "fieldset";
}
bool form_is_required(const Element& e) {
    if (!e.has_attribute("required")) return false;
    const auto tag = e.tag_name();
    if (tag == "select" || tag == "textarea") return true;
    if (tag != "input") return false;
    const auto type = form_input_type(e);
    return type != "hidden" && type != "range" && type != "color" &&
           type != "button" && type != "submit" && type != "reset" && type != "image";
}
bool form_is_validation_candidate(const Element& e) {
    const auto tag = e.tag_name();
    if (tag != "input" && tag != "select" && tag != "textarea" && tag != "button") return false;
    if (tag == "button" && !form_is_submit_button(e)) return false;
    if (form_is_disabled(e)) return false;
    if ((tag == "input" || tag == "textarea") && e.has_attribute("readonly")) return false;
    if (tag == "input") {
        const auto type = form_input_type(e);
        if (type == "hidden" || type == "button" || type == "reset") return false;
    }
    for (const Node* p = e.parent(); p; p = p->parent())
        if (p->is_element() && static_cast<const Element*>(p)->tag_name() == "datalist") return false;
    return true;
}

static int compute_validity_selector_state(const Element& e) {
    const auto control_state = [](const Element& field) {
        if (!form_is_validation_candidate(field)) return 0;
        if (field.form_bad_input() || !field.custom_validity().empty() ||
            form_required_value_missing(field) || form_email_type_mismatch(field) ||
            form_url_type_mismatch(field) || !form_text_length_validity(field).valid() ||
            !form_number_validity(field).valid() || !form_temporal_validity(field).valid()) return 2;
        if (field.tag_name() == "input" && field.has_attribute("pattern") && !field.form_value().empty()) {
            const auto type = form_input_type(field);
            if (type == "text" || type == "search" || type == "tel" || type == "url" ||
                type == "email" || type == "password") return -1;
        }
        return 1;
    };
    const bool form = e.tag_name() == "form";
    if (!form && e.tag_name() != "fieldset") return control_state(e);
    int result = 1;
    // Forms aggregate their owned controls, including external controls;
    // fieldsets aggregate descendants regardless of form ownership. Neither
    // aggregate includes a fieldset's own custom validity error.
    walk(form ? *root_of(e) : const_cast<Element&>(e), [&](Element& field) {
        if (result == 2 || (form && form_owner(field) != &e)) return;
        if (!form_is_validation_candidate(field)) return;
        const int state = form_validity_selector_state(field);
        if (state == 2 || state == -1) result = state;
    });
    return result;
}

int form_validity_selector_state(const Element& e) {
    // Ownership and radio groups can reach beyond this element's subtree.
    // The tree's mutation version covers those inputs as well as attributes,
    // live values, disabled ancestors, insertions and removals.
    const auto version = root_of(e)->subtree_version();
    if (e.validity_selector_version_ != version) {
        e.validity_selector_state_ = compute_validity_selector_state(e);
        e.validity_selector_version_ = version;
    }
    return e.validity_selector_state_;
}

bool form_required_value_missing(const Element& e) {
    if (radio(e)) {
        const auto name = e.get_attribute("name");
        if (name.empty()) return false;
        const Element* owner = form_owner(e);
        bool required = false, checked = false;
        walk(*root_of(e), [&](Element& other) {
            if (!radio(other) || other.get_attribute("name") != name || form_owner(other) != owner) return;
            required = required || other.has_attribute("required");
            checked = checked || other.form_checked();
        });
        return required && !checked;
    }
    if (!form_is_required(e)) return false;
    if (e.tag_name() == "select") {
        const auto options = form_options(e);
        const Element* placeholder = !e.has_attribute("multiple") && select_display_size(e) == 1 &&
            !options.empty() && options.front()->parent() == &e && option_value(*options.front()).empty()
            ? options.front() : nullptr;
        for (const auto* option : options)
            if (option->form_selected() && option != placeholder) return false;
        return true;
    }
    if (e.tag_name() == "input" && form_input_type(e) == "checkbox") return !e.form_checked();
    if (form_is_disabled(e) || e.has_attribute("readonly")) return false;
    return e.form_value().empty();
}

// HTML email grammar, not the broader RFC mailbox grammar. Keep this scan
// allocation-free; values have already passed the email sanitization algorithm.
bool form_email_type_mismatch(const Element& e) {
    if (e.tag_name() != "input" || form_input_type(e) != "email") return false;
    const std::string_view value = e.form_value();
    if (value.empty()) return false;
    const auto alnum = [](char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    };
    const auto valid_address = [&](std::string_view address) {
        const size_t at = address.find('@');
        if (at == 0 || at == std::string_view::npos) return false;
        for (char c : address.substr(0, at))
            if (!alnum(c) && std::string_view(".!#$%&'*+-/=?^_`{|}~").find(c) == std::string_view::npos)
                return false;
        auto domain = address.substr(at + 1);
        for (;;) {
            const size_t dot = domain.find('.');
            const auto label = domain.substr(0, dot);
            if (label.empty() || label.size() > 63 || !alnum(label.front()) || !alnum(label.back())) return false;
            for (char c : label) if (!alnum(c) && c != '-') return false;
            if (dot == std::string_view::npos) return true;
            domain.remove_prefix(dot + 1);
        }
    };
    if (!e.has_attribute("multiple")) return !valid_address(value);
    std::string_view rest(value);
    for (;;) {
        const size_t comma = rest.find(',');
        if (!valid_address(trim(rest.substr(0, comma)))) return true;
        if (comma == std::string_view::npos) return false;
        rest.remove_prefix(comma + 1);
    }
}

int form_text_length_limit(const Element& e, std::string_view attribute) {
    if (e.tag_name() != "textarea") {
        if (e.tag_name() != "input") return -1;
        const auto type = form_input_type(e);
        if (type != "text" && type != "search" && type != "url" && type != "tel" &&
            type != "email" && type != "password") return -1;
    }
    auto raw = e.get_attribute(attribute);
    while (!raw.empty() && space(raw.front())) raw.remove_prefix(1);
    if (!raw.empty() && raw.front() == '+') {
        raw.remove_prefix(1);
        if (raw.empty() || raw.front() < '0' || raw.front() > '9') return -1;
    }
    if (raw.empty()) return -1;
    int limit = -1;
    const auto parsed = std::from_chars(raw.data(), raw.data() + raw.size(), limit);
    return parsed.ec == std::errc{} && limit >= 0 ? limit : -1;
}

TextLengthValidity form_text_length_validity(const Element& e) {
    TextLengthValidity result;
    if (!e.form_value_was_user_edited()) return result;
    if (e.tag_name() == "textarea" && (form_is_disabled(e) || e.has_attribute("readonly"))) return result;
    const int minimum = form_text_length_limit(e, "minlength");
    const int maximum = form_text_length_limit(e, "maxlength");
    if (minimum < 0 && maximum < 0) return result;
    const auto value = e.form_value();
    size_t units = 0;
    for (size_t at = 0, bytes = 0; at < value.size(); at += bytes)
        units += utf8_at(value, at, &bytes) > 0xffff ? 2 : 1;
    result.too_short = minimum >= 0 && units > 0 && units < static_cast<size_t>(minimum);
    result.too_long = maximum >= 0 && units > static_cast<size_t>(maximum);
    return result;
}

NumberValidity form_number_validity(const Element& e) {
    NumberValidity result;
    if (e.tag_name() != "input" || form_input_type(e) != "number") return result;
    const Decimal value = parse_form_decimal(e.form_value());
    if (!value.IsFinite()) return result;
    const Decimal minimum = parse_form_decimal(e.get_attribute("min"));
    const Decimal maximum = parse_form_decimal(e.get_attribute("max"));
    result.range_underflow = minimum.IsFinite() && value < minimum;
    result.range_overflow = maximum.IsFinite() && value > maximum;
    if (ascii_equal(e.get_attribute("step"), "any")) return result;
    Decimal step = parse_form_decimal(e.get_attribute("step"));
    if (!step.IsFinite() || step <= Decimal(0)) step = Decimal(1);
    Decimal base = minimum;
    if (!base.IsFinite()) base = parse_form_decimal(e.get_attribute("value"));
    if (!base.IsFinite()) base = Decimal(0);
    result.step_mismatch = decimal_step_mismatch(value, base, step, true);
    return result;
}

bool form_number_step(const Element& e, bool up, std::string& result) {
    if (e.tag_name() != "input" || form_input_type(e) != "number" ||
        form_is_disabled(e) || e.has_attribute("readonly")) return false;
    const Decimal old = parse_form_decimal(e.form_value());
    const bool empty = !old.IsFinite();
    const Decimal value = empty ? Decimal(0) : old;
    const Decimal minimum = parse_form_decimal(e.get_attribute("min"));
    const Decimal maximum = parse_form_decimal(e.get_attribute("max"));
    const bool any = ascii_equal(e.get_attribute("step"), "any");
    Decimal step = parse_form_decimal(e.get_attribute("step"));
    if (!step.IsFinite() || step <= Decimal(0)) step = Decimal(1);
    Decimal base = minimum;
    if (!base.IsFinite()) base = parse_form_decimal(e.get_attribute("value"));
    if (!base.IsFinite()) base = Decimal(0);
    Decimal next;
    // Directional recovery also handles contradictory min/max like the browser.
    if (empty && minimum.IsFinite() && maximum.IsFinite() && minimum > maximum)
        next = up ? minimum : maximum;
    else if ((up || empty) && minimum.IsFinite() && value < minimum) next = minimum;
    else if ((!up || empty) && maximum.IsFinite() && value > maximum)
        next = maximum;
    else {
        if ((!up && minimum.IsFinite() && value < minimum) ||
            (up && maximum.IsFinite() && value > maximum)) return false;
        if (any || !decimal_step_mismatch(value, base, step, true))
            next = value + (up ? step : -step);
        else {
            const Decimal index = (value - base) / step;
            next = base + (up ? index.Floor() + Decimal(1) : index.Ceil() - Decimal(1)) * step;
        }
        if (minimum.IsFinite() && next < minimum) next = minimum;
        if (maximum.IsFinite() && next > maximum)
            next = any ? maximum : base + ((maximum - base) / step).Floor() * step;
    }
    // Number controls are bounded by finite double magnitude even when no
    // explicit limit exists. Keep Decimal arithmetic while finding that step.
    static const Decimal limit(Decimal::kPositive, 292, UINT64_C(17976931348623157));
    const bool magnitude_clamped = next.IsFinite() && next.Abs() > limit;
    if (magnitude_clamped) {
        const Decimal edge = next.IsNegative() ? -limit : limit;
        const Decimal index = (edge - base) / step;
        next = any ? edge : base + (next.IsNegative() ? index.Ceil() : index.Floor()) * step;
    }
    if (!next.IsFinite() || (!empty && (up ? next < old : next > old)) ||
        (!empty && next == old && !magnitude_clamped)) return false;
    result = next.IsZero() ? "0" : next.ToString();
    if (!parse_form_decimal(result, true).IsFinite()) return false;
    return result != e.form_value();
}

NumberValidity form_temporal_validity(const Element& e) {
    NumberValidity result;
    if (e.tag_name() != "input") return result;
    const auto type = form_input_type(e);
    if (!is_temporal_type(type)) return result;
    int64_t value, minimum = 0, maximum = 0, initial = 0;
    if (!parse_temporal_value(type, e.form_value(), &value)) return result;
    const bool has_min = parse_temporal_value(type, e.get_attribute("min"), &minimum);
    const bool has_max = parse_temporal_value(type, e.get_attribute("max"), &maximum);
    if (type == "time" && has_min && has_max && minimum > maximum) {
        const bool outside = value > maximum && value < minimum;
        result.range_underflow = outside;
        result.range_overflow = outside;
    } else {
        result.range_underflow = has_min && value < minimum;
        result.range_overflow = has_max && value > maximum;
    }
    const auto raw_step = e.get_attribute("step");
    if (ascii_equal(raw_step, "any")) return result;
    const bool subday = type == "time" || type == "datetime-local";
    const int scale = subday ? 1000 : type == "date" ? 86400000 : type == "week" ? 604800000 : 1;
    Decimal step = parse_form_decimal(raw_step);
    if (!step.IsFinite() || step <= Decimal(0)) step = Decimal(subday ? 60 : 1);
    // Calendar steps round in calendar units; subday steps round in milliseconds.
    if (subday) step *= Decimal(scale);
    step = step.Round();
    if (step < Decimal(1)) step = Decimal(1);
    if (!subday) step *= Decimal(scale);
    int64_t base = type == "week" ? -259200000 : 0;
    if (has_min) base = minimum;
    else if (parse_temporal_value(type, e.get_attribute("value"), &initial)) base = initial;
    const auto decimal_integer = [](int64_t n) {
        return Decimal(n < 0 ? Decimal::kNegative : Decimal::kPositive, 0, static_cast<uint64_t>(n < 0 ? -n : n));
    };
    result.step_mismatch = decimal_step_mismatch(decimal_integer(value), decimal_integer(base), step, false);
    return result;
}

int form_range_selector_state(const Element& e) {
    if (e.tag_name() != "input" || !form_is_validation_candidate(e)) return 0;
    const auto type = form_input_type(e);
    if (type == "range") return 1; // Sanitization always clamps to its range.
    if (type != "number" && !is_temporal_type(type)) return 0;
    // Chrome treats empty numeric/temporal controls as in-range, including
    // controls with no bounds. Requiredness and step mismatch are independent.
    if (e.form_value().empty()) return 1;
    if (type == "number") {
        const auto minimum = parse_form_decimal(e.get_attribute("min"));
        const auto maximum = parse_form_decimal(e.get_attribute("max"));
        if (!minimum.IsFinite() && !maximum.IsFinite()) return 0;
        const auto value = parse_form_decimal(e.form_value());
        return (minimum.IsFinite() && value < minimum) ||
               (maximum.IsFinite() && value > maximum) ? 2 : 1;
    }
    int64_t minimum = 0, maximum = 0, value = 0;
    const bool has_min = parse_temporal_value(type, e.get_attribute("min"), &minimum);
    const bool has_max = parse_temporal_value(type, e.get_attribute("max"), &maximum);
    if (!has_min && !has_max) return 0;
    if (!parse_temporal_value(type, e.form_value(), &value)) return 1;
    if (type == "time" && has_min && has_max && minimum > maximum)
        return value > maximum && value < minimum ? 2 : 1;
    return (has_min && value < minimum) || (has_max && value > maximum) ? 2 : 1;
}

bool form_is_read_write(const Element& e) {
    if (e.tag_name() == "input" || e.tag_name() == "textarea")
        return (e.tag_name() == "textarea" || form_is_text_entry(e)) &&
               !e.has_attribute("readonly") && !form_is_disabled(e);
    // Missing and invalid contenteditable values inherit. Input/textarea
    // mutability above is independent of an ancestor editing host.
    for (const Node* node = &e; node; node = node->parent()) {
        if (!node->is_element()) continue;
        const auto& element = static_cast<const Element&>(*node);
        if (!element.has_attribute("contenteditable")) continue;
        const auto value = element.get_attribute("contenteditable");
        if (value.empty() || ascii_equal(value, "true") || ascii_equal(value, "plaintext-only")) return true;
        if (ascii_equal(value, "false")) return false;
    }
    return false;
}
bool form_is_submit_button(const Element& e) {
    if (e.tag_name() == "input") {
        const auto type = form_input_type(e);
        return type == "submit" || type == "image";
    }
    if (e.tag_name() != "button") return false;
    const auto type = e.get_attribute("type");
    if (ascii_equal(type, "submit")) return true;
    if (ascii_equal(type, "reset") || ascii_equal(type, "button")) return false;
    const Node* parent = e.parent();
    return !e.has_attribute("command") && !e.has_attribute("commandfor") &&
           !(parent && parent->is_element() && static_cast<const Element*>(parent)->tag_name() == "select");
}
bool form_is_default(const Element& e) {
    if (e.tag_name() == "option") return e.has_attribute("selected");
    if (e.tag_name() == "input") {
        const auto type = form_input_type(e);
        if (type == "checkbox" || type == "radio") return e.has_attribute("checked");
    }
    if (!form_is_submit_button(e)) return false;
    const auto* owner = form_owner(e);
    if (!owner) return false;
    // Stop at the first associated submit button in tree order. Disabled
    // buttons still count; controls with form= may precede the form itself.
    const auto first = [&](const auto& self, const Node& node) -> const Element* {
        if (node.is_element()) {
            const auto& candidate = static_cast<const Element&>(node);
            if (form_is_submit_button(candidate) && form_owner(candidate) == owner) return &candidate;
        }
        for (const auto& child : node.children()) if (const auto* found = self(self, *child)) return found;
        return nullptr;
    };
    return first(first, *root_of(e)) == &e;
}
bool form_is_inert(const Element& e) {
    for (const Node* node = &e; node; node = node->parent()) {
        if (!node->is_element()) continue;
        const auto& ancestor = static_cast<const Element&>(*node);
        if (ancestor.has_attribute("inert")) return true;
        if (ancestor.is_modal()) return false;
    }
    return false;
}

bool form_is_disabled(const Element& e) {
    if (!form_supports_disabled(e)) return false;
    if (e.tag_name() == "option") return option_disabled(e);
    if (e.has_attribute("disabled")) return true;
    if (e.tag_name() == "optgroup") return false;
    const Node* branch = &e;
    for (const Node* parent = e.parent(); parent; branch = parent, parent = parent->parent()) {
        if (!parent->is_element()) continue;
        const auto& fieldset = static_cast<const Element&>(*parent);
        if (fieldset.tag_name() != "fieldset" || !fieldset.has_attribute("disabled")) continue;
        const Node* legend = nullptr;
        for (const auto& child : fieldset.children()) {
            if (child->is_element() && static_cast<const Element&>(*child).tag_name() == "legend") {
                legend = child.get(); break;
            }
        }
        if (branch != legend) return true;
    }
    return false;
}
bool form_popover_light_dismiss(const Element& e) {
    const auto value = e.get_attribute("popover");
    return e.has_attribute("popover") &&
           (value.empty() || ascii_equal(value, "auto") || ascii_equal(value, "hint"));
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
std::string_view form_number_edit_text(std::string_view current, size_t from, size_t to,
    std::string_view incoming, std::string& scratch) {
    const auto left = current.substr(0, from), right = current.substr(to);
    const auto exponent = [](char c) { return c == 'e' || c == 'E'; };
    const auto sign = [](char c) { return c == '+' || c == '-'; };
    const auto decimal = [](char c) { return c == '.' || c == ','; };
    size_t signs = 0;
    bool has_decimal = false, left_exponent = false, right_exponent = false;
    bool right_sign_before_exponent = false, seen_exponent = false;
    for (char c : left) {
        signs += sign(c); has_decimal |= decimal(c); left_exponent |= exponent(c);
    }
    for (char c : right) {
        signs += sign(c); has_decimal |= decimal(c); right_exponent |= exponent(c);
        if (sign(c) && !seen_exponent) right_sign_before_exponent = true;
        seen_exponent |= exponent(c);
    }
    const bool right_decimal = right.find_first_of(".,") != std::string_view::npos;
    bool left_empty = left.empty();
    char previous = left_empty ? 0 : left.back();
    bool changed = false;
    size_t offset = 0;
    while (offset < incoming.size()) {
        size_t bytes = 0;
        const auto code = utf8_at(incoming, offset, &bytes);
        if (!bytes) break;
        char c = code < 128 ? static_cast<char>(code) : 0;
        if (code >= 0xff10 && code <= 0xff19) c = static_cast<char>('0' + code - 0xff10);
        else if (code == 0xff0d || code == 0x30fc) c = '-';
        else if (code == 0xff0e) c = '.';
        const bool digit = c >= '0' && c <= '9';
        bool accept = digit || sign(c) || decimal(c) || exponent(c);
        if (decimal(c)) accept = !has_decimal && !left_exponent && !right_sign_before_exponent;
        else if (exponent(c)) accept = !left_exponent && !right_exponent && !right_decimal;
        else if (sign(c)) accept = signs < 2 &&
            (!(left_exponent || right_exponent) || left_empty || exponent(previous)) &&
            (right.empty() || !sign(right.front())) &&
            !(c == '+' && (left_exponent || right_exponent) && left_empty);
        else if (digit && !right.empty() && sign(right.front()))
            accept = !left_empty && !exponent(previous);
        if (!accept || bytes != 1 || c != incoming[offset]) {
            if (!changed) { scratch.assign(incoming.substr(0, offset)); changed = true; }
        }
        if (accept) {
            if (changed) scratch.push_back(c);
            signs += sign(c); has_decimal |= decimal(c); left_exponent |= exponent(c);
            previous = c; left_empty = false;
        }
        offset += bytes;
    }
    return changed ? std::string_view(scratch) : incoming;
}
std::string_view Element::form_value() const {
    auto value = form_edit_value();
    if (tag_name_ != "input" || form_input_type(*this) != "number") return value;
    auto& state = control_state();
    if (state.number_public_version != form_version_) {
        state.number_public = number_public_value(value, form_value_was_user_edited());
        state.number_public_version = form_version_;
    }
    return state.number_public;
}
bool Element::form_bad_input() const {
    return tag_name_ == "input" && form_input_type(*this) == "number" &&
        form_value_was_user_edited() && !form_edit_value().empty() && form_value().empty();
}
std::string_view Element::form_edit_value() const {
    if (tag_name_ != "textarea" && (tag_name_ != "input" || !value_mode(form_input_type(*this))))
        return get_attribute("value");
    auto& state = control_state();
    if (!state.value_valid) {
        state.value = sanitize_value(*this, tag_name_ == "textarea" ? child_text(*this) : std::string(get_attribute("value")));
        state.value_valid = true;
    }
    return state.value;
}
std::string_view Element::custom_validity() const {
    return form_state_ ? std::string_view(form_state_->custom_validity) : std::string_view();
}
void Element::set_custom_validity(std::string_view message) {
    if (custom_validity() == message) return;
    control_state().custom_validity.assign(message);
    form_changed(FormValueMutation::None);
}
void Element::form_changed(FormValueMutation value_change) {
    ++form_version_;
    bump_version();
    raise_bubbling({MutationKind::FormStateChanged, this, nullptr, {}, {}, {}, value_change});
    if (tag_name_ == "option") if (Element* select = option_owner(*this)) select->form_changed();
    if (tag_name_ == "optgroup" && parent() && parent()->is_element() &&
        static_cast<Element*>(parent())->tag_name() == "select") static_cast<Element*>(parent())->form_changed();
}
bool Element::is_modal() const {
    return form_state_ && form_state_->modal && tag_name_ == "dialog";
}
uint64_t Element::top_layer_order() const {
    return form_state_ && (is_modal() || is_popover_open()) ? form_state_->top_layer_order : 0;
}
bool Element::is_popover_open() const {
    return form_state_ && form_state_->popover_open;
}
void Element::set_modal(bool value) {
    if (tag_name_ != "dialog" || (!form_state_ && !value)) return;
    auto& state = control_state();
    if (state.modal == value) return;
    state.modal = value;
    if (value && !state.popover_open) state.top_layer_order = next_top_layer_order.fetch_add(1, std::memory_order_relaxed);
    form_changed();
}
void Element::set_popover_open(bool value) {
    if (!form_state_ && !value) return;
    auto& state = control_state();
    if (state.popover_open == value) return;
    state.popover_open = value;
    if (value && !state.modal) state.top_layer_order = next_top_layer_order.fetch_add(1, std::memory_order_relaxed);
    form_changed();
}
bool Element::form_value_was_user_edited() const {
    return form_state_ && form_state_->value_dirty && form_state_->value_user_edited;
}
void Element::set_form_value(std::string_view value, bool sanitize, bool user_edit) {
    if (tag_name_ != "textarea" && (tag_name_ != "input" || !value_mode(form_input_type(*this)))) {
        set_attribute("value", value); return;
    }
    auto& state = control_state();
    std::string next = sanitize ? sanitize_value(*this, value) : std::string(value);
    const bool changed = form_edit_value() != next;
    const bool edited = user_edit || (!changed && tag_name_ == "textarea" && state.value_user_edited);
    const bool edit_source_changed = state.value_user_edited != edited;
    state.value_user_edited = edited;
    // A no-op assignment must keep the buffer used by retained text runs.
    if (changed) state.value = std::move(next);
    state.value_valid = true; state.value_dirty = true;
    if (changed || edit_source_changed) form_changed();
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
        form_state_->value_user_edited = false;
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
    if (form_state_) {
        form_state_->modal = false;
        form_state_->popover_open = false;
        form_state_->top_layer_order = 0;
        form_state_->custom_validity.clear();
        form_state_->number_public_version = -1;
        // HTML clones the public value, not a number editor's incomplete text.
        if (form_input_type(source) == "number")
            form_state_->value.assign(source.form_value());
    }
    form_changed();
}
void Element::form_attribute_changed(std::string_view name, const std::string* old_v) {
    if (name == "popover") {
        const auto mode = [](std::string_view value) {
            if (value.empty() || ascii_equal(value, "auto")) return 1;
            if (ascii_equal(value, "hint")) return 2;
            return 3; // manual, including invalid values
        };
        if (!old_v || !has_attribute("popover") || mode(*old_v) != mode(get_attribute("popover"))) {
            Document* owner = owner_document();
            if (!(is_popover_open() && owner && owner->request_popover_attribute_close(*this)))
                set_popover_open(false);
        }
    }
    if (tag_name_ == "input") {
        if (name == "value" && (!form_state_ || !form_state_->value_dirty)) {
            const bool value_changed = sanitize_value(*this, old_v ? *old_v : std::string()) !=
                sanitize_value(*this, get_attribute("value"));
            if (form_state_) form_state_->value_valid = false;
            form_changed(value_changed ? FormValueMutation::InputDefault : FormValueMutation::None);
        }
        if (name == "checked" && (!form_state_ || !form_state_->checked_dirty)) {
            set_form_checked(has_attribute("checked"), false); form_changed();
        }
        if (name == "type") {
            const auto previous = canonical_type(old_v ? std::string_view(*old_v) : std::string_view());
            if (previous != form_input_type(*this) && form_state_ && form_state_->value_valid) {
                if (previous == "number" && form_input_type(*this) != "number") {
                    const auto value = number_public_value(form_state_->value, form_state_->value_user_edited);
                    form_state_->value = std::string(value);
                }
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
            if (form_state_ && form_state_->value_valid && form_input_type(*this) != "number")
                form_state_->value = sanitize_value(*this, form_state_->value);
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
        e.form_changed(FormValueMutation::TextareaDefault);
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
