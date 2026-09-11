#include "weva/selector.h"

#include <algorithm>
#include <cstdlib>

namespace weva {

Specificity PseudoClassSelector::specificity() const {
    switch (kind) {
        // CSS Selectors L4 §5.7: :where() contributes nothing.
        case PseudoClassKind::Where:
            return Specificity::zero();
        // :not(), :is() and :has() take the highest specificity in their list.
        case PseudoClassKind::Not:
        case PseudoClassKind::Is:
        case PseudoClassKind::Has: {
            Specificity m = Specificity::zero();
            for (const auto& c : inner_list) m = Specificity::max(m, c->specificity());
            return m;
        }
        // §6.6.5: :nth-child(An+B of S) is (0,1,0) PLUS the max of S.
        case PseudoClassKind::NthChild:
        case PseudoClassKind::NthLastChild: {
            Specificity base{0, 1, 0};
            if (nth_of_filter.empty()) return base;
            Specificity m = Specificity::zero();
            for (const auto& c : nth_of_filter) m = Specificity::max(m, c->specificity());
            return Specificity::add(base, m);
        }
        default:
            return {0, 1, 0};
    }
}

Specificity CompoundSelector::specificity() const {
    Specificity s = Specificity::zero();
    for (const auto& p : parts) s = Specificity::add(s, p->specificity());
    if (has_pseudo_element) s = Specificity::add(s, Specificity{0, 0, 1});
    return s;
}

Specificity CompoundSequence::specificity() const {
    Specificity s = Specificity::zero();
    for (const auto& c : compounds) s = Specificity::add(s, c.specificity());
    return s;
}

const std::string* CompoundSequence::pseudo_element() const {
    if (compounds.empty()) return nullptr;
    const CompoundSelector& last = compounds.back();
    return last.has_pseudo_element ? &last.pseudo_element : nullptr;
}

namespace {

bool is_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}
bool is_digit(char c) { return c >= '0' && c <= '9'; }
bool is_ident_start(char c) {
    auto u = static_cast<unsigned char>(c);
    return (u >= 'a' && u <= 'z') || (u >= 'A' && u <= 'Z') || u == '_' || u == '-' || u >= 0x80;
}
bool is_ident_char(char c) { return is_ident_start(c) || is_digit(c); }

std::string ascii_lower(std::string_view s) {
    std::string o(s);
    for (char& c : o) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return o;
}

std::string trim(std::string_view s) {
    std::size_t b = 0, e = s.size();
    while (b < e && is_ws(s[b])) ++b;
    while (e > b && is_ws(s[e - 1])) --e;
    return std::string(s.substr(b, e - b));
}

struct PseudoEntry { const char* name; PseudoClassKind kind; };
const PseudoEntry kPseudoClasses[] = {
    {"first-child", PseudoClassKind::FirstChild}, {"last-child", PseudoClassKind::LastChild},
    {"only-child", PseudoClassKind::OnlyChild}, {"first-of-type", PseudoClassKind::FirstOfType},
    {"last-of-type", PseudoClassKind::LastOfType}, {"only-of-type", PseudoClassKind::OnlyOfType},
    {"nth-child", PseudoClassKind::NthChild}, {"nth-last-child", PseudoClassKind::NthLastChild},
    {"nth-of-type", PseudoClassKind::NthOfType}, {"nth-last-of-type", PseudoClassKind::NthLastOfType},
    {"empty", PseudoClassKind::Empty}, {"not", PseudoClassKind::Not},
    {"is", PseudoClassKind::Is}, {"where", PseudoClassKind::Where}, {"has", PseudoClassKind::Has},
    {"lang", PseudoClassKind::Lang}, {"dir", PseudoClassKind::Dir},
    {"link", PseudoClassKind::Link}, {"visited", PseudoClassKind::Visited},
    {"any-link", PseudoClassKind::AnyLink}, {"target", PseudoClassKind::Target},
    {"scope", PseudoClassKind::Scope}, {"hover", PseudoClassKind::Hover},
    {"focus", PseudoClassKind::Focus}, {"focus-visible", PseudoClassKind::FocusVisible},
    {"focus-within", PseudoClassKind::FocusWithin}, {"active", PseudoClassKind::Active},
    {"disabled", PseudoClassKind::Disabled}, {"enabled", PseudoClassKind::Enabled},
    {"checked", PseudoClassKind::Checked}, {"required", PseudoClassKind::Required},
    {"optional", PseudoClassKind::Optional}, {"read-only", PseudoClassKind::ReadOnly},
    {"read-write", PseudoClassKind::ReadWrite}, {"valid", PseudoClassKind::Valid},
    {"invalid", PseudoClassKind::Invalid}, {"in-range", PseudoClassKind::InRange},
    {"out-of-range", PseudoClassKind::OutOfRange}, {"user-valid", PseudoClassKind::UserValid},
    {"user-invalid", PseudoClassKind::UserInvalid}, {"default", PseudoClassKind::Default},
    {"placeholder-shown", PseudoClassKind::PlaceholderShown}, {"root", PseudoClassKind::Root},
    {"popover-open", PseudoClassKind::PopoverOpen}, {"modal", PseudoClassKind::Modal},
    {"autofill", PseudoClassKind::Autofill},
};

bool pseudo_class_from_name(std::string_view n, PseudoClassKind* out) {
    for (const auto& e : kPseudoClasses) {
        if (n == e.name) { *out = e.kind; return true; }
    }
    return false;
}

bool is_known_pseudo_element(std::string_view n) {
    static const char* known[] = {
        "before", "after", "first-line", "first-letter", "marker", "placeholder",
        "selection", "backdrop", "file-selector-button",
        "-webkit-scrollbar", "-webkit-scrollbar-thumb", "-webkit-scrollbar-track",
        "-webkit-scrollbar-track-piece", "-webkit-scrollbar-corner", "-webkit-scrollbar-button",
    };
    for (const char* k : known) {
        if (n == k) return true;
    }
    return false;
}

class Parser {
public:
    Parser(std::string_view src, SelectorParseError* err) : src_(src), err_(err) {}

