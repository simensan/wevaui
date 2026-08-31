#include "weva/dom.h"
#include "weva/selector.h"

#include <algorithm>

// Ports Runtime/Css/Selectors/SelectorMatcher.cs.
//
// Deferred, and reported as non-matching rather than guessed: the form-state
// pseudo-classes (:valid, :invalid, :in-range, :out-of-range, :required,
// :optional, :read-only, :read-write, :default, :user-valid, :user-invalid)
// and :popover-open / :modal. All of them need the Forms layer, which is not
// ported yet. Returning false is the honest answer; returning true would
// silently apply styles that should not apply.

namespace weva {

namespace {

bool is_ascii_ws(char c) {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
}

char lower_ascii(char c) { return (c >= 'A' && c <= 'Z') ? static_cast<char>(c + 32) : c; }

bool contains_token(std::string_view s, std::string_view token, bool ignore_case) {
    if (token.empty()) return false;
    std::size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && is_ascii_ws(s[i])) ++i;
        std::size_t start = i;
        while (i < s.size() && !is_ascii_ws(s[i])) ++i;
        if (i - start != token.size()) continue;
        bool match = true;
        for (std::size_t k = 0; k < token.size(); ++k) {
            char a = s[start + k], b = token[k];
            if (ignore_case) { a = lower_ascii(a); b = lower_ascii(b); }
            if (a != b) { match = false; break; }
        }
        if (match) return true;
    }
    return false;
}

// All attribute comparisons are CODE-POINT (ordinal), per CSS Selectors L4
// §6.3. C# pins StringComparison.Ordinal for exactly this reason: the
// culture-sensitive overloads fold Turkish dotted/dotless i and German ss/SS
// per locale, so a selector would behave differently by machine.
bool str_eq(std::string_view a, std::string_view b, bool ci) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (ci) { x = lower_ascii(x); y = lower_ascii(y); }
        if (x != y) return false;
    }
    return true;
}
bool starts_with(std::string_view s, std::string_view p, bool ci) {
    return s.size() >= p.size() && str_eq(s.substr(0, p.size()), p, ci);
}
bool ends_with(std::string_view s, std::string_view p, bool ci) {
    return s.size() >= p.size() && str_eq(s.substr(s.size() - p.size()), p, ci);
}
bool contains_sub(std::string_view s, std::string_view p, bool ci) {
    if (p.empty() || p.size() > s.size()) return false;
    for (std::size_t i = 0; i + p.size() <= s.size(); ++i) {
        if (str_eq(s.substr(i, p.size()), p, ci)) return true;
    }
    return false;
}

const Element* as_element(const Node* n) {
    return (n && n->is_element()) ? static_cast<const Element*>(n) : nullptr;
}
const Element* parent_element(const Element& e) { return as_element(e.parent()); }

const Element* prev_element_sibling(const Element& e) {
    const Node* p = e.parent();
    if (!p) return nullptr;
    const Element* prev = nullptr;
    for (const auto& c : p->children()) {
        if (c.get() == &e) return prev;
        if (c->is_element()) prev = static_cast<const Element*>(c.get());
    }
    return nullptr;
}

const Element* next_element_sibling(const Element& e) {
    const Node* p = e.parent();
    if (!p) return nullptr;
    bool seen = false;
    for (const auto& c : p->children()) {
        if (seen && c->is_element()) return static_cast<const Element*>(c.get());
        if (c.get() == &e) seen = true;
    }
    return nullptr;
}

// 1-based, counting elements only.
int child_index(const Element& e) {
    const Node* p = e.parent();
    if (!p) return 0;
    int i = 0;
    for (const auto& c : p->children()) {
        if (c->is_element()) ++i;
        if (c.get() == &e) return i;
    }
    return 0;
}

int child_index_from_end(const Element& e) {
    const Node* p = e.parent();
    if (!p) return 0;
    int total = 0, idx = 0;
    for (const auto& c : p->children()) {
        if (c->is_element()) ++total;
        if (c.get() == &e) idx = total;
    }
    return idx == 0 ? 0 : total - idx + 1;
}

