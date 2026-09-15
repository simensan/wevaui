#pragma once
#include "weva/css_token.h"

#include <memory>
#include <string>
#include <vector>

// Ports Runtime/Css/Parsing/{Rule,StyleRule,Stylesheet,Declaration}.cs and the
// core of CssParser.cs.
//
// Scope of this file: style rules, declarations, and generic at-rules. The
// specialised at-rule types (@font-face, @property, @scope, @layer,
// @keyframes, @import) are deferred — they land as GenericAtRule with their
// prelude and body preserved, so nothing is lost from the token stream and
// they can be promoted to real types without re-parsing.

namespace weva {

class Element;

struct Declaration {
    std::string property;    // ASCII-lowercased
    std::string value_text;  // reconstructed source text, trimmed
    bool important = false;
};

enum class RuleKind { Style, At };

struct Rule {
    virtual ~Rule() = default;
    virtual RuleKind kind() const = 0;
    // Source stylesheet URL, relative to the document reader when appropriate.
    // Imports retain this even after their rules are spliced into another sheet.
    std::string source_url;
};

using RulePtr = std::unique_ptr<Rule>;

struct StyleRule : Rule {
    RuleKind kind() const override { return RuleKind::Style; }

    std::vector<std::string> selectors;
    std::vector<Declaration> declarations;
    // Nested style/group rules and intervening declaration runs, in order.
    // The cascade resolves selectors against their nearest style-rule parent.
    std::vector<RulePtr> nested_rules;
    // A declaration run after a nested rule copies its parent's selectors
    // exactly, including pseudo-elements and each selector's specificity.
    bool nested_declarations = false;
};

struct GenericAtRule : Rule {
    RuleKind kind() const override { return RuleKind::At; }

    std::string name;      // ASCII-lowercased, without '@'
    std::string prelude;   // reconstructed source text, trimmed
    bool has_block = false;
    // Populated for at-rules with a block. Declarations and nested rules are
    // kept separate exactly as a style rule body is.
    std::vector<Declaration> declarations;
    std::vector<RulePtr> nested_rules;
};

struct Stylesheet {
    std::vector<RulePtr> rules;
    std::string source_url;
    // Borrowed parent of an inline <style>, for @scope with no explicit root.
    // Unowned sheets use the document element. The owner outlives the sheet.
    const Element* scope_root = nullptr;
};

// Mirrors CssParser.Parse. In strict mode a malformed construct fails the
// parse and fills `error`; otherwise the offending rule or declaration is
// skipped and the rest of the sheet survives.
bool parse_stylesheet(std::string_view source, bool strict, Stylesheet* out,
                      CssParseError* error);

// Mirrors CssParser.ParseInlineDeclarations for a style="" attribute body.
bool parse_inline_declarations(std::string_view source, bool strict,
                               std::vector<Declaration>* out, CssParseError* error);

// Reconstructs a token's source text. Exposed because declaration values are
// stored as reconstructed text, so any drift here changes every value the
// cascade sees.
std::string css_token_source(const CssToken& t);

} // namespace weva
