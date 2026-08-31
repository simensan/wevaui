#pragma once
#include "weva/css_rule.h"

#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/AtPropertyRegistry.cs — the CSS Properties and
// Values API L1 `@property` rule:
//
//   @property --my-prop {
//     syntax: "<length>";
//     initial-value: 0px;
//     inherits: false;
//   }
//
// All three descriptors are required; a rule missing or misusing any of them is
// discarded. The registry is per-engine rather than global, so two stylesheets
// compiled into different engines cannot contaminate each other.

namespace weva {

struct PropertyDescriptor {
    std::string name;            // including the leading "--"
    std::string syntax;          // quotes stripped, e.g. "<length>"
    std::string initial_value;   // may legitimately be empty under `syntax: "*"`
    bool inherits = true;

    // Builds a descriptor from the three raw descriptor value texts. Each
    // argument is empty-optional when the descriptor was absent, which is
    // distinct from present-but-empty: `initial-value: ;` is valid under the
    // universal syntax, a missing `initial-value` is not.
    static std::optional<PropertyDescriptor> try_create(
        std::string_view name, const std::optional<std::string>& syntax,
        const std::optional<std::string>& initial_value,
        const std::optional<std::string>& inherits_text);

    // Validates a value against a syntax string, honouring `|` alternatives.
    // v1 covers the spec's primitives; `+` and `#` multipliers are stripped
    // rather than enforced, and an unrecognised component accepts anything.
    static bool validate(std::string_view syntax, std::string_view value);
};

class AtPropertyRegistry {
public:
    // Later registrations for the same name win, matching the cascade's
    // treatment of duplicate at-rules.
    void register_descriptor(const PropertyDescriptor& d);
    const PropertyDescriptor* find(std::string_view name) const;

    // Unregistered custom properties inherit (CSS Custom Properties L1 §2), so
    // this is false for anything the sheet did not declare.
    bool is_non_inheriting(std::string_view name) const;

    // Null when unregistered — callers read that as "no initial-value
    // constraint", which is not the same as an empty initial value.
    const std::string* initial_value(std::string_view name) const;

    // True for an unregistered property: with no declared syntax there is
    // nothing to violate.
    bool validate_value(std::string_view name, std::string_view value) const;

    int count() const { return static_cast<int>(descriptors_.size()); }
    const std::map<std::string, PropertyDescriptor>& all() const { return descriptors_; }
    void clear() { descriptors_.clear(); }

private:
    std::map<std::string, PropertyDescriptor> descriptors_;
};

// Reads an `@property` at-rule body into a descriptor. Returns empty when the
// rule is invalid and must be discarded.
//
// The C# validates inside CssParser and never constructs the rule object; this
// port lets the generic at-rule survive parsing and validates here instead, so
// the parser stays free of cascade-layer types. An `@property` rule has no
// other effect on the sheet, so the two are equivalent from the cascade's side.
std::optional<PropertyDescriptor> parse_at_property_rule(const GenericAtRule& rule);

} // namespace weva