int child_index_of_type(const Element& e, bool from_end) {
    const Node* p = e.parent();
    if (!p) return 0;
    int total = 0, idx = 0;
    for (const auto& c : p->children()) {
        const Element* ce = as_element(c.get());
        if (ce && ce->tag_name() == e.tag_name()) ++total;
        if (c.get() == &e) idx = total;
    }
    if (idx == 0) return 0;
    return from_end ? total - idx + 1 : idx;
}

bool is_root_element(const Element& e) {
    const Node* p = e.parent();
    return p && !p->is_element();   // parent is the Document
}

bool is_hyperlink(const Element& e) {
    const std::string& t = e.tag_name();
    return (t == "a" || t == "area" || t == "link") && e.has_attribute("href");
}

bool match_compound(const CompoundSelector& c, const Element& e,
                    const ElementStateProvider& st, const Element* scope,
                    bool allow_pseudo_element = false);
bool match_sequence(const CompoundSequence& seq, const Element& e,
                    const ElementStateProvider& st, const Element* scope,
                    bool allow_pseudo_on_subject = false);
bool match_has(const std::vector<std::unique_ptr<CompoundSequence>>& list,
               const Element& subject, const ElementStateProvider& st, const Element* scope);

bool match_attribute(const AttributeSelector& at, const Element& e) {
    if (!e.has_attribute(at.name)) return false;
    std::string_view v = e.get_attribute(at.name);
    const bool ci = at.case_insensitive;
    switch (at.op) {
        case AttributeOperator::Exists: return true;
        case AttributeOperator::Equals: return str_eq(v, at.value, ci);
        case AttributeOperator::WhitespaceContains:
            if (at.value.empty()) return false;
            return contains_token(v, at.value, ci);
        case AttributeOperator::DashMatch:
            // §6.3.2: [attr|=""] matches only an empty value. The precomputed
            // dash prefix for an empty value is just "-", which would
            // otherwise match every value starting with '-'.
            if (at.value.empty()) return v.empty();
            if (str_eq(v, at.value, ci)) return true;
            return starts_with(v, at.dash_prefix, ci);
        case AttributeOperator::Prefix:
            return !at.value.empty() && starts_with(v, at.value, ci);
        case AttributeOperator::Suffix:
            return !at.value.empty() && ends_with(v, at.value, ci);
        case AttributeOperator::Substring:
            return !at.value.empty() && contains_sub(v, at.value, ci);
    }
    return false;
}

bool matches_filter(const Element& e,
                    const std::vector<std::unique_ptr<CompoundSequence>>& filter,
                    const ElementStateProvider& st, const Element* scope) {
    for (const auto& f : filter) {
        if (match_sequence(*f, e, st, scope)) return true;
    }
    return false;
}

int filtered_child_index(const Element& e,
                         const std::vector<std::unique_ptr<CompoundSequence>>& filter,
                         const ElementStateProvider& st, const Element* scope, bool from_end) {
    const Node* p = e.parent();
    if (!p) return 0;
    std::vector<const Element*> matching;
    for (const auto& c : p->children()) {
        const Element* ce = as_element(c.get());
        if (ce && matches_filter(*ce, filter, st, scope)) matching.push_back(ce);
    }
    for (std::size_t i = 0; i < matching.size(); ++i) {
        if (matching[i] == &e) {
            return from_end ? static_cast<int>(matching.size() - i)
                            : static_cast<int>(i + 1);
        }
    }
    return 0;
}

bool matches_language(const Element& e, std::string_view arg) {
    // Walks up for the nearest lang attribute; matches exactly or on a
    // BCP-47 subtag boundary ("en" matches "en-GB", not "english").
    for (const Element* n = &e; n; n = parent_element(*n)) {
        if (!n->has_attribute("lang")) continue;
        std::string_view v = n->get_attribute("lang");
        if (str_eq(v, arg, true)) return true;
        return v.size() > arg.size() && v[arg.size()] == '-' &&
               str_eq(v.substr(0, arg.size()), arg, true);
    }
    return false;
}

bool matches_direction(const Element& e, std::string_view arg) {
    for (const Element* n = &e; n; n = parent_element(*n)) {
        if (!n->has_attribute("dir")) continue;
        return str_eq(n->get_attribute("dir"), arg, true);
    }
    return str_eq("ltr", arg, true);   // documents default to ltr
}