    bool at_end() const { return pos_ >= src_.size(); }
    char peek() const { return pos_ < src_.size() ? src_[pos_] : '\0'; }
    char peek_at(std::size_t o) const { return pos_ + o < src_.size() ? src_[pos_ + o] : '\0'; }
    void advance() { if (pos_ < src_.size()) ++pos_; }
    int column() const { return static_cast<int>(pos_) + 1; }

    void skip_ws() { while (!at_end() && is_ws(peek())) advance(); }
    bool consume_ws() {
        bool any = false;
        while (!at_end() && is_ws(peek())) { advance(); any = true; }
        return any;
    }

    bool fail(std::string msg) {
        failed_ = true;
        if (err_) *err_ = SelectorParseError{std::move(msg), column()};
        return false;
    }
    bool failed() const { return failed_; }

    std::string read_ident() {
        std::string s;
        while (!at_end() && is_ident_char(peek())) { s.push_back(peek()); advance(); }
        return s;
    }

    bool parse_sequence_list(std::vector<CompoundSequence>* out) {
        skip_ws();
        CompoundSequence first;
        if (!parse_sequence(&first)) return false;
        out->push_back(std::move(first));
        for (;;) {
            skip_ws();
            if (at_end() || peek() != ',') break;
            advance();
            skip_ws();
            CompoundSequence next;
            if (!parse_sequence(&next)) return false;
            out->push_back(std::move(next));
        }
        return true;
    }

    bool parse_sequence(CompoundSequence* seq) {
        skip_ws();   // the C# ParseSequence leads with SkipWhitespace
        CompoundSelector first;
        if (!parse_compound(&first)) return false;
        seq->compounds.push_back(std::move(first));
        for (;;) {
            bool had_ws = consume_ws();
            if (at_end()) break;
            char c = peek();
            Combinator comb;
            if (c == '>' || c == '+' || c == '~') {
                advance();
                skip_ws();
                comb = c == '>'   ? Combinator::Child
                     : c == '+'   ? Combinator::AdjacentSibling
                                  : Combinator::GeneralSibling;
            } else if (c == ',' || c == ')') {
                break;
            } else if (had_ws) {
                comb = Combinator::Descendant;
            } else {
                break;
            }
            if (at_end() || peek() == ',' || peek() == ')') {
                return fail("Expected selector after combinator");
            }
            if (peek() == '>' || peek() == '+' || peek() == '~') {
                return fail(std::string("Unexpected combinator '") + peek() + "'");
            }
            CompoundSelector next;
            if (!parse_compound(&next)) return false;
            seq->combinators.push_back(comb);
            seq->compounds.push_back(std::move(next));
        }
        if (seq->compounds.empty()) return fail("Empty selector");
        return true;
    }

