#pragma once
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Selectors/{SimpleSelector,CompoundSelector,Specificity,
// NthExpression,Combinator,AttributeOperator,PseudoClassKind,ElementState,
// CompiledSelector,SelectorParser}.cs.
//
// Note the C# selector parser works on raw characters, not CSS tokens — it is
// handed the selector text already sliced out of the rule prelude. Ported the
// same way rather than routed through CssTokenizer, so escape and whitespace
// handling stay identical.
//
// Matching (SelectorMatcher.cs, 1069 LOC) is the next slice; this file is the
// model and the parser.

namespace weva {

struct Specificity {
    int a = 0, b = 0, c = 0;

    static Specificity zero() { return {}; }
    static Specificity add(Specificity x, Specificity y) {
        return {x.a + y.a, x.b + y.b, x.c + y.c};
    }
    int compare(const Specificity& o) const {
        if (a != o.a) return a < o.a ? -1 : 1;
        if (b != o.b) return b < o.b ? -1 : 1;
        if (c != o.c) return c < o.c ? -1 : 1;
        return 0;
    }
    static Specificity max(Specificity x, Specificity y) {
        return x.compare(y) >= 0 ? x : y;
    }
    friend bool operator==(const Specificity& x, const Specificity& y) {
        return x.a == y.a && x.b == y.b && x.c == y.c;
    }
    friend bool operator!=(const Specificity& x, const Specificity& y) { return !(x == y); }
};

struct NthExpression {
    int a = 0, b = 0;
    bool matches(int index) const {
        if (a == 0) return index == b;
        int diff = index - b;
        if (diff == 0) return true;
        if (a > 0) return diff > 0 && diff % a == 0;
        return diff < 0 && (-diff) % (-a) == 0;
    }
};

enum class Combinator { None, Descendant, Child, AdjacentSibling, GeneralSibling };

enum class AttributeOperator {
    Exists, Equals, WhitespaceContains, DashMatch, Prefix, Suffix, Substring
};

enum class PseudoClassKind {
    FirstChild, LastChild, OnlyChild, FirstOfType, LastOfType, OnlyOfType,
    NthChild, NthLastChild, NthOfType, NthLastOfType, Empty,
    Not, Is, Where, Has, Lang, Dir, Link, Visited, AnyLink, Target, Scope,
    Hover, Focus, FocusVisible, FocusWithin, Active, Disabled, Enabled,
    Checked, Required, Optional, ReadOnly, ReadWrite, Valid, Invalid,
    InRange, OutOfRange, UserValid, UserInvalid, Default, PlaceholderShown,
    Root, PopoverOpen, Modal, Autofill,
};

enum class ElementState : uint32_t {
    None = 0,
    Hover = 1u << 0, Focus = 1u << 1, FocusVisible = 1u << 2, FocusWithin = 1u << 3,
    Active = 1u << 4, Disabled = 1u << 5, Checked = 1u << 6, PlaceholderShown = 1u << 7,
    Root = 1u << 8, UserInteracted = 1u << 9, Target = 1u << 10,
    // Set when the UA pre-fills a control (password manager, autofill). Hosts
    // wire this themselves; the headless provider never sets it.
    Autofill = 1u << 11,
};
inline ElementState operator|(ElementState a, ElementState b) {
    return static_cast<ElementState>(static_cast<uint32_t>(a) | static_cast<uint32_t>(b));
}
inline bool has_state(ElementState set, ElementState bit) {
    return (static_cast<uint32_t>(set) & static_cast<uint32_t>(bit)) != 0;
}

struct CompoundSequence;

struct SimpleSelector {
    enum class Tag { Universal, Type, Id, Class, Attribute, PseudoClass };
    virtual ~SimpleSelector() = default;
    virtual Tag tag() const = 0;
    virtual Specificity specificity() const = 0;
};
using SimpleSelectorPtr = std::unique_ptr<SimpleSelector>;

struct UniversalSelector : SimpleSelector {
    Tag tag() const override { return Tag::Universal; }
    Specificity specificity() const override { return Specificity::zero(); }
};

struct TypeSelector : SimpleSelector {
    Tag tag() const override { return Tag::Type; }
    Specificity specificity() const override { return {0, 0, 1}; }
    std::string tag_name;
};

struct IdSelector : SimpleSelector {
    Tag tag() const override { return Tag::Id; }
    Specificity specificity() const override { return {1, 0, 0}; }
    std::string id;
};

struct ClassSelector : SimpleSelector {
    Tag tag() const override { return Tag::Class; }
    Specificity specificity() const override { return {0, 1, 0}; }
    std::string class_name;
};

struct AttributeSelector : SimpleSelector {
    Tag tag() const override { return Tag::Attribute; }
    Specificity specificity() const override { return {0, 1, 0}; }
    std::string name;
    AttributeOperator op = AttributeOperator::Exists;
    std::string value;
    std::string dash_prefix;      // value + "-", precomputed for DashMatch
    bool case_insensitive = false;
};

struct PseudoClassSelector : SimpleSelector {
    Tag tag() const override { return Tag::PseudoClass; }
    Specificity specificity() const override;

    PseudoClassKind kind = PseudoClassKind::Root;
    NthExpression nth;
    bool has_nth = false;
    std::vector<std::unique_ptr<CompoundSequence>> inner_list;      // :not/:is/:where/:has
    std::vector<std::unique_ptr<CompoundSequence>> nth_of_filter;   // :nth-child(An+B of S)
    std::string argument;                                           // :lang/:dir
};

struct CompoundSelector {
    std::vector<SimpleSelectorPtr> parts;
    std::string pseudo_element;   // empty when absent
    bool has_pseudo_element = false;
    Specificity specificity() const;
};

struct CompoundSequence {
    std::vector<CompoundSelector> compounds;
    std::vector<Combinator> combinators;   // combinators[i] joins compounds[i] to [i+1]
    Specificity specificity() const;
    const std::string* pseudo_element() const;
};

struct CompiledSelector {
    CompoundSequence sequence;
    std::string source_text;   // as authored, trimmed
    Specificity specificity() const { return sequence.specificity(); }
};

struct SelectorParseError {
    std::string message;
    int column = 0;
};

// Parses one complex selector. Returns false on trailing garbage or syntax error.
bool parse_selector(std::string_view text, CompiledSelector* out, SelectorParseError* error);
// Parses a comma-separated selector list.
bool parse_selector_list(std::string_view text, std::vector<CompiledSelector>* out,
                         SelectorParseError* error);

} // namespace weva