bool match_pseudo(const PseudoClassSelector& pc, const Element& e,
                  const ElementStateProvider& st, const Element* scope) {
    const bool has_parent_element = parent_element(e) != nullptr;
    auto bit = [&](ElementState b) { return has_state(st.state_of(e), b); };

    switch (pc.kind) {
        case PseudoClassKind::FirstChild:
            return prev_element_sibling(e) == nullptr && has_parent_element;
        case PseudoClassKind::LastChild:
            return next_element_sibling(e) == nullptr && has_parent_element;
        case PseudoClassKind::OnlyChild:
            return prev_element_sibling(e) == nullptr &&
                   next_element_sibling(e) == nullptr && has_parent_element;
        case PseudoClassKind::FirstOfType:
            return has_parent_element && child_index_of_type(e, false) == 1;
        case PseudoClassKind::LastOfType:
            return has_parent_element && child_index_of_type(e, true) == 1;
        case PseudoClassKind::OnlyOfType:
            return has_parent_element && child_index_of_type(e, false) == 1 &&
                   child_index_of_type(e, true) == 1;

        case PseudoClassKind::NthChild: {
            if (!has_parent_element) return false;
            if (!pc.nth_of_filter.empty()) {
                int i = filtered_child_index(e, pc.nth_of_filter, st, scope, false);
                return i > 0 && pc.nth.matches(i);
            }
            return pc.nth.matches(child_index(e));
        }
        case PseudoClassKind::NthLastChild: {
            if (!has_parent_element) return false;
            if (!pc.nth_of_filter.empty()) {
                int i = filtered_child_index(e, pc.nth_of_filter, st, scope, true);
                return i > 0 && pc.nth.matches(i);
            }
            return pc.nth.matches(child_index_from_end(e));
        }
        case PseudoClassKind::NthOfType:
            return has_parent_element && pc.nth.matches(child_index_of_type(e, false));
        case PseudoClassKind::NthLastOfType:
            return has_parent_element && pc.nth.matches(child_index_of_type(e, true));

        case PseudoClassKind::Empty:
            return e.children().empty();

        // §6.2: :not() matches when NONE of the listed selectors match.
        case PseudoClassKind::Not:
            for (const auto& s : pc.inner_list) {
                if (match_sequence(*s, e, st, scope)) return false;
            }
            return true;
        case PseudoClassKind::Is:
        case PseudoClassKind::Where:
            for (const auto& s : pc.inner_list) {
                if (match_sequence(*s, e, st, scope)) return true;
            }
            return false;
        case PseudoClassKind::Has:
            return match_has(pc.inner_list, e, st, scope);

        case PseudoClassKind::Lang: return matches_language(e, pc.argument);
        case PseudoClassKind::Dir:  return matches_direction(e, pc.argument);

        case PseudoClassKind::Link:    return is_hyperlink(e);   // no history: never :visited
        case PseudoClassKind::Visited: return false;
        case PseudoClassKind::AnyLink: return is_hyperlink(e);

        case PseudoClassKind::Target: return bit(ElementState::Target);
        case PseudoClassKind::Scope:  return scope ? (&e == scope) : is_root_element(e);
        case PseudoClassKind::Root:   return is_root_element(e);

        case PseudoClassKind::Hover:            return bit(ElementState::Hover);
        case PseudoClassKind::Focus:            return bit(ElementState::Focus);
        case PseudoClassKind::FocusVisible:     return bit(ElementState::FocusVisible);
        case PseudoClassKind::FocusWithin:      return bit(ElementState::FocusWithin);
        case PseudoClassKind::Active:           return bit(ElementState::Active);
        case PseudoClassKind::Disabled:         return bit(ElementState::Disabled);
        case PseudoClassKind::Enabled:          return !bit(ElementState::Disabled);
        case PseudoClassKind::Checked:          return bit(ElementState::Checked);
        case PseudoClassKind::PlaceholderShown: return bit(ElementState::PlaceholderShown);
        case PseudoClassKind::Autofill:         return bit(ElementState::Autofill);

        // Need the Forms layer; false is the honest answer until it lands.
        case PseudoClassKind::Required:
        case PseudoClassKind::Optional:
        case PseudoClassKind::ReadOnly:
        case PseudoClassKind::ReadWrite:
        case PseudoClassKind::Valid:
        case PseudoClassKind::Invalid:
        case PseudoClassKind::InRange:
        case PseudoClassKind::OutOfRange:
        case PseudoClassKind::UserValid:
        case PseudoClassKind::UserInvalid:
        case PseudoClassKind::Default:
        case PseudoClassKind::PopoverOpen:
        case PseudoClassKind::Modal:
            return false;
    }
    return false;
}