    // A relative-selector-list (:has argument): each item may begin with a
    // combinator. The leading relation is encoded by prepending a synthetic
    // universal "anchor" compound, so the matcher can strip it and walk the
    // relation between the :has() subject and the matched element.
    bool parse_relative_sequence(CompoundSequence* seq) {
        skip_ws();
        Combinator leading = Combinator::Descendant;
        if (!at_end()) {
            char c = peek();
            if (c == '>' || c == '+' || c == '~') {
                advance();
                skip_ws();
                leading = c == '>' ? Combinator::Child
                        : c == '+' ? Combinator::AdjacentSibling
                                   : Combinator::GeneralSibling;
            }
        }
        if (!parse_sequence(seq)) return false;
        CompoundSelector anchor;
        anchor.parts.push_back(std::make_unique<UniversalSelector>());
        seq->compounds.insert(seq->compounds.begin(), std::move(anchor));
        seq->combinators.insert(seq->combinators.begin(), leading);
        return true;
    }

    bool parse_relative_selector_list(std::vector<CompoundSequence>* out) {
        skip_ws();
        CompoundSequence first;
        if (!parse_relative_sequence(&first)) return false;
        out->push_back(std::move(first));
        for (;;) {
            skip_ws();
            if (at_end() || peek() != ',') break;
            advance();
            skip_ws();
            CompoundSequence next;
            if (!parse_relative_sequence(&next)) return false;
            out->push_back(std::move(next));
        }
        return true;
    }

    bool inside_has_ = false;

private:
    bool parse_compound(CompoundSelector* compound);
    bool parse_attribute(CompoundSelector* compound);
    bool parse_pseudo_class(CompoundSelector* compound);
    bool parse_nth(NthExpression* out, std::vector<std::unique_ptr<CompoundSequence>>* of_filter);

