#include "weva/form_state.h"
#include "../../third_party/ada/ada.h"

namespace weva {
bool form_url_type_mismatch(const Element& e) {
    if (e.tag_name() != "input" || form_input_type(e) != "url") return false;
    const auto value = e.form_value();
    return !value.empty() && !ada::can_parse(value);
}
} // namespace weva
