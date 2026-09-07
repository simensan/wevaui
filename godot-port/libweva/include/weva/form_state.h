#pragma once
#include "weva/dom.h"

namespace weva {
// Canonical HTML input type (unknown/missing -> text), allocation-free.
std::string_view form_input_type(const Element& input);
// Editable one-line values, including number/date fields. Their text stays
// centered even when the content box is shorter than the font.
bool form_is_text_entry(const Element& input);
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