bool match_simple(const SimpleSelector& part, const Element& e,
                  const ElementStateProvider& st, const Element* scope) {
    switch (part.tag()) {
        case SimpleSelector::Tag::Universal: return true;
        case SimpleSelector::Tag::Type:
            return e.tag_name() == static_cast<const TypeSelector&>(part).tag_name;
        case SimpleSelector::Tag::Id:
            return e.id() == static_cast<const IdSelector&>(part).id;
        case SimpleSelector::Tag::Class:
            return contains_token(e.class_name(),
                                  static_cast<const ClassSelector&>(part).class_name, false);
        case SimpleSelector::Tag::Attribute:
            return match_attribute(static_cast<const AttributeSelector&>(part), e);
        case SimpleSelector::Tag::PseudoClass:
            return match_pseudo(static_cast<const PseudoClassSelector&>(part), e, st, scope);
    }
    return false;
}

bool match_compound(const CompoundSelector& c, const Element& e,
                    const ElementStateProvider& st, const Element* scope,
                    bool allow_pseudo_element) {
    // A compound carrying a pseudo-element never matches a real element; the
    // cascade routes those through a separate pseudo-element path. The
    // pseudo-element cascade sets allow_pseudo_element for the SUBJECT
    // compound only — ancestors still reject one.
    if (c.has_pseudo_element && !allow_pseudo_element) return false;
    for (const auto& p : c.parts) {
        if (!match_simple(*p, e, st, scope)) return false;
    }
    return true;
}

// Right-to-left walk: the rightmost compound is the subject, then each
// combinator steps outward through parents / previous siblings.
bool match_sequence(const CompoundSequence& seq, const Element& e,
                    const ElementStateProvider& st, const Element* scope,
                    bool allow_pseudo_on_subject) {
    if (seq.compounds.empty()) return false;
    std::size_t i = seq.compounds.size() - 1;
    if (!match_compound(seq.compounds[i], e, st, scope, allow_pseudo_on_subject)) return false;

    const Element* current = &e;
    while (i > 0) {
        const Combinator comb = seq.combinators[i - 1];
        const CompoundSelector& prev = seq.compounds[i - 1];
        switch (comb) {
            case Combinator::Descendant: {
                bool found = false;
                for (const Element* p = parent_element(*current); p; p = parent_element(*p)) {
                    if (match_compound(prev, *p, st, scope)) { current = p; found = true; break; }
                }
                if (!found) return false;
                break;
            }
            case Combinator::Child: {
                const Element* p = parent_element(*current);
                if (!p || !match_compound(prev, *p, st, scope)) return false;
                current = p;
                break;
            }
            case Combinator::AdjacentSibling: {
                const Element* s = prev_element_sibling(*current);
                if (!s || !match_compound(prev, *s, st, scope)) return false;
                current = s;
                break;
            }
            case Combinator::GeneralSibling: {
                bool found = false;
                for (const Element* s = prev_element_sibling(*current); s;
                     s = prev_element_sibling(*s)) {
                    if (match_compound(prev, *s, st, scope)) { current = s; found = true; break; }
                }
                if (!found) return false;
                break;
            }
            case Combinator::None:
                return false;
        }
        --i;
    }
    return true;
}

bool match_has_chain_forward(const CompoundSequence& seq, std::size_t index,
                             const Element& current, const ElementStateProvider& st,
                             const Element* scope);

