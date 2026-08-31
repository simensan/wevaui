#include "weva/css_rule.h"

#include <algorithm>

namespace weva {

namespace {

std::string ascii_lower(std::string_view s) {
    std::string out(s);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

std::string trim(std::string_view s) {
    auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    std::size_t b = 0, e = s.size();
    while (b < e && ws(s[b])) ++b;
    while (e > b && ws(s[e - 1])) --e;
    return std::string(s.substr(b, e - b));
}

std::string escape_string(std::string_view s) {
    std::string out;
    out.reserve(s.size());
    for (char c : s) {
        if (c == '"') out += "\\\"";
        else if (c == '\\') out += "\\\\";
        else if (c == '\n') out += "\\n";
        else out += c;
    }
    return out;
}

// Hard cap on rule-nesting recursion. ~20,000 nested `@media all{` (about
// 200KB of hostile CSS) drove the mutual recursion into an uncatchable stack
// overflow. Real sheets nest a handful of levels; past the cap the rule is
// skipped.
constexpr int kMaxRuleDepth = 128;

struct Ctx {
    const std::vector<CssToken>* tokens;
    bool strict;
    CssParseError* error;
    std::size_t index = 0;
    int rule_depth = 0;
    bool failed = false;

    const CssToken& current() const { return (*tokens)[index]; }
    // Clamps at the Eof token, matching C#'s `if (index < Count - 1) index++`.
    void advance() { if (index + 1 < tokens->size()) ++index; }
    bool is_eof() const { return current().kind == CssTokenKind::Eof; }

    void skip_whitespace() {
        while ((*tokens)[index].kind == CssTokenKind::Whitespace) {
            if (index + 1 >= tokens->size()) break;
            ++index;
        }
    }

    bool fail(std::string_view msg, const CssToken& at) {
        failed = true;
        if (error) *error = CssParseError{std::string(msg), at.line, at.column};
        return false;
    }
};

RulePtr parse_rule(Ctx& ctx, bool at_rule);
void parse_rule_body(Ctx& ctx, std::vector<Declaration>* decls,
                     std::vector<RulePtr>* nested, const CssToken& open_tok);

// --- value-text reconstruction -----------------------------------------------

void skip_declaration(Ctx& ctx) {
    int paren = 0, brace = 0;
    while (!ctx.is_eof()) {
        const CssToken& t = ctx.current();
        if (t.kind == CssTokenKind::Semicolon && paren == 0 && brace == 0) { ctx.advance(); return; }
        if (t.kind == CssTokenKind::RBrace && paren == 0 && brace == 0) return;
        if (t.kind == CssTokenKind::LBrace) ++brace;
        if (t.kind == CssTokenKind::RBrace && brace > 0) --brace;
        if (t.kind == CssTokenKind::LParen || t.kind == CssTokenKind::Function) ++paren;
        if (t.kind == CssTokenKind::RParen && paren > 0) --paren;
        ctx.advance();
    }
}

void skip_to_next_rule(Ctx& ctx) {
    int depth = 0;
    while (!ctx.is_eof()) {
        const CssToken& t = ctx.current();
        if (t.kind == CssTokenKind::LBrace) { ++depth; ctx.advance(); continue; }
        if (t.kind == CssTokenKind::RBrace) {
            ctx.advance();
            if (depth == 0) return;
            --depth;
            if (depth == 0) return;
            continue;
        }
        if (t.kind == CssTokenKind::Semicolon && depth == 0) { ctx.advance(); return; }
        ctx.advance();
    }
}

// Finds the LAST top-level '!' — C#'s loop keeps overwriting `candidate`
// rather than breaking, so `a !x !important` strips at the second bang.
int find_top_level_important_bang(const std::string& v) {
    int paren = 0, bracket = 0;
    char quote = '\0';
    int candidate = -1;
    for (std::size_t i = 0; i < v.size(); ++i) {
        char c = v[i];
        if (quote != '\0') {
            if (c == '\\' && i + 1 < v.size()) { ++i; continue; }
            if (c == quote) quote = '\0';
            continue;
        }
        if (c == '"' || c == '\'') { quote = c; continue; }
        if (c == '(') ++paren;
        else if (c == ')' && paren > 0) --paren;
        else if (c == '[') ++bracket;
        else if (c == ']' && bracket > 0) --bracket;
        else if (c == '!' && paren == 0 && bracket == 0) candidate = static_cast<int>(i);
    }
    return candidate;
}

bool strip_important(std::string* value) {
    int bang = find_top_level_important_bang(*value);
    if (bang < 0) return false;
    std::string after = trim(std::string_view(*value).substr(static_cast<std::size_t>(bang) + 1));
    if (ascii_lower(after) != "important") return false;
    std::string head = value->substr(0, static_cast<std::size_t>(bang));
    // TrimEnd only — leading whitespace was already removed by the caller's trim.
    std::size_t e = head.size();
    while (e > 0 && (head[e-1]==' '||head[e-1]=='\t'||head[e-1]=='\n'||head[e-1]=='\r'||head[e-1]=='\f')) --e;
    value->assign(head, 0, e);
    return true;
}

bool try_parse_declaration(Ctx& ctx, std::vector<Declaration>* out) {
    ctx.skip_whitespace();
    if (ctx.is_eof()) return false;
    const CssToken& name_tok = ctx.current();
    if (name_tok.kind != CssTokenKind::Ident) return false;

    std::string property = ascii_lower(name_tok.text);
    ctx.advance();
    ctx.skip_whitespace();
    if (ctx.is_eof() || ctx.current().kind != CssTokenKind::Colon) return false;
    ctx.advance();

    std::string sb;
    int paren = 0, bracket = 0;
    bool saw_non_ws = false;
    while (!ctx.is_eof()) {
        const CssToken& t = ctx.current();
        if (t.kind == CssTokenKind::Semicolon && paren == 0 && bracket == 0) break;
        if (t.kind == CssTokenKind::RBrace && paren == 0 && bracket == 0) break;
        if (t.kind == CssTokenKind::LBrace && paren == 0 && bracket == 0) return false;
        if (t.kind == CssTokenKind::LParen || t.kind == CssTokenKind::Function) ++paren;
        if (t.kind == CssTokenKind::RParen) --paren;
        if (t.kind == CssTokenKind::LBracket) ++bracket;
        if (t.kind == CssTokenKind::RBracket) --bracket;
        if (t.kind != CssTokenKind::Whitespace) saw_non_ws = true;
        sb += css_token_source(t);
        ctx.advance();
    }
    if (!ctx.is_eof() && ctx.current().kind == CssTokenKind::Semicolon) ctx.advance();

    std::string raw = trim(sb);
    bool important = strip_important(&raw);
    if (!saw_non_ws) return false;

    Declaration d;
    d.property = std::move(property);
    d.value_text = std::move(raw);
    d.important = important;
    out->push_back(std::move(d));
    return true;
}

// --- selector / prelude reading ----------------------------------------------

void add_selector(std::vector<std::string>* list, const std::string& sb) {
    std::string s = trim(sb);
    if (!s.empty()) list->push_back(std::move(s));
}

std::vector<std::string> read_selector_list(Ctx& ctx) {
    std::vector<std::string> result;
    std::string sb;
    int paren = 0, bracket = 0;
    while (!ctx.is_eof()) {
        const CssToken& t = ctx.current();
        if (paren == 0 && bracket == 0 &&
            (t.kind == CssTokenKind::LBrace || t.kind == CssTokenKind::RBrace ||
             t.kind == CssTokenKind::Semicolon)) {
            break;
        }
        if (t.kind == CssTokenKind::Comma && paren == 0 && bracket == 0) {
            add_selector(&result, sb);
            sb.clear();
            ctx.advance();
            continue;
        }
        if (t.kind == CssTokenKind::LParen || t.kind == CssTokenKind::Function) ++paren;
        if (t.kind == CssTokenKind::RParen) --paren;
        if (t.kind == CssTokenKind::LBracket) ++bracket;
        if (t.kind == CssTokenKind::RBracket) --bracket;
        sb += css_token_source(t);
        ctx.advance();
    }
    add_selector(&result, sb);
    return result;
}

std::string read_prelude_text(Ctx& ctx) {
    std::string sb;
    int paren = 0;
    while (!ctx.is_eof()) {
        const CssToken& t = ctx.current();
        if (paren == 0 && (t.kind == CssTokenKind::LBrace || t.kind == CssTokenKind::RBrace ||
                           t.kind == CssTokenKind::Semicolon)) {
            break;
        }
        if (t.kind == CssTokenKind::LParen || t.kind == CssTokenKind::Function) ++paren;
        if (t.kind == CssTokenKind::RParen) --paren;
        sb += css_token_source(t);
        ctx.advance();
    }
    return trim(sb);
}

// --- nested-rule lookahead ----------------------------------------------------

// CSS Nesting: does the current token sequence start a nested rule (selector +
// `{`) or a declaration (ident + `:` + value)? `&` always starts a selector; a
// bare ident followed by `:` is ambiguous (`color:` vs `a:hover`) and is
// resolved by scanning forward to the first top-level `{`, `;` or `}`.
bool looks_like_nested_selector(const Ctx& ctx) {
    const auto& toks = *ctx.tokens;
    const CssToken& t = ctx.current();

    if (t.kind == CssTokenKind::Delim && t.text == "&") return true;
    if (t.kind == CssTokenKind::Hash || t.kind == CssTokenKind::LBracket ||
        t.kind == CssTokenKind::Colon) {
        return true;
    }
    if (t.kind == CssTokenKind::Delim) {
        if (t.text == "." || t.text == "*" || t.text == ">" || t.text == "+" || t.text == "~") {
            return true;
        }
    }
    if (t.kind != CssTokenKind::Ident) return false;

    std::size_t peek = ctx.index + 1;
    while (peek < toks.size() && toks[peek].kind == CssTokenKind::Whitespace) ++peek;
    if (peek >= toks.size()) return false;
    const CssToken& next = toks[peek];

    if (next.kind == CssTokenKind::Colon) {
        std::size_t p2 = peek + 1;
        if (p2 < toks.size() && toks[p2].kind == CssTokenKind::Colon) return true;  // `::`
        int depth = 0;
        for (std::size_t i = p2; i < toks.size(); ++i) {
            const CssToken& tk = toks[i];
            if (tk.kind == CssTokenKind::LParen || tk.kind == CssTokenKind::Function) ++depth;
            else if (tk.kind == CssTokenKind::RParen) --depth;
            else if (depth == 0) {
                if (tk.kind == CssTokenKind::LBrace) return true;
                if (tk.kind == CssTokenKind::Semicolon) return false;
                if (tk.kind == CssTokenKind::RBrace) return false;
                if (tk.kind == CssTokenKind::Eof) return false;
            }
        }
        return false;
    }
    if (next.kind == CssTokenKind::Delim &&
        (next.text == "." || next.text == ">" || next.text == "+" || next.text == "~")) {
        return true;
    }
    if (next.kind == CssTokenKind::Hash || next.kind == CssTokenKind::LBracket ||
        next.kind == CssTokenKind::Comma || next.kind == CssTokenKind::LBrace) {
        return true;
    }
    if (next.kind == CssTokenKind::Ident) return true;   // descendant combinator
    return false;
}

// --- rules --------------------------------------------------------------------

void parse_rule_body(Ctx& ctx, std::vector<Declaration>* decls,
                     std::vector<RulePtr>* nested, const CssToken& open_tok) {
    ctx.skip_whitespace();
    while (!ctx.is_eof() && ctx.current().kind != CssTokenKind::RBrace) {
        const CssToken decl_start = ctx.current();
        if (decl_start.kind == CssTokenKind::AtKeyword) {
            RulePtr inner = parse_rule(ctx, /*at_rule=*/true);
            if (ctx.failed) return;
            if (inner) nested->push_back(std::move(inner));
            ctx.skip_whitespace();
            continue;
        }
        if (looks_like_nested_selector(ctx)) {
            RulePtr child = parse_rule(ctx, /*at_rule=*/false);
            if (ctx.failed) return;
            if (child) nested->push_back(std::move(child));
            ctx.skip_whitespace();
            continue;
        }
        if (!try_parse_declaration(ctx, decls)) {
            if (ctx.strict) { ctx.fail("Invalid declaration", decl_start); return; }
            skip_declaration(ctx);
        }
        ctx.skip_whitespace();
    }
    if (ctx.is_eof()) {
        if (ctx.strict) ctx.fail("Unterminated declaration block", open_tok);
        return;
    }
    ctx.advance();
}

RulePtr parse_rule(Ctx& ctx, bool at_rule) {
    if (ctx.rule_depth >= kMaxRuleDepth) {
        // Rule-drop semantics: skip the whole rule rather than recursing.
        skip_to_next_rule(ctx);
        return nullptr;
    }
    ++ctx.rule_depth;
    struct Pop {
        Ctx& c;
        ~Pop() { --c.rule_depth; }
    } pop{ctx};

    if (at_rule) {
        const CssToken at_tok = ctx.current();
        auto rule = std::make_unique<GenericAtRule>();
        rule->name = ascii_lower(at_tok.text);
        ctx.advance();
        rule->prelude = read_prelude_text(ctx);

        if (ctx.is_eof()) return rule;                       // `@charset "x"` with no ';'
        if (ctx.current().kind == CssTokenKind::Semicolon) { // statement at-rule
            ctx.advance();
            return rule;
        }
        if (ctx.current().kind != CssTokenKind::LBrace) return rule;

        const CssToken brace = ctx.current();
        ctx.advance();
        rule->has_block = true;
        parse_rule_body(ctx, &rule->declarations, &rule->nested_rules, brace);
        if (ctx.failed) return nullptr;
        return rule;
    }

    const CssToken start = ctx.current();
    std::vector<std::string> selectors = read_selector_list(ctx);
    if (ctx.is_eof() || ctx.current().kind != CssTokenKind::LBrace) {
        if (ctx.strict) { ctx.fail("Expected '{' after selector list", start); return nullptr; }
        skip_to_next_rule(ctx);
        return nullptr;
    }
    const CssToken brace = ctx.current();
    ctx.advance();
    auto rule = std::make_unique<StyleRule>();
    rule->selectors = std::move(selectors);
    parse_rule_body(ctx, &rule->declarations, &rule->nested_rules, brace);
    if (ctx.failed) return nullptr;
    return rule;
}

} // namespace

std::string css_token_source(const CssToken& t) {
    switch (t.kind) {
        case CssTokenKind::Whitespace: return " ";
        case CssTokenKind::Ident:      return t.text;
        case CssTokenKind::Function:   return t.text + "(";
        case CssTokenKind::AtKeyword:  return "@" + t.text;
        case CssTokenKind::Hash:       return "#" + t.text;
        case CssTokenKind::String:     return "\"" + escape_string(t.text) + "\"";
        case CssTokenKind::Number:     return t.text;
        case CssTokenKind::Percentage: return t.text;
        case CssTokenKind::Dimension:  return t.text;
        case CssTokenKind::Url:        return "url(" + t.text + ")";
        case CssTokenKind::Delim:      return t.text;
        case CssTokenKind::Comma:      return ",";
        case CssTokenKind::Colon:      return ":";
        case CssTokenKind::Semicolon:  return ";";
        case CssTokenKind::LBrace:     return "{";
        case CssTokenKind::RBrace:     return "}";
        case CssTokenKind::LParen:     return "(";
        case CssTokenKind::RParen:     return ")";
        case CssTokenKind::LBracket:   return "[";
        case CssTokenKind::RBracket:   return "]";
        default:                       return "";
    }
}

bool parse_stylesheet(std::string_view source, bool strict, Stylesheet* out,
                      CssParseError* error) {
    out->rules.clear();

    std::vector<CssToken> tokens;
    CssTokenizer tokenizer(source, strict);
    if (!tokenizer.tokenize(&tokens, error)) return false;

    Ctx ctx{&tokens, strict, error};
    ctx.skip_whitespace();
    while (!ctx.is_eof()) {
        std::size_t before = ctx.index;
        bool at_rule = ctx.current().kind == CssTokenKind::AtKeyword;
        RulePtr rule = parse_rule(ctx, at_rule);
        if (ctx.failed) return false;
        if (rule) out->rules.push_back(std::move(rule));
        // Guard against a construct that consumes nothing: without this a
        // lenient-mode skip that lands back on the same token spins forever.
        if (ctx.index == before) ctx.advance();
        ctx.skip_whitespace();
    }
    return true;
}

bool parse_inline_declarations(std::string_view source, bool strict,
                               std::vector<Declaration>* out, CssParseError* error) {
    out->clear();

    std::vector<CssToken> tokens;
    CssTokenizer tokenizer(source, strict);
    if (!tokenizer.tokenize(&tokens, error)) return false;

    Ctx ctx{&tokens, strict, error};
    ctx.skip_whitespace();
    while (!ctx.is_eof()) {
        const CssToken decl_start = ctx.current();
        if (!try_parse_declaration(ctx, out)) {
            if (strict) return ctx.fail("Invalid declaration", decl_start);
            std::size_t before = ctx.index;
            skip_declaration(ctx);
            if (ctx.index == before) ctx.advance();
        }
        ctx.skip_whitespace();
    }
    return true;
}

} // namespace weva