    std::string_view src_;
    SelectorParseError* err_;
    std::size_t pos_ = 0;
    bool failed_ = false;
};

bool Parser::parse_compound(CompoundSelector* compound) {
    bool any = false;
    while (!at_end()) {
        char c = peek();

        if (c == ':' && peek_at(1) == ':') {
            advance(); advance();
            std::string name = read_ident();
            if (name.empty()) return fail("Expected pseudo-element name");
            if (compound->has_pseudo_element) return fail("Multiple pseudo-elements");
            std::string lower = ascii_lower(name);
            if (!is_known_pseudo_element(lower)) {
                return fail("Unknown pseudo-element '::" + name + "'");
            }
            // §3.3: pseudo-element names are ASCII case-insensitive; store the
            // canonical lowercase so matching against the cascade's lowercase
            // name works when the author wrote ::BEFORE.
            compound->pseudo_element = lower;
            compound->has_pseudo_element = true;
            any = true;
            continue;
        }

        if (c == '*') {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            advance();
            // `*|name` — namespace prefix. No @namespace machinery: drop the
            // prefix and match the local name. Guard '|=' so the attribute
            // dash-match operator is never mistaken for a prefix.
            if (peek() == '|' && peek_at(1) != '=') {
                advance();
                if (peek() == '*') {
                    advance();
                    compound->parts.push_back(std::make_unique<UniversalSelector>());
                } else {
                    std::string local = read_ident();
                    if (local.empty()) return fail("Expected local name after '*|'");
                    auto t = std::make_unique<TypeSelector>();
                    t->tag_name = ascii_lower(local);
                    compound->parts.push_back(std::move(t));
                }
            } else {
                compound->parts.push_back(std::make_unique<UniversalSelector>());
            }
            any = true;
            continue;
        }

        if (c == '#') {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            advance();
            std::string id = read_ident();
            if (id.empty()) return fail("Expected identifier after '#'");
            auto s = std::make_unique<IdSelector>();
            s->id = std::move(id);
            compound->parts.push_back(std::move(s));
            any = true;
            continue;
        }

        if (c == '.') {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            advance();
            std::string cn = read_ident();
            if (cn.empty()) return fail("Expected identifier after '.'");
            auto s = std::make_unique<ClassSelector>();
            s->class_name = std::move(cn);
            compound->parts.push_back(std::move(s));
            any = true;
            continue;
        }

        if (c == '[') {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            if (!parse_attribute(compound)) return false;
            any = true;
            continue;
        }

        if (c == ':') {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            if (!parse_pseudo_class(compound)) return false;
            any = true;
            continue;
        }

        if (is_ident_start(c)) {
            if (compound->has_pseudo_element) return fail("Selector after pseudo-element");
            if (any) break;   // a type selector may only lead a compound
            std::string name = read_ident();
            if (peek() == '|' && peek_at(1) != '=') {
                advance();
                if (peek() == '*') {
                    advance();
                    compound->parts.push_back(std::make_unique<UniversalSelector>());
                    any = true;
                    continue;
                }
                name = read_ident();
                if (name.empty()) return fail("Expected local name after '|'");
            }
            auto t = std::make_unique<TypeSelector>();
            t->tag_name = ascii_lower(name);
            compound->parts.push_back(std::move(t));
            any = true;
            continue;
        }

        break;
    }
    if (!any) return fail("Empty compound selector");
    return true;
}

bool Parser::parse_attribute(CompoundSelector* compound) {
    advance();   // '['
    skip_ws();
    std::string name = read_ident();
    if (name.empty()) return fail("Expected attribute name");
    // `[ns|attr]` — drop the namespace prefix, same as type selectors.
    if (peek() == '|' && peek_at(1) != '=') {
        advance();
        name = read_ident();
        if (name.empty()) return fail("Expected attribute local name after '|'");
    }
    skip_ws();

    auto sel = std::make_unique<AttributeSelector>();
    sel->name = ascii_lower(name);

    if (peek() == ']') {
        advance();
        sel->op = AttributeOperator::Exists;
        compound->parts.push_back(std::move(sel));
        return true;
    }

    char c = peek();
    AttributeOperator op;
    if (c == '=') { advance(); op = AttributeOperator::Equals; }
    else if (peek_at(1) == '=') {
        switch (c) {
            case '~': op = AttributeOperator::WhitespaceContains; break;
            case '|': op = AttributeOperator::DashMatch; break;
            case '^': op = AttributeOperator::Prefix; break;
            case '$': op = AttributeOperator::Suffix; break;
            case '*': op = AttributeOperator::Substring; break;
            default: return fail(std::string("Unknown attribute operator '") + c + "'");
        }
        advance(); advance();
    } else {
        return fail(std::string("Unexpected character '") + c + "' in attribute selector");
    }
    sel->op = op;

    skip_ws();
    std::string value;
    if (peek() == '"' || peek() == '\'') {
        char q = peek();
        advance();
        while (!at_end() && peek() != q) {
            if (peek() == '\\') { advance(); if (at_end()) break; }
            value.push_back(peek());
            advance();
        }
        if (at_end()) return fail("Unterminated attribute value");
        advance();
    } else {
        value = read_ident();
        if (value.empty()) return fail("Expected attribute value");
    }
    sel->value = value;
    if (op == AttributeOperator::DashMatch) sel->dash_prefix = value + "-";

    skip_ws();
    // [attr=value i] / [attr=value s] — the case-sensitivity flag.
    if (peek() == 'i' || peek() == 'I') { sel->case_insensitive = true; advance(); skip_ws(); }
    else if (peek() == 's' || peek() == 'S') { advance(); skip_ws(); }

    if (peek() != ']') return fail("Expected ']'");
    advance();
    compound->parts.push_back(std::move(sel));
    return true;
}

bool Parser::parse_nth(NthExpression* out, std::vector<std::unique_ptr<CompoundSequence>>* of_filter) {
    skip_ws();
    // odd / even
    if (is_ident_start(peek())) {
        std::size_t save = pos_;
        std::string word = ascii_lower(read_ident());
        if (word == "odd")  { *out = NthExpression{2, 1}; }
        else if (word == "even") { *out = NthExpression{2, 0}; }
        else { pos_ = save; goto numeric; }
        goto of_clause;
    }
numeric: {
        int a = 0, b = 0;
        bool has_n = false;
        int sign = 1;
        if (peek() == '+') { advance(); }
        else if (peek() == '-') { sign = -1; advance(); }

        std::string digits;
        while (is_digit(peek())) { digits.push_back(peek()); advance(); }

        if (peek() == 'n' || peek() == 'N') {
            advance();
            has_n = true;
            a = digits.empty() ? sign : sign * std::atoi(digits.c_str());
            skip_ws();
            int bsign = 0;
            if (peek() == '+') { bsign = 1; advance(); }
            else if (peek() == '-') { bsign = -1; advance(); }
            if (bsign != 0) {
                skip_ws();
                std::string bd;
                while (is_digit(peek())) { bd.push_back(peek()); advance(); }
                if (bd.empty()) return fail("Expected number after sign in nth expression");
                b = bsign * std::atoi(bd.c_str());
            }
        } else {
            if (digits.empty()) return fail("Expected nth expression");
            b = sign * std::atoi(digits.c_str());
        }
        (void)has_n;
        *out = NthExpression{a, b};
    }
of_clause:
    skip_ws();
    // `of S` — §6.6.5's filtered nth.
    if ((peek() == 'o' || peek() == 'O') && (peek_at(1) == 'f' || peek_at(1) == 'F') &&
        (is_ws(peek_at(2)) || peek_at(2) == '\0')) {
        advance(); advance();
        skip_ws();
        std::vector<CompoundSequence> list;
        if (!parse_sequence_list(&list)) return false;
        for (auto& s : list) of_filter->push_back(std::make_unique<CompoundSequence>(std::move(s)));
    }
    return true;
}

bool Parser::parse_pseudo_class(CompoundSelector* compound) {
    advance();   // ':'
    std::string name = read_ident();
    if (name.empty()) return fail("Expected pseudo-class name");
    std::string lower = ascii_lower(name);

    // Legacy single-colon pseudo-elements (:before, :after, :first-line,
    // :first-letter) are pseudo-ELEMENTS per CSS 2.1 compatibility.
    if (lower == "before" || lower == "after" || lower == "first-line" ||
        lower == "first-letter") {
        if (compound->has_pseudo_element) return fail("Multiple pseudo-elements");
        compound->pseudo_element = lower;
        compound->has_pseudo_element = true;
        return true;
    }

    PseudoClassKind kind;
    if (!pseudo_class_from_name(lower, &kind)) {
        return fail("Unknown pseudo-class ':" + name + "'");
    }

    auto sel = std::make_unique<PseudoClassSelector>();
    sel->kind = kind;

    if (peek() != '(') {
        compound->parts.push_back(std::move(sel));
        return true;
    }
    advance();   // '('
    skip_ws();

    switch (kind) {
        case PseudoClassKind::NthChild:
        case PseudoClassKind::NthLastChild:
        case PseudoClassKind::NthOfType:
        case PseudoClassKind::NthLastOfType: {
            if (!parse_nth(&sel->nth, &sel->nth_of_filter)) return false;
            sel->has_nth = true;
            break;
        }
        case PseudoClassKind::Has: {
            // :has() may not nest inside :has() — the matcher would recurse
            // without a base case on a self-referential subject.
            if (inside_has_) return fail(":has() may not be nested inside :has()");
            inside_has_ = true;
            std::vector<CompoundSequence> list;
            bool ok = parse_relative_selector_list(&list);
            inside_has_ = false;
            if (!ok) return false;
            for (auto& s : list) {
                sel->inner_list.push_back(std::make_unique<CompoundSequence>(std::move(s)));
            }
            break;
        }
        case PseudoClassKind::Not:
        case PseudoClassKind::Is:
        case PseudoClassKind::Where: {
            std::vector<CompoundSequence> list;
            if (!parse_sequence_list(&list)) return false;
            for (auto& s : list) {
                sel->inner_list.push_back(std::make_unique<CompoundSequence>(std::move(s)));
            }
            break;
        }
        default: {
            // :lang(en), :dir(rtl) and anything else functional keep raw text.
            std::string arg;
            int depth = 0;
            while (!at_end()) {
                char c = peek();
                if (c == '(') ++depth;
                if (c == ')') { if (depth == 0) break; --depth; }
                arg.push_back(c);
                advance();
            }
            sel->argument = trim(arg);
            break;
        }
    }

    skip_ws();
    if (peek() != ')') return fail("Expected ')' closing ':" + name + "('");
    advance();
    compound->parts.push_back(std::move(sel));
    return true;
}

} // namespace

bool parse_selector(std::string_view text, CompiledSelector* out, SelectorParseError* error) {
    Parser p(text, error);
    CompoundSequence seq;
    if (!p.parse_sequence(&seq)) return false;
    p.skip_ws();
    if (!p.at_end()) {
        return p.fail(std::string("Unexpected character '") + p.peek() + "'");
    }
    out->sequence = std::move(seq);
    out->source_text = trim(text);
    return true;
}

bool parse_selector_list(std::string_view text, std::vector<CompiledSelector>* out,
                         SelectorParseError* error) {
    Parser p(text, error);
    std::vector<CompoundSequence> list;
    if (!p.parse_sequence_list(&list)) return false;
    p.skip_ws();
    if (!p.at_end()) {
        return p.fail(std::string("Unexpected character '") + p.peek() + "'");
    }
    for (auto& s : list) {
        CompiledSelector cs;
        cs.sequence = std::move(s);
        out->push_back(std::move(cs));
    }
    return true;
}

} // namespace weva