bool walk_descendants_for_has(const Element& root, const CompoundSequence& seq,
                              std::size_t index, const ElementStateProvider& st,
                              const Element* scope) {
    for (const auto& c : root.children()) {
        const Element* ce = as_element(c.get());
        if (!ce) continue;
        if (match_has_chain_forward(seq, index, *ce, st, scope)) return true;
        if (walk_descendants_for_has(*ce, seq, index, st, scope)) return true;
    }
    return false;
}

// Forward (left-to-right) walk for the chain inside :has(). Per §17.4 the
// traversal is anchored at the subject and must move OUTWARD only — never up
// through parents, which would escape the subject's relative scope. That is
// why this cannot delegate to match_sequence, which walks right-to-left.
bool match_has_chain_forward(const CompoundSequence& seq, std::size_t index,
                             const Element& current, const ElementStateProvider& st,
                             const Element* scope) {
    if (!match_compound(seq.compounds[index], current, st, scope)) return false;
    if (index == seq.compounds.size() - 1) return true;

    switch (seq.combinators[index]) {
        case Combinator::Descendant:
            return walk_descendants_for_has(current, seq, index + 1, st, scope);
        case Combinator::Child:
            for (const auto& c : current.children()) {
                const Element* ce = as_element(c.get());
                if (ce && match_has_chain_forward(seq, index + 1, *ce, st, scope)) return true;
            }
            return false;
        case Combinator::AdjacentSibling: {
            const Element* s = next_element_sibling(current);
            return s && match_has_chain_forward(seq, index + 1, *s, st, scope);
        }
        case Combinator::GeneralSibling:
            for (const Element* s = next_element_sibling(current); s;
                 s = next_element_sibling(*s)) {
                if (match_has_chain_forward(seq, index + 1, *s, st, scope)) return true;
            }
            return false;
        case Combinator::None:
            return false;
    }
    return false;
}

bool match_has(const std::vector<std::unique_ptr<CompoundSequence>>& list,
               const Element& subject, const ElementStateProvider& st, const Element* scope) {
    for (const auto& seq : list) {
        // compounds = [anchor, c1, ...], combinators = [leading, c1->c2, ...].
        if (seq->compounds.size() < 2) continue;
        switch (seq->combinators[0]) {
            case Combinator::Descendant:
                if (walk_descendants_for_has(subject, *seq, 1, st, scope)) return true;
                break;
            case Combinator::Child:
                for (const auto& c : subject.children()) {
                    const Element* ce = as_element(c.get());
                    if (ce && match_has_chain_forward(*seq, 1, *ce, st, scope)) return true;
                }
                break;
            case Combinator::AdjacentSibling: {
                const Element* s = next_element_sibling(subject);
                if (s && match_has_chain_forward(*seq, 1, *s, st, scope)) return true;
                break;
            }
            case Combinator::GeneralSibling:
                for (const Element* s = next_element_sibling(subject); s;
                     s = next_element_sibling(*s)) {
                    if (match_has_chain_forward(*seq, 1, *s, st, scope)) return true;
                }
                break;
            case Combinator::None:
                break;
        }
    }
    return false;
}

} // namespace

bool selector_matches_sequence(const CompoundSequence& seq, const Element& e,
                               const ElementStateProvider& state, const Element* scope_root) {
    return match_sequence(seq, e, state, scope_root);
}

bool selector_matches_sequence_ignoring_pseudo(const CompoundSequence& seq, const Element& e,
                                               const ElementStateProvider& state,
                                               const Element* scope_root) {
    // Only the rightmost compound may carry the pseudo-element marker; the
    // ancestor walk is otherwise identical, so this is a flag rather than a
    // second traversal. (Slicing the sequence would mean copying
    // CompoundSelector, which owns unique_ptrs and is deliberately move-only.)
    return match_sequence(seq, e, state, scope_root, /*allow_pseudo_on_subject=*/true);
}

bool selector_matches(const CompiledSelector& sel, const Element& e,
                      const ElementStateProvider& state, const Element* scope_root) {
    // A selector ending in a pseudo-element never matches the element itself.
    if (sel.sequence.pseudo_element() != nullptr) return false;
    return match_sequence(sel.sequence, e, state, scope_root);
}

} // namespace weva
