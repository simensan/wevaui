#pragma once
#include "weva/dom.h"

namespace weva {
// Canonical HTML input type (unknown/missing -> text), allocation-free.
std::string_view form_input_type(const Element& input);
// Editable one-line values, including number/date fields. Their text stays
// centered even when the content box is shorter than the font.
bool form_is_text_entry(const Element& input);
// Filter an insertion against the surviving editor text. Returns incoming
// unchanged without allocation on the common path; otherwise uses scratch.
std::string_view form_number_edit_text(std::string_view current, size_t from, size_t to,
    std::string_view incoming, std::string& scratch);
Element* form_owner(const Element& control);
Element* option_owner(const Element& option);
std::vector<const Element*> form_options(const Element& select);
// Display rows include optgroup headings; option indices never count them.
std::vector<const Element*> select_rows(const Element& select);
// HTML non-negative integer parsing; zero/invalid sizes use 4 for multiple,
// 1 otherwise. A single selection control is a listbox only above size 1.
int select_display_size(const Element& select);
bool select_is_listbox(const Element& select);
bool option_disabled(const Element& option);
bool form_supports_disabled(const Element& element);
// Effective HTML disabledness, including fieldsets and the first-legend exception.
bool form_is_disabled(const Element& element);
// Explicit HTML inertness follows DOM ancestry; a modal escapes its ancestors.
// This is independent of disabledness and of document-wide modal blocking.
bool form_is_inert(const Element& element);
bool form_is_required(const Element& element);
bool form_is_validation_candidate(const Element& element);
bool form_required_value_missing(const Element& element);
// 0: neither, 1: valid, 2: invalid, -1: unsupported pattern constraint.
int form_validity_selector_state(const Element& element);
struct NumberValidity {
    bool range_underflow = false;
    bool range_overflow = false;
    bool step_mismatch = false;
    bool valid() const { return !range_underflow && !range_overflow && !step_mismatch; }
};
// 0: neither pseudo, 1: in-range, 2: out-of-range.
int form_range_selector_state(const Element& element);
NumberValidity form_number_validity(const Element& element);
// Keyboard stepping (step=any uses 1). Returns false when the value stays put.
bool form_number_step(const Element& element, bool up, std::string& result);
NumberValidity form_temporal_validity(const Element& element);
bool form_email_type_mismatch(const Element& element);
bool form_url_type_mismatch(const Element& element);
int form_text_length_limit(const Element& element, std::string_view attribute);
struct TextLengthValidity {
    bool too_short = false, too_long = false;
    bool valid() const { return !too_short && !too_long; }
};
TextLengthValidity form_text_length_validity(const Element& element);
// HTML :read-write selector state; contenteditable inheritance is styling only.
bool form_is_read_write(const Element& element);
bool form_is_submit_button(const Element& element);
bool form_is_default(const Element& element);
bool form_popover_light_dismiss(const Element& element);
std::string option_value(const Element& option);
std::string option_label(const Element& option);
void normalize_select(Element& select, bool fallback = true);
// The host's multiple-select value remains a comma-separated list.
void set_select_value(Element& select, std::string_view value);
void reset_form(Element& form);

// DOM mutation hooks. No work is performed during idle frames.
void form_children_changed(Node& parent);
void form_subtree_inserted(Node& node);
} // namespace weva
