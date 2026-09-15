#include "weva/cascade.h"

#include "weva/css_value.h"
#include "weva/css_token.h"
#include "weva/at_import.h"
#include "weva/env_attr.h"
#include "weva/keyword_resolver.h"
#include "weva/shorthand.h"
#include "weva/logical.h"
#include "weva/dom.h"
#include "weva/form_state.h"
#include "weva/variable_resolver.h"

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstdlib>

namespace weva {

namespace {
// `@font-face` descriptor values. A family is one quoted or unquoted name;
// `src` is an ordered list of url()/local() entries for the host to try.
std::string_view descriptor_trim(std::string_view value) {
    while (!value.empty() && (value.front() == ' ' || value.front() == '\t' || value.front() == '\n')) value.remove_prefix(1);
    while (!value.empty() && (value.back() == ' ' || value.back() == '\t' || value.back() == '\n')) value.remove_suffix(1);
    return value;
}

std::string_view unquote_family(std::string_view value) {
    value = descriptor_trim(value);
    if (value.size() >= 2 && (value.front() == '"' || value.front() == '\'') && value.back() == value.front())
        value = descriptor_trim(value.substr(1, value.size() - 2));
    return value;
}

bool iequals_ascii(std::string_view a, std::string_view b);

// Every src entry in order: "url:<path>" or "local:<name>", unquoted.
std::vector<std::string> font_sources(std::string_view value) {
    std::vector<CssToken> tokens;
    CssParseError error;
    if (!CssTokenizer(value, false).tokenize(&tokens, &error)) return {};
    std::vector<std::string> out;
    size_t i = 0;
    const auto kind = [&] { return i < tokens.size() ? tokens[i].kind : CssTokenKind::Eof; };
    const auto whitespace = [&] { while (kind() == CssTokenKind::Whitespace) ++i; };
    whitespace();
    while (kind() != CssTokenKind::Eof) {
        std::string argument;
        bool url = kind() == CssTokenKind::Url;
        if (url) {
            argument = tokens[i++].text;
        } else if (kind() == CssTokenKind::Function &&
                   (iequals_ascii(tokens[i].text, "url") || iequals_ascii(tokens[i].text, "local"))) {
            url = iequals_ascii(tokens[i++].text, "url");
            whitespace();
            if (kind() == CssTokenKind::String) {
                // Tokenization already handles escaped quotes, commas and
                // closing parentheses inside strings.
                argument = tokens[i++].text;
                whitespace();
            } else if (!url) {
                while (kind() == CssTokenKind::Ident) {
                    if (!argument.empty()) argument += ' ';
                    argument += tokens[i++].text;
                    whitespace();
                }
            } else return {};
            if (kind() != CssTokenKind::RParen) return {};
            ++i;
        } else return {};
        if (url) argument = std::string(descriptor_trim(argument));
        if (!argument.empty()) out.push_back((url ? "url:" : "local:") + argument);
        whitespace();
        // Keep the existing format/technology-hint behavior: the host tries
        // the source bytes. Consume whole functions so their punctuation
        // cannot become another source entry.
        while (kind() == CssTokenKind::Function &&
               (iequals_ascii(tokens[i].text, "format") || iequals_ascii(tokens[i].text, "tech"))) {
            ++i;
            int depth = 1;
            while (depth > 0 && kind() != CssTokenKind::Eof) {
                if (kind() == CssTokenKind::Function || kind() == CssTokenKind::LParen) ++depth;
                else if (kind() == CssTokenKind::RParen) --depth;
                else if (kind() == CssTokenKind::BadString || kind() == CssTokenKind::BadUrl) return {};
                ++i;
            }
            if (depth != 0) return {};
            whitespace();
        }
        if (kind() == CssTokenKind::Eof) break;
        if (kind() != CssTokenKind::Comma) return {};
        ++i;
        whitespace();
        if (kind() == CssTokenKind::Eof) return {};
    }
    return out;
}
} // namespace

namespace {

struct CascadePhaseProfile {
    using Clock = std::chrono::steady_clock;
    CascadeEngine::WorkProfile* profile;
    Clock::time_point last;
    size_t final_bucket;
    CascadePhaseProfile(CascadeEngine::WorkProfile& p, size_t final)
        : profile(nullptr), final_bucket(final) {
        // The counts are kept always: they are the engine counters a host's
        // stats window reads. Only the timings wait for WEVA_CASCADE_LOG.
        if (final == 7) ++p.pseudos; else ++p.elements;
        static const bool enabled = std::getenv("WEVA_CASCADE_LOG") != nullptr;
        if (!enabled) return;
        profile = &p;
        last = Clock::now();
    }
    void lap(size_t bucket) {
        if (!profile) return;
        const auto now = Clock::now();
        profile->ms[bucket] += std::chrono::duration<double, std::milli>(now - last).count();
        last = now;
    }
    ~CascadePhaseProfile() { lap(final_bucket); }
};

// UA < User < Author for normal declarations.
int compare_normal_origin(DeclarationOrigin a, DeclarationOrigin b) {
    int ia = static_cast<int>(a), ib = static_cast<int>(b);
    return ia < ib ? -1 : (ia > ib ? 1 : 0);
}
// REVERSED for !important: Author < User < UA.
int compare_important_origin(DeclarationOrigin a, DeclarationOrigin b) {
    int ia = static_cast<int>(a), ib = static_cast<int>(b);
    return ib < ia ? -1 : (ib > ia ? 1 : 0);
}
int cmp_int(int a, int b) { return a < b ? -1 : (a > b ? 1 : 0); }


// ASCII-only whitespace trim and case-insensitive compare, used by the
// @property pass. Kept local rather than shared: the value semantics here are
// CSS keyword matching, not general text handling.
std::string_view trim_ws(std::string_view s) {
    size_t b = 0, e = s.size();
    const auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    while (b < e && ws(s[b])) ++b;
    while (e > b && ws(s[e - 1])) --e;
    return s.substr(b, e - b);
}

bool iequals_ascii(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}


// Replaces every shorthand declaration with its longhands, in place, keeping
// the surrounding order. The shorthand itself is DROPPED — that is what makes
// `{ padding: 5px; padding-left: 20px }` resolve by ordinary source order
// rather than by a shorthand-versus-longhand precedence rule.
//
// A shorthand whose value still contains var() or attr() is left alone: the
// expander cannot tokenise an unresolved reference, and the reference engine
// never revisits it either, so it stays a shorthand for good.
//
// Done once per rule at compile time rather than once per element per pass.
// A shorthand that reached the computed style unexpanded — because its value
// carried var() at compile time — is expanded now that the reference is
// resolved. `border: 2px solid var(--cyan)` otherwise produced no border
// widths at all, and every card and chip using a themed border was 4px short.
// Order caveat: a longhand declared AFTER the shorthand in the same cascade
// has already been applied and is overwritten here; that is the one case the
// compile-time expansion gets right and this does not.
void expand_substituted_shorthands(const std::vector<std::pair<int, std::string>>& rewrites,
                                   ComputedStyle* out) {
    const auto& reg = CssPropertyRegistry::instance();
    std::vector<ShorthandLonghand> longhands;
    for (const auto& r : rewrites) {
        const std::string_view name = reg.name_of(r.first);
        if (name.empty() || !is_shorthand(name)) continue;
        longhands.clear();
        if (!expand_shorthand(name, r.second, &longhands)) continue;
        for (const ShorthandLonghand& lh : longhands) out->set(lh.property, lh.value);
    }
}

std::vector<Declaration> expand_declarations(const std::vector<Declaration>& source,
                                            std::string_view source_url = {}) {
    bool any = false;
    for (const Declaration& d : source) {
        if (is_shorthand(d.property) && !contains_substitution(d.value_text)) {
            any = true;
            break;
        }
    }
    if (!any && source_url.empty()) return source;

    std::vector<Declaration> out;
    out.reserve(source.size() + 8);
    std::vector<ShorthandLonghand> longhands;
    for (const Declaration& original : source) {
        Declaration rebased;
        const Declaration* input = &original;
        if (!source_url.empty() && original.property.compare(0, 2, "--") != 0 &&
            !contains_substitution(original.value_text)) {
            rebased = original;
            resolve_stylesheet_value_urls(&rebased.value_text, source_url);
            input = &rebased;
        }
        const Declaration& d = *input;
        if (contains_substitution(d.value_text)) {
            out.push_back(d);
            continue;
        }
        longhands.clear();
        if (!expand_shorthand(d.property, d.value_text, &longhands)) {
            out.push_back(d);
            continue;
        }
        // A malformed shorthand emits nothing AND is still dropped: the
        // declaration is invalid, so the affected longhands keep whatever an
        // earlier declaration gave them.
        for (const ShorthandLonghand& lh : longhands) {
            Declaration sub;
            sub.property = std::string(lh.property);
            sub.value_text = lh.value;
            sub.important = d.important;
            out.push_back(std::move(sub));
        }
    }
    return out;
}

} // namespace

CascadeKey CascadeKey::of(const MatchedDeclaration& m, uint64_t generation) {
    CascadeKey k;
    k.source_url = m.source_url;
    k.specificity = m.specificity;
    k.source_index = m.source_index;
    k.scope_distance = m.scope_distance;
    k.in_rule_index = m.in_rule_index;
    k.layer_ordinal = m.layer_ordinal;
    k.origin = m.origin;
    k.is_inline = m.is_inline;
    k.important = m.declaration && m.declaration->important;
    k.generation = generation;
    return k;
}

int compare_for_cascade(const MatchedDeclaration& x, const MatchedDeclaration& y) {
    return compare_for_cascade(CascadeKey::of(x, 0), CascadeKey::of(y, 0));
}

int compare_for_cascade(const CascadeKey& x, const CascadeKey& y) {
    // Earlier in the sorted list = lower precedence; the LAST entry wins.

    // Importance is the dominant axis.
    if (x.important != y.important) {
        return x.important ? 1 : -1;
    }

    // Within an importance class the origin ordering flips.
    if (x.important) {
        if (int o = compare_important_origin(x.origin, y.origin); o != 0) return o;
    } else {
        if (int o = compare_normal_origin(x.origin, y.origin); o != 0) return o;
    }

    // §6.4.1 steps 4-5, the layer axis — and it is asymmetric in two ways that
    // are easy to lose:
    //   normal:    a LATER layer wins, and unlayered (the max ordinal) beats
    //              every layered rule. Inline bypasses this axis entirely and
    //              is settled by the inline tiebreak below.
    //   important: REVERSED — an EARLIER layer wins, and unlayered (including
    //              inline !important) LOSES to any layered !important. So the
    //              layer comparison must run even when one side is inline.
    if (x.important) {
        if (x.layer_ordinal != y.layer_ordinal) {
            return cmp_int(y.layer_ordinal, x.layer_ordinal);
        }
    } else if (!x.is_inline && !y.is_inline) {
        if (x.layer_ordinal != y.layer_ordinal) {
            return cmp_int(x.layer_ordinal, y.layer_ordinal);
        }
    }

    if (x.is_inline != y.is_inline) return x.is_inline ? 1 : -1;
    if (int s = x.specificity.compare(y.specificity); s != 0) return s;
    if (int d = cmp_int(y.scope_distance, x.scope_distance); d != 0) return d;
    if (int i = cmp_int(x.source_index, y.source_index); i != 0) return i;
    return cmp_int(x.in_rule_index, y.in_rule_index);
}

void CascadeEngine::clear() {
    ++container_generation_;
    rules_.clear();
    next_source_index_ = 0;
    container_queries_.clear();
    compiling_containers_.clear();
    compiling_scopes_.clear();
    compiling_scope_root_ = nullptr;
    property_registry_.clear();
    pseudo_rules_.clear();
    layer_names_.clear();
    unsupported_at_rules_.clear();
    font_faces_.clear();
    keyframes_.clear();
    keyframe_priorities_.clear();
    layer_prefix_.clear();
    shape_cache_.clear();
    cache_unsafe_sibling_composition_ = false;
    cache_unsafe_has_ = false;
    cache_unsafe_scope_ = false;
    has_subject_reach_ = StateReach{};
    hover_reach_ = StateReach{};
    active_reach_ = StateReach{};
    shape_key_folds_sibling_index_ = false;
    shape_key_folds_range_ = false;
    shape_key_folds_validity_ = false;
    shape_key_folds_default_ = false;
}

namespace {

constexpr uint64_t kFnvOffset = 14695981039346656037ULL;
constexpr uint64_t kFnvPrime = 1099511628211ULL;

uint64_t hash_str(std::string_view s) {
    uint64_t h = kFnvOffset;
    for (char c : s) {
        h ^= static_cast<unsigned char>(c);
        h *= kFnvPrime;
    }
    return h;
}

// Commutative XOR so the order tokens appear in the class attribute cannot
// shift the hash — `class="a b"` and `class="b a"` must share a key.
uint64_t hash_class_tokens(std::string_view classes) {
    uint64_t acc = 0;
    std::size_t i = 0;
    auto ws = [](char c) {
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    };
    while (i < classes.size()) {
        while (i < classes.size() && ws(classes[i])) ++i;
        std::size_t start = i;
        while (i < classes.size() && !ws(classes[i])) ++i;
        if (i > start) acc ^= hash_str(classes.substr(start, i - start));
    }
    return acc;
}

// Walks a compound sequence looking for constructs that make a per-element
// shape key unsound or incomplete.
void classify_selector(const CompoundSequence& seq, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index, StateReach* hover,
                       StateReach* active, bool inside_has);

// Which compounds a `:hover` or `:active` sits on, so a pointer move only
// restyles what a rule could actually match.
//
// The boolean this replaces -- "does any rule mention :hover" -- was useless in
// practice, because the user-agent sheet has one such rule
// (`.ui-menu-item:hover`) and that single line turned the flag on for every
// document ever loaded. What matters is not whether SOME rule says :hover but
// whether one says it about THIS element.
//
// So the keys of the compound carrying the pseudo are recorded, and an element
// that shares none of them cannot change appearance when the pointer arrives.
// A compound with no key at all (a bare `:hover`, or `*:hover`) reaches
// everything and is recorded as such.
//
// `:hover` nested inside `:has()` is given the same treatment, but as belt and
// braces rather than because a test can catch it: the element whose style moves
// need not be a descendant of the hovered one, so these keys describe the wrong
// element -- and yet the keyed element is by definition in the hover chain, so
// the marking still lands, and a sheet containing `:has()` already takes the
// unscoped walk (see `scoped` in weva_c.cpp). Deleting the guard breaks no test
// today. It is here so that narrowing the `:has()` walk later cannot silently
// take this with it.
void collect_keys(const CompoundSelector& c, StateReach* reach) {
    bool keyed = false;
    for (const auto& part : c.parts) {
        switch (part->tag()) {
            case SimpleSelector::Tag::Class:
                reach->classes.insert(static_cast<const ClassSelector&>(*part).class_name);
                keyed = true;
                break;
            case SimpleSelector::Tag::Id:
                reach->ids.insert(static_cast<const IdSelector&>(*part).id);
                keyed = true;
                break;
            case SimpleSelector::Tag::Type:
                reach->tags.insert(static_cast<const TypeSelector&>(*part).tag_name);
                keyed = true;
                break;
            default:
                break;
        }
    }
    // `:hover` on its own, or hung off `*`, or off an attribute selector we do
    // not index: nothing narrows it, so it reaches everything.
    if (!keyed) reach->everything = true;
}

void classify_compound(const CompoundSelector& c, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index, StateReach* hover,
                       StateReach* active, bool inside_has) {
    for (const auto& part : c.parts) {
        if (part->tag() != SimpleSelector::Tag::PseudoClass) continue;
        const auto& pc = static_cast<const PseudoClassSelector&>(*part);
        switch (pc.kind) {
            // Index-positional: the key must additionally fold sibling index
            // and count, or `li:nth-child(odd)` serves the first row's match
            // set to every identical sibling and zebra striping paints every
            // row.
            case PseudoClassKind::NthChild:
            case PseudoClassKind::NthLastChild:
            case PseudoClassKind::FirstChild:
            case PseudoClassKind::LastChild:
            case PseudoClassKind::OnlyChild:
            case PseudoClassKind::Empty:
                *folds_index = true;
                break;
            // Of-type pseudos depend on which TAGS precede the element, which
            // no per-element key can represent.
            case PseudoClassKind::FirstOfType:
            case PseudoClassKind::LastOfType:
            case PseudoClassKind::OnlyOfType:
            case PseudoClassKind::NthOfType:
            case PseudoClassKind::NthLastOfType:
                *unsafe_sibling = true;
                break;
            // :has() depends on DESCENDANT content the key cannot represent,
            // and a descendant mutation cannot invalidate an ancestor entry —
            // the key is a hash with no reverse index.
            case PseudoClassKind::Has:
                *has_has = true;
                break;
            // Not a cache-safety fact like the others: this is what a POINTER
            // MOVE can change. The keys come from the compound the pseudo sits
            // on, at whatever nesting depth it was found -- `.btn:not(:hover)`
            // is keyed on `.btn`, the same as `.btn:hover`.
            case PseudoClassKind::Hover:
                if (inside_has) hover->everything = true;
                else collect_keys(c, hover);
                break;
            case PseudoClassKind::Active:
                if (inside_has) active->everything = true;
                else collect_keys(c, active);
                break;
            default:
                break;
        }
        const bool nested_has = inside_has || pc.kind == PseudoClassKind::Has;
        for (const auto& inner : pc.inner_list) {
            classify_selector(*inner, unsafe_sibling, has_has, folds_index, hover, active,
                              nested_has);
        }
        for (const auto& inner : pc.nth_of_filter) {
            // A filtered sibling rank depends on other siblings' matches,
            // not just this element's index/count. Their attributes or text
            // (:empty) can change while this element's shape stays equal.
            *unsafe_sibling = true;
            classify_selector(*inner, unsafe_sibling, has_has, folds_index, hover, active,
                              nested_has);
        }
    }
}

void classify_selector(const CompoundSequence& seq, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index, StateReach* hover,
                       StateReach* active, bool inside_has) {
    for (Combinator cb : seq.combinators) {
        // `p + p` / `p ~ p`: the match depends on preceding-sibling
        // composition, not just this element's own shape.
        if (cb == Combinator::AdjacentSibling || cb == Combinator::GeneralSibling) {
            *unsafe_sibling = true;
        }
    }
    for (size_t i = 0; i < seq.compounds.size(); ++i) {
        bool positional = false;
        classify_compound(seq.compounds[i], unsafe_sibling, has_has, &positional, hover, active, inside_has);
        *folds_index = *folds_index || positional;
        // Only the subject's rank is in a shape key. A positional parent,
        // including one inside the :is() used for nesting, needs its own rank.
        if (positional && i + 1 < seq.compounds.size()) *unsafe_sibling = true;
    }
}

const Element* parent_el(const Element& e) {
    const Node* p = e.parent();
    return (p && p->is_element()) ? static_cast<const Element*>(p) : nullptr;
}

bool uses_default(const CompoundSequence& seq) {
    for (const auto& compound : seq.compounds) for (const auto& part : compound.parts) {
        if (part->tag() != SimpleSelector::Tag::PseudoClass) continue;
        const auto& pc = static_cast<const PseudoClassSelector&>(*part);
        if (pc.kind == PseudoClassKind::Default) return true;
        for (const auto& inner : pc.inner_list) if (uses_default(*inner)) return true;
        for (const auto& inner : pc.nth_of_filter) if (uses_default(*inner)) return true;
    }
    return false;
}

bool uses_range(const CompoundSequence& seq) {
    for (const auto& compound : seq.compounds) for (const auto& part : compound.parts) {
        if (part->tag() != SimpleSelector::Tag::PseudoClass) continue;
        const auto& pc = static_cast<const PseudoClassSelector&>(*part);
        if (pc.kind == PseudoClassKind::InRange || pc.kind == PseudoClassKind::OutOfRange) return true;
        for (const auto& inner : pc.inner_list) if (uses_range(*inner)) return true;
        for (const auto& inner : pc.nth_of_filter) if (uses_range(*inner)) return true;
    }
    return false;
}

bool uses_validity(const CompoundSequence& seq) {
    for (const auto& compound : seq.compounds) for (const auto& part : compound.parts) {
        if (part->tag() != SimpleSelector::Tag::PseudoClass) continue;
        const auto& pc = static_cast<const PseudoClassSelector&>(*part);
        if (pc.kind == PseudoClassKind::Valid || pc.kind == PseudoClassKind::Invalid) return true;
        for (const auto& inner : pc.inner_list) if (uses_validity(*inner)) return true;
        for (const auto& inner : pc.nth_of_filter) if (uses_validity(*inner)) return true;
    }
    return false;
}

} // namespace

uint64_t CascadeEngine::try_compute_shape_key(const Element& e,
                                              const ElementStateProvider& state) const {
    // 0 means "do not cache". Every opt-out below exists because sharing would
    // otherwise serve one element's match set to a genuinely different element.

    // Inline style is per-element and invisible to a tag/class/attribute key.
    if (e.has_attribute("style")) return 0;
    // Sibling composition and :has() are unrepresentable in a per-element key.
    if (cache_unsafe_sibling_composition_) return 0;
    if (cache_unsafe_scope_) return 0;
    // A :has-dependent rule can affect only its rightmost subject. Elements
    // excluded by every such subject's mandatory keys still use the ordinary
    // input-keyed cache. Unkeyed/nested subjects conservatively reach everyone.
    if (cache_unsafe_has_ && state_observable(has_subject_reach_, e)) return 0;

    uint64_t h = kFnvOffset;
    if (shape_key_folds_validity_) { h ^= form_validity_selector_state(e); h *= kFnvPrime; }
    if (shape_key_folds_range_) { h ^= form_range_selector_state(e); h *= kFnvPrime; }
    if (shape_key_folds_default_) { h ^= static_cast<uint64_t>(form_is_default(e)); h *= kFnvPrime; }
    if (!container_queries_.empty()) {
        h ^= container_provider_ ? container_provider_->version(e) : 0;
        h *= kFnvPrime;
    }
    h ^= hash_str(e.tag_name()); h *= kFnvPrime;
    h ^= hash_str(e.id());       h *= kFnvPrime;
    h ^= hash_class_tokens(e.class_name()); h *= kFnvPrime;

    // Attributes fold NAME AND VALUE: [data-state="open"] and
    // [data-state="closed"] must not share a key. class/id are folded above.
    uint64_t attr_hash = 0;
    const AttributeMap& attrs = e.attributes();
    for (std::size_t i = 0; i < attrs.size(); ++i) {
        std::string_view n = attrs.name_at(i);
        if (n == "class" || n == "id") continue;
        attr_hash ^= hash_str(n) * 2654435761ULL;
        attr_hash ^= hash_str(attrs.value_at(i)) * kFnvOffset;
    }
    h ^= attr_hash; h *= kFnvPrime;

    // Index-positional pseudos need sibling position folded in.
    if (shape_key_folds_sibling_index_) {
        const Node* p = e.parent();
        int index = 0, count = 0;
        if (p) {
            for (const auto& c : p->children()) {
                if (!c->is_element()) continue;
                ++count;
                if (c.get() == &e) index = count;
            }
        }
        h ^= static_cast<uint64_t>(index) * 2654435761ULL; h *= kFnvPrime;
        h ^= static_cast<uint64_t>(count) * 40503ULL;      h *= kFnvPrime;
        h ^= static_cast<uint64_t>(e.children().size()) * 2246822519ULL;
        h *= kFnvPrime;
    }

    // The FULL ancestor chain, plus ancestor state bits.
    //
    // The parent's own match set is not enough: a rule like `.parent #c`
    // matches only the descendant, so flipping the ancestor between `.parent`
    // and `.other` leaves the ancestor's own matches unchanged while changing
    // the child's. Likewise `div:hover span` puts the state on the LEFT of a
    // combinator, so the span's own state is irrelevant but the parent's is not.
    for (const Element* a = parent_el(e); a; a = parent_el(*a)) {
        if (shape_key_folds_validity_) { h ^= form_validity_selector_state(*a); h *= kFnvPrime; }
        if (shape_key_folds_range_) { h ^= form_range_selector_state(*a); h *= kFnvPrime; }
        if (shape_key_folds_default_) { h ^= static_cast<uint64_t>(form_is_default(*a)); h *= kFnvPrime; }
        uint64_t anc = 0;
        anc ^= hash_str(a->tag_name());
        anc ^= hash_str(a->id()) * 257ULL;
        anc ^= hash_class_tokens(a->class_name());
        anc ^= static_cast<uint64_t>(state.state_of(*a)) * 2654435761ULL;
        // An ancestor's OTHER attributes, for the same reason the element's own
        // are folded: `select[size] option` matches on the parent's attribute,
        // so a <select> and a <select size=3> that fold alike hand their
        // options the same match set. That is what made a list box's options
        // vanish -- they took `display: none` from a plain select's option,
        // computed earlier in the same document, and generated no boxes at all.
        const AttributeMap& ancestor_attrs = a->attributes();
        for (std::size_t i = 0; i < ancestor_attrs.size(); ++i) {
            const std::string_view n = ancestor_attrs.name_at(i);
            if (n == "class" || n == "id") continue;   // folded above
            anc ^= hash_str(n) * 2654435761ULL;
            anc ^= hash_str(ancestor_attrs.value_at(i)) * kFnvOffset;
        }
        h ^= anc;
        h *= kFnvPrime;
    }

    // The element's own state bits.
    h ^= static_cast<uint64_t>(state.state_of(e)) * 40503ULL;
    h *= kFnvPrime;

    return h == 0 ? 1 : h;   // never collide with the "do not cache" sentinel
}

namespace {

std::string_view trim_ascii(std::string_view s) {
    size_t b = 0, e = s.size();
    while (b < e && (s[b] == ' ' || s[b] == '\t' || s[b] == '\n' || s[b] == '\r')) ++b;
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\n' ||
                     s[e - 1] == '\r')) {
        --e;
    }
    return s.substr(b, e - b);
}

// Nested style selectors use their parent list as an :is() anchor. Scoped
// selectors without a style parent use a zero-specificity scope-root anchor.
// Tokenize so quoted attributes and escaped identifiers cannot look like & or
// :scope, and keep authored text for cascade inspection.
bool parse_scoped_selector(std::string_view text, bool relative, CompiledSelector* out,
                           const std::vector<std::string>* nesting = nullptr,
                           std::string* resolved = nullptr) {
    if (trim_ascii(text).empty()) return false;
    SelectorParseError error;
    if (!relative && !nesting && text.find('&') == std::string_view::npos) {
        if (resolved) *resolved = text;
        return parse_selector(text, out, &error);
    }
    // Repeated & references can multiply an already expanded selector. Bound
    // generated text, like the parser's rule-depth bound, before allocating it.
    constexpr size_t kMaxExpandedSelector = 65536;
    std::string anchor = ":where(:scope)";
    if (nesting) {
        anchor = ":is(";
        for (const auto& parent : *nesting) {
            CompiledSelector selector;
            SelectorParseError parse_error;
            // & cannot represent pseudo-elements, including their specificity.
            // Declaration runs use the original list and retain those pseudos.
            if (!parse_selector(parent, &selector, &parse_error) || selector.sequence.pseudo_element()) continue;
            if (anchor.size() + parent.size() + 2 > kMaxExpandedSelector) return false;
            if (anchor.size() > 4) anchor += ',';
            anchor += parent;
        }
        if (anchor.size() == 4) anchor = ":not(*)";
        else anchor += ')';
    }
    std::vector<CssToken> tokens;
    CssParseError token_error;
    if (!CssTokenizer(text).tokenize(&tokens, &token_error)) return false;
    std::string rewritten;
    int brackets = 0;
    bool anchored = false;
    for (size_t i = 0; i < tokens.size(); ++i) {
        const auto& token = tokens[i];
        if (token.kind == CssTokenKind::LBracket) ++brackets;
        if (token.kind == CssTokenKind::RBracket) --brackets;
        if (brackets == 0 && token.kind == CssTokenKind::Delim && token.text == "&") {
            if (rewritten.size() + anchor.size() > kMaxExpandedSelector) return false;
            rewritten += anchor;
            anchored = true;
        } else {
            if (!nesting && brackets == 0 && token.kind == CssTokenKind::Colon && i + 1 < tokens.size() &&
                tokens[i + 1].kind == CssTokenKind::Ident) {
                std::string name = tokens[i + 1].text;
                for (char& c : name) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
                if (name == "scope") anchored = true;
            }
            rewritten += css_token_source(token);
        }
        if (rewritten.size() > kMaxExpandedSelector) return false;
    }
    const char first = trim_ascii(text).front();
    const bool leading_combinator = first == '>' || first == '+' || first == '~';
    if ((relative || nesting) && (!anchored || leading_combinator)) {
        if (rewritten.size() + anchor.size() + 1 > kMaxExpandedSelector) return false;
        rewritten.insert(0, anchor + " ");
    }
    if (!parse_selector(rewritten, out, &error)) return false;
    if (resolved) *resolved = rewritten;
    out->source_text = std::string(trim_ascii(text));
    return true;
}

// Unlike :is(), scope-start and scope-end are unforgiving lists. A malformed
// group must discard the whole @scope, never turn it into an unbounded scope.
bool parse_scope_group(const std::vector<CssToken>& tokens, size_t* at, bool relative,
                       std::vector<CompiledSelector>* selectors,
                       const std::vector<std::string>* nesting = nullptr) {
    if (tokens[*at].kind != CssTokenKind::LParen) return false;
    ++*at;
    int depth = 0;
    std::string selector;
    for (; *at < tokens.size(); ++*at) {
        const auto& token = tokens[*at];
        if (token.kind == CssTokenKind::Eof) return false;
        const bool end = depth == 0 && token.kind == CssTokenKind::RParen;
        if (end || (depth == 0 && token.kind == CssTokenKind::Comma)) {
            CompiledSelector compiled;
            if (!parse_scoped_selector(selector, relative, &compiled, nesting) ||
                compiled.sequence.pseudo_element()) return false;
            selectors->push_back(std::move(compiled));
            selector.clear();
            if (end) { ++*at; return true; }
            continue;
        }
        if (token.kind == CssTokenKind::LParen || token.kind == CssTokenKind::Function ||
            token.kind == CssTokenKind::LBracket) ++depth;
        if (token.kind == CssTokenKind::RParen || token.kind == CssTokenKind::RBracket) --depth;
        if (depth < 0) return false;
        selector += css_token_source(token);
    }
    return false;
}

bool parse_scope_prelude(std::string_view text, bool nested,
                         std::vector<CompiledSelector>* roots,
                         std::vector<CompiledSelector>* limits,
                         const std::vector<std::string>* nesting) {
    std::vector<CssToken> tokens;
    CssParseError error;
    if (!CssTokenizer(text).tokenize(&tokens, &error)) return false;
    size_t at = 0;
    const auto skip = [&] {
        while (at < tokens.size() && tokens[at].kind == CssTokenKind::Whitespace) ++at;
    };
    skip();
    if (tokens[at].kind == CssTokenKind::LParen) {
        if (!parse_scope_group(tokens, &at, nested, roots, nesting)) return false;
        skip();
    }
    if (tokens[at].kind == CssTokenKind::Eof) return true;
    std::string keyword = tokens[at].text;
    for (char& c : keyword) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    if (tokens[at].kind != CssTokenKind::Ident || keyword != "to") return false;
    ++at;
    skip();
    if (!parse_scope_group(tokens, &at, true, limits)) return false;
    skip();
    return tokens[at].kind == CssTokenKind::Eof;
}

} // namespace

// The ordinal for a layer name, registering it at the end if this is the first
// time it has been named. First mention fixes the order; reopening a layer
// later does not move it.
int CascadeEngine::layer_ordinal_for(std::string_view name) {
    for (size_t i = 0; i < layer_names_.size(); ++i) {
        if (layer_names_[i] == name) return static_cast<int>(i);
    }
    layer_names_.emplace_back(name);
    return static_cast<int>(layer_names_.size() - 1);
}

int CascadeEngine::compare_keyframe_layers(int left, int right) const {
    if (left == right) return 0;
    if (left == kUnlayeredOrdinal) return 1;
    if (right == kUnlayeredOrdinal) return -1;
    const std::string_view a = layer_names_[static_cast<size_t>(left)];
    const std::string_view b = layer_names_[static_cast<size_t>(right)];
    // A dotted first mention also establishes its ancestors. Compare the
    // first differing sibling, not the global registration time of a leaf.
    const auto first_mention = [&](std::string_view prefix) {
        for (size_t i = 0; i < layer_names_.size(); ++i) {
            const std::string_view name = layer_names_[i];
            if (name == prefix || (name.size() > prefix.size() &&
                name.substr(0, prefix.size()) == prefix && name[prefix.size()] == '.')) return i;
        }
        return layer_names_.size();
    };
    size_t offset = 0;
    for (;;) {
        const size_t ae = std::min(a.find('.', offset), a.size());
        const size_t be = std::min(b.find('.', offset), b.size());
        const auto ap = a.substr(0, ae), bp = b.substr(0, be);
        if (ap != bp) return first_mention(ap) < first_mention(bp) ? -1 : 1;
        // Rules directly in a layer outrank its nested layers.
        if (ae == a.size()) return 1;
        if (be == b.size()) return -1;
        offset = ae + 1;
    }
}

bool CascadeEngine::state_observable(const StateReach& reach, const Element& e) const {
    if (reach.everything) return true;
    if (reach.empty()) return false;
    if (!reach.tags.empty() && reach.tags.count(e.tag_name())) return true;
    if (!reach.ids.empty()) {
        const std::string_view id = e.id();
        if (!id.empty() && reach.ids.count(std::string(id))) return true;
    }
    if (!reach.classes.empty()) {
        // Walked rather than set-intersected: an element carries a handful of
        // classes and this runs once per element of a hover chain, which is a
        // depth, not a tree.
        for (const std::string_view c : e.class_list()) {
            if (reach.classes.count(std::string(c))) return true;
        }
    }
    return false;
}

void CascadeEngine::compile_declarations(const Rule& rule,
                                          const std::vector<Declaration>& declarations,
                                          std::vector<CompiledSelector> selectors,
                                          DeclarationOrigin origin, int* source_index,
                                          int layer_ordinal) {
    for (auto& cs : selectors) {
        // Classify BEFORE moving: the shape cache's soundness depends
        // on spotting sibling-composition and :has() selectors anywhere
        // in the sheet.
        bool selector_has = false;
        classify_selector(cs.sequence, &cache_unsafe_sibling_composition_,
                          &selector_has, &shape_key_folds_sibling_index_,
                          &hover_reach_, &active_reach_, false);
        cache_unsafe_has_ = cache_unsafe_has_ || selector_has;
        if (selector_has && !cs.sequence.compounds.empty())
            collect_keys(cs.sequence.compounds.back(), &has_subject_reach_);
        shape_key_folds_range_ = shape_key_folds_range_ || uses_range(cs.sequence);
        shape_key_folds_validity_ = shape_key_folds_validity_ || uses_validity(cs.sequence);
        shape_key_folds_default_ = shape_key_folds_default_ || uses_default(cs.sequence);
        const std::string* pseudo = cs.sequence.pseudo_element();
        std::string pseudo_name = pseudo ? *pseudo : std::string();
        CompiledRule cr;
        cr.selector = std::move(cs);
        cr.rule = &rule;
        cr.origin = origin;
        cr.source_index = (*source_index)++;
        cr.layer_ordinal = layer_ordinal;
        cr.container_conditions = compiling_containers_;
        cr.scopes = compiling_scopes_;
        cr.declarations = expand_declarations(declarations, rule.source_url);
        if (!pseudo_name.empty()) {
            pseudo_rules_[pseudo_name].push_back(std::move(cr));
        } else {
            rules_.push_back(std::move(cr));
        }
    }
}

void CascadeEngine::compile_group(const GenericAtRule& rule, DeclarationOrigin origin,
                                  int* source_index, int layer_ordinal,
                                  const std::vector<std::string>* nesting) {
    // Group declarations match the nearest style parent exactly. Within
    // @scope, declarations without a style parent match :where(:scope).
    const std::vector<std::string> scope_selector{":where(:scope)"};
    const auto* parent = nesting ? nesting : (rule.name == "scope" ? &scope_selector : nullptr);
    if (parent && !rule.declarations.empty()) {
        std::vector<CompiledSelector> selectors;
        for (const auto& text : *parent) {
            CompiledSelector selector;
            SelectorParseError error;
            if (parse_selector(text, &selector, &error)) selectors.push_back(std::move(selector));
        }
        compile_declarations(rule, rule.declarations, std::move(selectors), origin, source_index, layer_ordinal);
    }
    compile_rules(rule.nested_rules, origin, source_index, layer_ordinal, nesting, rule.name == "scope");
}

void CascadeEngine::compile_rules(const std::vector<RulePtr>& rules, DeclarationOrigin origin,
                                  int* source_index, int layer_ordinal,
                                  const std::vector<std::string>* nesting, bool scope_declarations) {
    for (const auto& r : rules) {
        if (r->kind() == RuleKind::Style) {
            const auto* sr = static_cast<const StyleRule*>(r.get());
            std::vector<std::string> resolved;
            std::vector<CompiledSelector> selectors;
            bool valid = true;
            if (sr->nested_declarations) {
                if (nesting) resolved = *nesting;
                else if (scope_declarations) resolved.push_back(":where(:scope)");
                for (const auto& text : resolved) {
                    CompiledSelector selector;
                    SelectorParseError error;
                    if (!parse_selector(text, &selector, &error)) { valid = false; break; }
                    selectors.push_back(std::move(selector));
                }
            } else {
                for (const auto& text : sr->selectors) {
                    CompiledSelector selector;
                    std::string expanded;
                    if (!parse_scoped_selector(text, !compiling_scopes_.empty(), &selector, nesting, &expanded)) {
                        valid = false;
                        break;
                    }
                    resolved.push_back(std::move(expanded));
                    selectors.push_back(std::move(selector));
                }
            }
            // An invalid selector list discards its children too. Compiling
            // them independently would leak styles outside the parent rule.
            if (!valid || selectors.empty()) continue;
            compile_declarations(*sr, sr->declarations, std::move(selectors), origin, source_index, layer_ordinal);
            compile_rules(sr->nested_rules, origin, source_index, layer_ordinal, &resolved);
        } else {
            const auto* ar = static_cast<const GenericAtRule*>(r.get());
            // Only grouping rules may be nested inside a style rule.
            if (nesting && ar->name != "media" && ar->name != "supports" && ar->name != "layer" &&
                ar->name != "container" && ar->name != "scope") continue;
            // Conditional at-rules gate their body. A false condition
            // contributes no rules at all, rather than contributing rules that
            // silently apply.
            if (ar->name == "media") {
                if (!evaluate_media_query(ar->prelude, media_)) continue;
            } else if (ar->name == "scope") {
                // `(<root list>)? [to (<limit list>)]?`: both lists optional.
                auto spec = std::make_shared<ScopeSpec>();
                if (!ar->has_block || !parse_scope_prelude(ar->prelude, !compiling_scopes_.empty(),
                                                          &spec->roots, &spec->limits, nesting)) continue;
                spec->implicit_root = compiling_scope_root_;
                if (spec->roots.empty() && spec->implicit_root) cache_unsafe_scope_ = true;
                const auto classify_boundary = [&](const CompiledSelector& selector) {
                    bool has = false, positional = false;
                    classify_selector(selector.sequence, &cache_unsafe_sibling_composition_,
                                      &has, &positional, &hover_reach_, &active_reach_, false);
                    // The subject key folds its own rank, not every ancestor's.
                    cache_unsafe_scope_ = cache_unsafe_scope_ || positional;
                    cache_unsafe_has_ = cache_unsafe_has_ || has;
                    if (has) has_subject_reach_.everything = true;
                    shape_key_folds_range_ = shape_key_folds_range_ || uses_range(selector.sequence);
                    shape_key_folds_validity_ = shape_key_folds_validity_ || uses_validity(selector.sequence);
                    shape_key_folds_default_ = shape_key_folds_default_ || uses_default(selector.sequence);
                };
                for (const auto& root : spec->roots) classify_boundary(root);
                for (const auto& limit : spec->limits) classify_boundary(limit);
                compiling_scopes_.push_back(std::move(spec));
                compile_group(*ar, origin, source_index, layer_ordinal, nullptr);
                compiling_scopes_.pop_back();
                continue;
            } else if (ar->name == "supports") {
                if (!evaluate_supports(ar->prelude)) continue;
            } else if (ar->name == "font-face") {
                // Descriptors only; the block never contributes style rules.
                if (ar->has_block && origin != DeclarationOrigin::UserAgent) {
                    FontFace face;
                    for (const Declaration& d : ar->declarations) {
                        std::string resolved = d.value_text;
                        resolve_stylesheet_value_urls(&resolved, ar->source_url);
                        const std::string_view value = descriptor_trim(resolved);
                        if (d.property == "font-family") {
                            if (face.family.empty()) face.family = std::string(unquote_family(value));
                        } else if (d.property == "src") {
                            auto sources = font_sources(value);
                            if (!sources.empty()) {
                                face.sources = std::move(sources);
                                face.src.clear();
                                for (const auto& source : face.sources) {
                                    if (source.compare(0, 4, "url:") == 0) { face.src = source.substr(4); break; }
                                }
                            }
                        } else if (d.property == "font-weight") {
                            if (face.weight.empty()) face.weight = std::string(value);
                        } else if (d.property == "font-style") {
                            if (face.style.empty()) face.style = std::string(value);
                        }
                    }
                    // A rule with only local() sources is still a rule: the host
                    // may have that font installed.
                    if (!face.family.empty() && !face.sources.empty()) {
                        const bool seen = std::any_of(font_faces_.begin(), font_faces_.end(), [&](const FontFace& f) {
                            return f.family == face.family && f.src == face.src &&
                                   f.weight == face.weight && f.style == face.style &&
                                   f.sources == face.sources;
                        });
                        if (!seen) font_faces_.push_back(std::move(face));
                    }
                }
                continue;
            } else if (ar->name == "keyframes") {
                KeyframeAnimation animation;
                if (parse_keyframes_rule(*ar, &animation)) {
                    const auto priority = std::make_pair(static_cast<int>(origin), layer_ordinal);
                    const auto prior = keyframe_priorities_.find(animation.name);
                    if (prior == keyframe_priorities_.end() || priority.first > prior->second.first ||
                        (priority.first == prior->second.first &&
                         compare_keyframe_layers(priority.second, prior->second.second) >= 0)) {
                        keyframe_priorities_[animation.name] = priority;
                        keyframes_[animation.name] = std::move(animation);
                    }
                }
                continue;
            } else if (ar->name == "property") {
                // CSS Properties & Values L1. An invalid rule contributes no
                // descriptor and is otherwise inert, so it is dropped here
                // rather than at parse time.
                if (auto d = parse_at_property_rule(*ar)) property_registry_.register_descriptor(*d);
                continue;
            } else if (ar->name == "layer") {
                // CSS Cascade 5 §6.4.4. Two forms. The statement form,
                // `@layer a, b, c;`, only fixes the ORDER — which is the whole
                // point of writing it, since a layer's priority comes from
                // where it was first named, not from where its rules sit. The
                // block form assigns its rules to one layer.
                //
                // Doing neither, which is what happened before, left every
                // layered rule at kUnlayeredOrdinal — i.e. unlayered — so it
                // competed on specificity alone and a layered `.layered-btn`
                // beat the unlayered `button` rule it was written to lose to.
                // menu.html's button then took `padding: 4px 8px` instead of
                // `8px 20px`, which was enough for its label to stop wrapping
                // and its card to come out 108.57 against Chrome's 113.
                if (!ar->has_block) {
                    size_t start = 0;
                    while (start <= ar->prelude.size()) {
                        const size_t comma = ar->prelude.find(',', start);
                        const std::string_view piece =
                            std::string_view(ar->prelude).substr(
                                start, comma == std::string::npos ? std::string::npos
                                                                  : comma - start);
                        std::string full = layer_prefix_;
                        if (!full.empty()) full += '.';
                        full.append(trim_ascii(piece));
                        layer_ordinal_for(full);
                        if (comma == std::string::npos) break;
                        start = comma + 1;
                    }
                    continue;
                }
                const std::string_view raw_name = trim_ascii(ar->prelude);
                // An anonymous `@layer { ... }` is its own layer that nothing
                // else can name or reopen, so give it a name no author can
                // write.
                std::string full = layer_prefix_;
                if (!full.empty()) full += '.';
                if (raw_name.empty()) {
                    full += "%anon" + std::to_string(layer_names_.size());
                } else {
                    full.append(raw_name);
                }
                const int ordinal = layer_ordinal_for(full);
                const std::string saved_prefix = layer_prefix_;
                layer_prefix_ = full;
                compile_group(*ar, origin, source_index, ordinal, nesting);
                layer_prefix_ = saved_prefix;
                continue;
            } else if (ar->name == "container") {
                auto condition = trim_ascii(ar->prelude);
                CompiledContainerQuery query;
                const auto space = condition.find_first_of(" \t\r\n\f");
                const auto paren = condition.find('(');
                if (space != std::string_view::npos && space < paren &&
                    !iequals_ascii(condition.substr(0,space), "not")) {
                    query.name = condition.substr(0,space);
                    condition = trim_ascii(condition.substr(space));
                }
                if (!query.condition.parse(condition)) continue;
                const size_t index = container_queries_.size();
                container_queries_.push_back(std::move(query));
                compiling_containers_.push_back(index);
                compile_group(*ar, origin, source_index, layer_ordinal, nesting);
                compiling_containers_.pop_back();
                continue;
            } else {
                // Only recognized grouping rules may contribute style rules.
                // Unknown blocks are ignored as a unit (CSS Syntax 3), while
                // descriptors/keyframes are consumed by their own subsystems.
                // Recursing here leaked nested selectors into the global cascade.
                if (origin != DeclarationOrigin::UserAgent && ar->name != "keyframes" &&
                    ar->name != "charset" &&
                    std::find(unsupported_at_rules_.begin(), unsupported_at_rules_.end(), ar->name) == unsupported_at_rules_.end()) {
                    unsupported_at_rules_.push_back(ar->name);
                }
                continue;
            }
            compile_group(*ar, origin, source_index, layer_ordinal, nesting);
        }
    }
}

void CascadeEngine::add_stylesheet(const Stylesheet* sheet, DeclarationOrigin origin) {
    if (!sheet) return;
    ++container_generation_;
    compiling_scope_root_ = sheet->scope_root;
    compile_rules(sheet->rules, origin, &next_source_index_, kUnlayeredOrdinal);
    compiling_scope_root_ = nullptr;
    // Cached match lists were computed against the previous rule set. Without
    // this, a sheet added after the first compute() applies only to elements
    // that miss the cache — which looks like a selector bug, not a staleness
    // bug, and is exactly how it first showed up here.
    shape_cache_.clear();
}

bool CascadeEngine::container_matches(const CompiledRule& rule, const Element& element) const {
    for (size_t query : rule.container_conditions) {
        if (!container_provider_ || !container_provider_->matches(element,query)) return false;
    }
    return true;
}

bool CascadeEngine::rule_matches(const CompiledRule& rule, const Element& e,
                                 const ElementStateProvider& state, bool pseudo, int* distance) const {
    *distance = kUnscopedDistance;
    const auto body_matches = [&](const Element* root) {
        return pseudo ? selector_matches_sequence_ignoring_pseudo(rule.selector.sequence, e, state, root)
                      : selector_matches(rule.selector, e, state, root);
    };
    if (rule.scopes.empty()) return body_matches(nullptr);
    std::vector<const Element*> ancestors;
    for (const Element* a = &e; a; a = parent_el(*a)) ancestors.push_back(a);
    // Retain every valid outer instance: the closest one may not admit an
    // inner root, or the body may match only against a farther :scope. Dynamic
    // programming bounds work by tree depth rather than combinations of roots.
    std::vector<bool> previous(ancestors.size()), current(ancestors.size());
    bool nested = false;
    for (const auto& spec : rule.scopes) {
        std::fill(current.begin(), current.end(), false);
        for (size_t i = 0; i < ancestors.size(); ++i) {
            const Element* candidate = ancestors[i];
            const auto root_matches = [&](const Element* outer) {
                if (spec->roots.empty()) {
                    return candidate == (spec->implicit_root ? spec->implicit_root : ancestors.back());
                }
                for (const auto& root : spec->roots)
                    if (selector_matches(root, *candidate, state, outer)) return true;
                return false;
            };
            bool hit = !nested && root_matches(nullptr);
            if (nested) {
                for (size_t outer = i; outer < ancestors.size(); ++outer) {
                    if (previous[outer] && root_matches(ancestors[outer])) { hit = true; break; }
                }
            }
            if (!hit) continue;
            // Root is included; a limit and every descendant below it are not.
            for (size_t between = 0; hit && between < i; ++between) {
                for (const auto& limit : spec->limits) {
                    if (selector_matches(limit, *ancestors[between], state, candidate)) {
                        hit = false;
                        break;
                    }
                }
            }
            current[i] = hit;
        }
        previous.swap(current);
        nested = true;
    }
    for (size_t i = 0; i < ancestors.size(); ++i) {
        if (previous[i] && body_matches(ancestors[i])) {
            *distance = static_cast<int>(i);
            return true;
        }
    }
    return false;
}

const std::vector<MatchedDeclaration>& CascadeEngine::collect_matches(
    const Element& e, const ElementStateProvider& state) const {
    const uint64_t key = try_compute_shape_key(e, state);
    if (key != 0) {
        auto it = shape_cache_.find(key);
        if (it != shape_cache_.end()) {
            ++stats_.hits;
            // By reference. Returning by value here copied the whole match
            // list on every hit, which is most of what the cache was for.
            return it->second;
        }
        ++stats_.misses;
    } else {
        ++stats_.skipped;
    }

    std::vector<MatchedDeclaration>& out = uncached_matches_;
    out.clear();
    for (const CompiledRule& cr : rules_) {
        if (!container_matches(cr,e)) continue;
        int scope_distance;
        if (!rule_matches(cr, e, state, false, &scope_distance)) continue;
        const Specificity spec = cr.selector.specificity();
        int in_rule = 0;
        for (const Declaration& d : cr.declarations) {
            MatchedDeclaration m;
            m.declaration = &d;
            m.source_url = &cr.rule->source_url;
            m.origin = cr.origin;
            m.specificity = spec;
            m.source_index = cr.source_index;
            m.scope_distance = scope_distance;
            m.in_rule_index = in_rule++;
            m.layer_ordinal = cr.layer_ordinal;
            m.selector_text = cr.selector.source_text;
            out.push_back(std::move(m));
        }
    }

    // Inline styles are deliberately NOT collected here: a MatchedDeclaration
    // borrows its Declaration, and inline declarations are parsed on the fly,
    // so their storage would not outlive this call. compute() applies them
    // directly instead.
    std::stable_sort(out.begin(), out.end(),
                     [](const MatchedDeclaration& a, const MatchedDeclaration& b) {
                         return compare_for_cascade(a, b) < 0;
                     });
    if (key == 0) return out;
    // Moved in, not copied: the caller reads it through the map either way.
    return shape_cache_.emplace(key, std::move(out)).first->second;
}

namespace {
// CSS Color Adjustment 1 §3 -- the scheme light-dark() picks by on this
// element: its own (inherited) `color-scheme` when that names one scheme or
// says `only`, otherwise the host's preference from the media context.
bool light_dark_is_dark(const ComputedStyle& style, const MediaContext& media) {
    static const int id = CssPropertyRegistry::instance().id_of("color-scheme");
    const std::string_view raw = style.get(id);
    const bool host_dark = media.color_scheme == ColorScheme::Dark;
    if (raw.empty()) return host_dark;
    std::string lower;
    lower.reserve(raw.size());
    for (char c : raw) lower.push_back((c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c);
    if (lower == "normal") return host_dark;
    const bool only = lower.find("only") != std::string::npos;
    const bool dark = lower.find("dark") != std::string::npos;
    const bool light = lower.find("light") != std::string::npos;
    if (only || (dark != light)) return dark;
    return host_dark;
}

// Rewrites every declaration holding a light-dark() call to the branch the
// effective scheme picks. Runs AFTER var() substitution so a call that
// arrived through a custom property is seen, and before the CSS-wide keyword
// pass so an `inherit` reads the picked branch.
void substitute_light_dark(ComputedStyle* out, std::vector<int>* ids, const MediaContext& media) {
    out->copy_set_ids(*ids);
    bool decided = false, dark = false;
    std::vector<std::pair<int, std::string>> rewrites;
    for (int id : *ids) {
        const std::string_view raw = out->get(id);
        if (raw.find("light-dark(") == std::string_view::npos &&
            raw.find("LIGHT-DARK(") == std::string_view::npos &&
            raw.find("Light-Dark(") == std::string_view::npos) {
            continue;
        }
        if (!decided) { dark = light_dark_is_dark(*out, media); decided = true; }
        std::string resolved;
        if (resolve_light_dark(raw, dark, &resolved)) rewrites.emplace_back(id, std::move(resolved));
    }
    for (auto& r : rewrites) out->set(r.first, r.second);
    expand_substituted_shorthands(rewrites, out);
}
} // namespace

void CascadeEngine::compute(const Element& e, const ElementStateProvider& state,
                            const ComputedStyle* parent, ComputedStyle* out) const {
    CascadePhaseProfile profile(work_profile_, 6);
    out->clear();
    auto& reg = CssPropertyRegistry::instance();

    // 1. Declarations from stylesheets, in cascade order — later wins.
    //
    // `winner` records, per property id, the cascade key of whichever
    // declaration currently owns the slot. Only the logical-property aliasing
    // in step 3 needs it: a logical alias has to be compared against the
    // physical winner rather than assumed to lose, so the key must survive the
    // stamping loop that would otherwise discard it.
    const uint64_t gen = ++cascade_generation_;
    winner_keys_.resize(static_cast<size_t>(reg.count()));
    const auto& collected = collect_matches(e, state);
    profile.lap(0);
    // No subsequent operation collects matches again or mutates their storage.
    // Borrow the cached/uncached list through application and keyword rollback.
    const auto& matches = collected;
    profile.lap(1);
    // Custom properties first, in cascade order among themselves, so that a
    // shorthand carrying var() can be expanded AT ITS CASCADE POSITION in the
    // pass below. Expanding it after the cascade instead — the only option
    // when the value cannot be read — let `.row { border-bottom: 1px solid
    // var(--edge) }` overwrite a later `.row:last-child { border-bottom: none }`.
    for (const MatchedDeclaration& m : matches) {
        const std::string& name = m.declaration->property;
        if (name.size() > 2 && name[0] == '-' && name[1] == '-') {
            out->set(name, m.declaration->value_text);
        }
    }
    // Inline custom properties must be in place before the early shorthand
    // substitution below: `.map { background: var(--bg, #d8e6ef) }` on an
    // element with `style="--bg:#cfe0c6"` resolved against the sheet alone
    // and took the fallback — level-select's discs and roads all grey. The
    // attribute is parsed once here; step 2 applies the rest of it.
    std::vector<Declaration> inline_expanded;
    if (e.has_attribute("style")) {
        std::vector<Declaration> inline_decls;
        CssParseError perr;
        if (parse_inline_declarations(e.get_attribute("style"), /*strict=*/false,
                                      &inline_decls, &perr)) {
            inline_expanded = expand_declarations(inline_decls);
        }
        for (const Declaration& d : inline_expanded) {
            if (d.property.size() > 2 && d.property[0] == '-' && d.property[1] == '-') {
                out->set(d.property, d.value_text);
            }
        }
    }
    // Inherited custom properties live on the parent; the link is normally
    // attached only for the substitution step, so it is borrowed here for the
    // duration of this pass and released again.
    out->set_inherit_parent(parent);
    std::vector<ShorthandLonghand> early_longhands;
    for (const MatchedDeclaration& m : matches) {
        const std::string& name = m.declaration->property;
        if (name.size() > 2 && name[0] == '-' && name[1] == '-') continue;
        const std::string& value = m.declaration->value_text;
        if ((value.find("var(") != std::string::npos || value.find("VAR(") != std::string::npos) &&
            is_shorthand(name)) {
            std::string resolved;
            if (resolve_variables(value, *out, &resolved)) {
                if (m.source_url) resolve_stylesheet_value_urls(&resolved, *m.source_url);
                early_longhands.clear();
                if (expand_shorthand(name, resolved, &early_longhands) && !early_longhands.empty()) {
                    // The shorthand slot keeps the substituted text as well:
                    // readers of the raw shorthand (and tests of it) still see
                    // the resolved value there.
                    out->set(name, resolved);
                    for (const ShorthandLonghand& lh : early_longhands) {
                        out->set(lh.property, lh.value);
                        const int lid = reg.id_of(lh.property);
                        if (lid != kCustomPropertyId) {
                            out->set_important(lid, m.declaration->important);
                            winner_keys_[static_cast<size_t>(lid)] = CascadeKey::of(m, gen);
                        }
                    }
                    continue;
                }
            }
        }
        out->set(name, value);
        int id = reg.id_of(name);
        if (id != kCustomPropertyId) {
            out->set_important(id, m.declaration->important);
            winner_keys_[static_cast<size_t>(id)] = CascadeKey::of(m, gen);
        }
    }
    out->set_inherit_parent(nullptr);

    // 2. Inline styles (parsed above, so the Declaration storage does not
    // outlive the call that owns it).
    {
        {
            // Shorthands in an inline style need expanding just as much as
            // those in a rule. Stylesheet rules are expanded once at compile
            // time (see expand_declarations), and inline styles were reaching
            // the cascade unexpanded — so `style="margin: 0"` set a `margin`
            // slot nothing reads, while the UA sheet's expanded
            // `p { margin: 1em 0 }` longhands kept the element. The oracle
            // found it as a 16px offset on every `<p style="margin:0">`.
            //
            // Expanded per element rather than per rule, which is the cost of
            // an inline style; the early-out inside makes it free when the
            // attribute holds no shorthand.
            const std::vector<Declaration>& expanded = inline_expanded;
            int in_rule = 0;
            for (const Declaration& d : expanded) {
                int id = reg.id_of(d.property);
                const int idx = in_rule++;
                // An inline normal declaration loses to an existing !important
                // one; an inline !important beats a normal one.
                if (id != kCustomPropertyId && out->is_important(id) && !d.important) continue;
                out->set(d.property, d.value_text);
                if (id != kCustomPropertyId) {
                    out->set_important(id, d.important);
                    CascadeKey k;
                    k.origin = DeclarationOrigin::Author;
                    k.source_index = static_cast<int>(rules_.size());
                    k.is_inline = true;
                    k.in_rule_index = idx;
                    k.important = d.important;
                    k.generation = gen;
                    winner_keys_[static_cast<size_t>(id)] = k;
                }
            }
        }
    }

    profile.lap(2);
    // 3. Link the inherit chain, then map logical properties onto physical
    // ones, then resolve attr(), env() and var().
    //
    // The link must come FIRST: var() reads custom properties through it, so
    // `color: var(--ink)` sees an ancestor's --ink without this style having
    // to copy every ancestor custom property into itself.
    out->set_inherit_parent(parent);

    // Logical properties are mapped BEFORE substitution, so `margin-inline-start:
    // var(--gap)` becomes `margin-left: var(--gap)` and is resolved once, as the
    // physical property it will be laid out as. The mapping itself reads
    // `direction` and `writing-mode` through the inherit chain, which is why the
    // parent link has to be in place first.
    apply_logical_properties(out, winner_keys_.data(), reg.count(), gen);

    // Custom properties are resolved in their own pass first — CSS-wide
    // keywords, `@property` syntax validation and initial-value seeding —
    // matching the C#, so a `var()` reference below reads a settled value.
    resolve_custom_properties(out, parent);
    profile.lap(3);

    // attr() and env() run BEFORE var(), so a custom property whose value is
    // `attr(data-x)` or `env(safe-area-inset-top)` is already substituted by
    // the time a var() reference reads it.
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> env_drops;
        out->copy_set_ids(property_ids_);
        for (int id : property_ids_) {
            std::string raw(out->get(id));
            bool changed = false;
            if (raw.find("attr(") != std::string::npos || raw.find("ATTR(") != std::string::npos) {
                raw = resolve_attr(raw, e);
                changed = true;
            }
            if (raw.find("env(") != std::string::npos || raw.find("ENV(") != std::string::npos) {
                std::string resolved;
                if (!resolve_env(raw, env_, &resolved)) { env_drops.push_back(id); continue; }
                raw = std::move(resolved);
                changed = true;
            }
            if (changed) {
                const auto& winner = winner_keys_[static_cast<size_t>(id)];
                if (!contains_substitution(raw) && winner.generation == gen && winner.source_url)
                    resolve_stylesheet_value_urls(&raw, *winner.source_url);
                rewrites.emplace_back(id, std::move(raw));
            }
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        // An env() with no usable fallback taints its declaration, same as var().
        for (int id : env_drops) out->unset(id);
    }
    profile.lap(4);
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> drops;
        out->copy_set_ids(property_ids_);
        for (int id : property_ids_) {
            std::string_view raw = out->get(id);
            if (raw.find("var(") == std::string_view::npos &&
                raw.find("VAR(") == std::string_view::npos) {
                continue;
            }
            std::string resolved;
            if (resolve_variables(raw, *out, &resolved)) {
                const auto& winner = winner_keys_[static_cast<size_t>(id)];
                if (winner.generation == gen && winner.source_url)
                    resolve_stylesheet_value_urls(&resolved, *winner.source_url);
                rewrites.emplace_back(id, std::move(resolved));
            } else {
                // §3: invalid at computed-value time. The declaration is
                // dropped so the property falls back to its inherited or
                // initial value in step 4 — NOT left as the literal text.
                drops.push_back(id);
            }
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        expand_substituted_shorthands(rewrites, out);
        for (int id : drops) out->set(id, "");
        // A dropped declaration must not keep its slot, or step 4 would see it
        // as "already set" and skip the inherit/initial fill.
        for (int id : drops) out->set_important(id, false);
        dropped_ = drops;
    }
    substitute_light_dark(out, &property_ids_, media_);

    // 4. Inheritance and initial values are resolved LAZILY on read — see
    // ComputedStyle::set_inherit_parent. Materialising all 334 registered
    // initial values here measured at 342 ms for 1004 elements, which was the
    // whole cascade runtime; the table is immutable and shared, so an unset
    // slot can defer to it instead of copying a string per element per
    // property.
    //
    // A declaration dropped as invalid-at-computed-value-time must lose its
    // slot entirely, or the lazy read would return the empty string it was
    // stamped with rather than falling through to inherited/initial.
    for (int id : dropped_) out->unset(id);
    dropped_.clear();

    profile.lap(5);
    // 5. CSS-wide keywords, LAST — after substitution, so `inherit` yields the
    // parent's already-substituted computed value rather than the text
    // `var(--x)`. `revert`/`revert-layer` first roll back to the appropriate
    // lower-priority match. Font-size and line-height retain their inheritance
    // source when no rollback target exists.
    {
        static const int font_id = reg.id_of("font-size");
        static const int line_id = reg.id_of("line-height");
        bool font_inherited = false;
        bool line_inherited = false;
        std::vector<std::pair<int, std::string>> rewrites;
        out->copy_set_ids(property_ids_);
        for (int id : property_ids_) {
            const std::string_view raw = out->get(id);
            if (!is_css_wide_keyword(trim_ws(raw))) continue;
            const std::string_view name = reg.name_of(id);
            std::string pre(raw);
            if (winner_keys_[static_cast<size_t>(id)].generation == gen) {
                pre = pre_resolve_rollback(name, raw, matches,
                                           winner_keys_[static_cast<size_t>(id)]);
            }
            if (id == font_id || id == line_id) {
                const auto keyword = trim_ws(pre);
                // Relative syntax copied from the parent is not a new
                // declaration relative to that parent. Preserve its source
                // so layout inherits the parent's computed pixel size.
                const bool inherited = iequals_ascii(keyword, "inherit") ||
                    iequals_ascii(keyword, "unset") || iequals_ascii(keyword, "revert") ||
                    iequals_ascii(keyword, "revert-layer");
                if (id == font_id) font_inherited = inherited;
                else {
                    line_inherited = inherited;
                    // A rollback with no remaining declaration defaults to
                    // inheritance for this inherited property, not normal.
                    if (inherited) pre = "inherit";
                }
            }
            std::string resolved;
            if (resolve_css_wide_keyword(id, pre, parent, &resolved)) {
                rewrites.emplace_back(id, std::move(resolved));
            } else {
                rewrites.emplace_back(id, std::move(pre));
            }
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        if (font_inherited) out->mark_font_size_inherited();
        if (line_inherited) out->mark_line_height_inherited();
    }
    // Top-layer positioning is a computed-value adjustment after the cascade.
    if (e.is_modal() || e.is_popover_open()) {
        const auto position = out->get("position");
        if (position != "absolute" && position != "fixed") out->set("position", "absolute");
        if (out->get("display") == "contents") out->set("display", "block");
    }
}

void CascadeEngine::resolve_custom_properties(ComputedStyle* out,
                                              const ComputedStyle* parent) const {
    // Pass 1 — CSS-wide keywords and `@property` syntax validation on the
    // custom properties authored on THIS element. Runs before substitution, so
    // a `var()` reference elsewhere reads the resolved value.
    //
    // The names are snapshotted because the loop writes back into the same map.
    std::vector<std::string> names;
    names.reserve(out->custom_properties().size());
    for (const auto& kv : out->custom_properties()) names.push_back(kv.first);

    for (const std::string& name : names) {
        const std::string value(out->get(name));
        std::string resolved;

        // CSS Cascade L5 §7.3: `unset` is `inherit` for an inherited property
        // and `initial` otherwise. Every custom property looks inherited to a
        // keyword resolver, so only the registry can tell that a property
        // declared `inherits: false` takes the `initial` branch — which is why
        // this intercept sits ahead of the resolver rather than inside it.
        if (property_registry_.is_non_inheriting(name) &&
            is_css_wide_keyword(trim_ws(value)) && iequals_ascii(trim_ws(value), "unset")) {
            resolved = *property_registry_.initial_value(name);
        } else if (!resolve_css_wide_keyword_custom(name, value, parent, &resolved)) {
            resolved = value;
        }

        // A value violating the declared syntax is invalid at computed-value
        // time and falls back to the descriptor's initial value. Unregistered
        // properties have no syntax to violate.
        if (!property_registry_.validate_value(name, resolved)) {
            if (const std::string* init = property_registry_.initial_value(name)) {
                out->set(name, *init);
            }
        } else if (resolved != value) {
            out->set(name, resolved);
        }
    }

    // Pass 2 — seed the initial value of every registered property this element
    // does not declare. A non-inheriting one always takes it; an inheriting one
    // only when no ancestor supplies it, which is the root case.
    //
    // Seeding is also what makes `inherits: false` work at all here: this port
    // reads inherited custom properties lazily through the parent chain, and a
    // value stamped locally is what stops that walk.
    if (property_registry_.count() == 0) return;
    for (const auto& kv : property_registry_.all()) {
        const PropertyDescriptor& d = kv.second;
        if (out->contains_own(d.name)) continue;
        if (!d.inherits || !out->contains(d.name)) out->set(d.name, d.initial_value);
    }
}

bool CascadeEngine::compute_pseudo_element(const Element& host, std::string_view pseudo_name,
                                           const ElementStateProvider& state,
                                           const ComputedStyle& host_style,
                                           ComputedStyle* out) const {
    CascadePhaseProfile profile(work_profile_, 7);
    auto it = pseudo_rules_.find(std::string(pseudo_name));
    if (it == pseudo_rules_.end() || it->second.empty()) return false;

    // Match on the ORIGINATING element, ignoring the pseudo-element marker on
    // the rightmost compound — selector_matches deliberately refuses those, so
    // the sequence is matched directly here.
    std::vector<MatchedDeclaration> matches;
    for (const CompiledRule& cr : it->second) {
        if (!container_matches(cr,host)) continue;
        int scope_distance;
        if (!rule_matches(cr, host, state, true, &scope_distance)) continue;
        const Specificity spec = cr.selector.specificity();
        int in_rule = 0;
        for (const Declaration& d : cr.declarations) {
            MatchedDeclaration m;
            m.declaration = &d;
            m.source_url = &cr.rule->source_url;
            m.origin = cr.origin;
            m.specificity = spec;
            m.source_index = cr.source_index;
            m.scope_distance = scope_distance;
            m.in_rule_index = in_rule++;
            m.layer_ordinal = cr.layer_ordinal;
            m.selector_text = cr.selector.source_text;
            matches.push_back(std::move(m));
        }
    }
    // No matching rule means the author wrote no such pseudo for this host.
    // That is "no box", not "an empty box" — hence false rather than an empty
    // ComputedStyle.
    if (matches.empty()) return false;

    std::stable_sort(matches.begin(), matches.end(),
                     [](const MatchedDeclaration& a, const MatchedDeclaration& b) {
                         return compare_for_cascade(a, b) < 0;
                     });

    out->clear();
    auto& reg = CssPropertyRegistry::instance();
    for (const MatchedDeclaration& m : matches) {
        out->set(m.declaration->property, m.declaration->value_text);
        int id = reg.id_of(m.declaration->property);
        if (id != kCustomPropertyId) out->set_important(id, m.declaration->important);
    }

    // A pseudo participates in the host's var() namespace, so authors can
    // reference --tokens declared on the originating element — or inherited
    // by it: `.slot { --icon: '⚔' } .slot-icon::before { content:
    // var(--icon) }` reads the token through the host's inherit chain.
    for (const ComputedStyle* s = &host_style; s; s = s->inherit_parent()) {
        for (const auto& kv : s->custom_properties()) {
            if (!out->contains(kv.first)) out->set(kv.first, kv.second);
        }
    }
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> drops;
        out->copy_set_ids(property_ids_);
        for (int id : property_ids_) {
            std::string_view raw = out->get(id);
            if (raw.find("var(") == std::string_view::npos &&
                raw.find("VAR(") == std::string_view::npos) {
                continue;
            }
            std::string resolved;
            if (resolve_variables(raw, *out, &resolved)) {
                for (auto m = matches.rbegin(); m != matches.rend(); ++m) {
                    if (m->declaration->property != reg.name_of(id)) continue;
                    if (m->source_url) resolve_stylesheet_value_urls(&resolved, *m->source_url);
                    break;
                }
                rewrites.emplace_back(id, std::move(resolved));
            }
            else drops.push_back(id);
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        expand_substituted_shorthands(rewrites, out);
        for (int id : drops) out->set(id, "");
        dropped_ = drops;
    }
    substitute_light_dark(out, &property_ids_, media_);

    // Inheritance source is the ORIGINATING element, not the host's parent.
    const bool has_drops = !dropped_.empty();
    static const int font_id = reg.id_of("font-size");
    static const int line_id = reg.id_of("line-height");
    auto was_dropped = [&](int id) {
        return has_drops && std::find(dropped_.begin(), dropped_.end(), id) != dropped_.end();
    };
    // Read through the host's inherit chain: an element's own slots hold only
    // what the cascade set on it, and a `quotes` or `color` declared on an
    // ancestor lives up the chain (ComputedStyle::get walks it; contains()
    // does not). Checking contains() here left every such pseudo at the
    // initial value — `q::before` opened with the English pair no matter
    // what `quotes` the author set on the wrapper.
    for (int id = 0; id < reg.count(); ++id) {
        if (out->contains(id) && !was_dropped(id)) continue;
        if (reg.is_inherited(id)) {
            const std::string_view v = host_style.get(id);
            if (!v.empty()) {
                out->set(id, v);
                if (id == font_id) out->mark_font_size_inherited();
                if (id == line_id) out->mark_line_height_inherited();
            }
        } else {
            std::string_view initial = reg.initial_value(id);
            if (!initial.empty()) out->set(id, initial);
        }
    }
    dropped_.clear();
    out->set_inherit_parent(&host_style);
    if (!out->font_size_inherited()) {
        const auto raw = out->get(font_id);
        if (is_css_wide_keyword(trim_ws(raw))) {
            // Pseudos inherit from their originating element too. Resolve
            // initial as a value, but keep computed inheritance distinct.
            if (iequals_ascii(trim_ws(raw), "initial")) {
                out->set(font_id, reg.initial_value(font_id));
            } else {
                out->set(font_id, host_style.get(font_id));
                out->mark_font_size_inherited();
            }
        }
    }
    if (!out->line_height_inherited()) {
        const auto raw = out->get(line_id);
        if (is_css_wide_keyword(trim_ws(raw))) {
            std::string pre(raw);
            for (auto m = matches.rbegin(); m != matches.rend(); ++m) {
                if (reg.id_of(m->declaration->property) != line_id) continue;
                pre = pre_resolve_rollback(reg.name_of(line_id), raw, matches, CascadeKey::of(*m, 0));
                break;
            }
            if (!is_css_wide_keyword(trim_ws(pre))) {
                out->set(line_id, pre);
            } else if (iequals_ascii(trim_ws(pre), "initial")) {
                out->set(line_id, reg.initial_value(line_id));
            } else {
                out->set(line_id, host_style.get(line_id));
                out->mark_line_height_inherited();
            }
        }
    }
    return true;
}

void CascadeEngine::report_work_profile() const {
    if (!work_profile_.elements && !work_profile_.pseudos) return;
    static constexpr const char* names[] = {"match", "match copy", "declarations", "logical/custom",
                                           "attr/env", "variables", "keywords", "pseudos"};
    std::fprintf(stderr, "    cascade work: %zu elements, %zu pseudo queries\n",
                 work_profile_.elements, work_profile_.pseudos);
    for (size_t i = 0; i < work_profile_.ms.size(); ++i)
        std::fprintf(stderr, "    cascade part: %-14s %.3f ms\n", names[i], work_profile_.ms[i]);
    work_profile_ = {};
}

bool CascadeEngine::resolve_pseudo_content(const ComputedStyle& pseudo_style,
                                           std::string* text) {
    std::string_view raw = pseudo_style.get("content");
    if (raw.empty()) return false;
    // `none` and `normal` both suppress the box.
    if (raw == "none" || raw == "normal") return false;

    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return false;
    if (v->kind() == CssValueKind::String) {
        *text = static_cast<const CssString&>(*v).text;
        return true;   // `content: ""` still generates a box, with empty text
    }
    // attr(), counter(), url() and image content are not handled in v1; the
    // caller treats false as "no pseudo box" rather than rendering the literal
    // function text.
    return false;
}

namespace {

void append_content_item(const CssValue& v, const Element* host, std::string* out) {
    switch (v.kind()) {
    case CssValueKind::String:
        *out += static_cast<const CssString&>(v).text;
        return;
    case CssValueKind::List:
        for (const CssValuePtr& item : static_cast<const CssValueList&>(v).items) {
            if (item) append_content_item(*item, host, out);
        }
        return;
    case CssValueKind::Keyword:
    case CssValueKind::Identifier: {
        const std::string& name = v.kind() == CssValueKind::Keyword
                                      ? static_cast<const CssKeyword&>(v).name
                                      : static_cast<const CssIdentifier&>(v).name;
        // CSS Generated Content §3.2: the `quotes` initial pair for English.
        if (name == "open-quote") *out += "\xE2\x80\x9C";
        else if (name == "close-quote") *out += "\xE2\x80\x9D";
        return;
    }
    case CssValueKind::FunctionCall: {
        const auto& f = static_cast<const CssFunctionCall&>(v);
        if (f.name == "attr" && host && !f.arguments.empty() && f.arguments[0]) {
            const CssValue& a = *f.arguments[0];
            std::string attr;
            if (a.kind() == CssValueKind::Identifier) attr = static_cast<const CssIdentifier&>(a).name;
            else if (a.kind() == CssValueKind::Keyword) attr = static_cast<const CssKeyword&>(a).name;
            else if (a.kind() == CssValueKind::List) {
                const auto& l = static_cast<const CssValueList&>(a);
                if (!l.items.empty() && l.items[0]) {
                    if (l.items[0]->kind() == CssValueKind::Identifier)
                        attr = static_cast<const CssIdentifier&>(*l.items[0]).name;
                    else if (l.items[0]->kind() == CssValueKind::Keyword)
                        attr = static_cast<const CssKeyword&>(*l.items[0]).name;
                }
            }
            if (!attr.empty()) *out += std::string(host->get_attribute(attr));
        }
        // counter(), counters(), url(), image-set(): a box with no text.
        return;
    }
    default:
        return;
    }
}

} // namespace

bool CascadeEngine::resolve_pseudo_content(const ComputedStyle& pseudo_style, const Element* host,
                                           std::string* text) {
    std::string_view raw = pseudo_style.get("content");
    if (raw.empty() || raw == "none" || raw == "normal") return false;
    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return false;
    if (v->kind() == CssValueKind::Keyword || v->kind() == CssValueKind::Identifier) {
        const std::string& name = v->kind() == CssValueKind::Keyword
                                      ? static_cast<const CssKeyword&>(*v).name
                                      : static_cast<const CssIdentifier&>(*v).name;
        if (name == "none" || name == "normal" || name == "inherit" || name == "initial" ||
            name == "unset") {
            return false;
        }
    }
    text->clear();
    append_content_item(*v, host, text);
    return true;
}

namespace {

// CSS Fonts 4 §2.1's generic families (and the `ui-*` and `emoji`/`math`/
// `fangsong` ones), plus the CSS-wide keywords: none names a font.
bool is_generic_or_keyword(std::string_view name) {
    static constexpr std::string_view kSkip[] = {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "ui-serif",
        "ui-sans-serif", "ui-monospace", "ui-rounded", "emoji", "math", "fangsong",
        "inherit", "initial", "unset", "revert", "revert-layer",
    };
    for (std::string_view s : kSkip) if (iequals_ascii(name, s)) return true;
    return false;
}

}   // namespace

void append_font_family_names(std::string_view stack, std::vector<std::string>* out) {
    if (!out) return;
    // Split at top-level commas: a comma inside quotes or parentheses
    // (`var(--a, b)`) is part of the entry.
    size_t start = 0;
    int depth = 0;
    char quote = 0;
    for (size_t i = 0; i <= stack.size(); ++i) {
        const bool end = i == stack.size();
        const char c = end ? ',' : stack[i];
        if (!end && quote) {
            if (c == '\\' && i + 1 < stack.size()) ++i;
            else if (c == quote) quote = 0;
            continue;
        }
        if (!end && (c == '"' || c == '\'')) { quote = c; continue; }
        if (!end && c == '(') { ++depth; continue; }
        if (!end && c == ')') { if (depth > 0) --depth; continue; }
        if (c != ',' || depth != 0) continue;
        std::string_view entry = stack.substr(start, i - start);
        start = i + 1;
        while (!entry.empty() && (entry.front() == ' ' || entry.front() == '\t' || entry.front() == '\n' || entry.front() == '\r')) entry.remove_prefix(1);
        while (!entry.empty() && (entry.back() == ' ' || entry.back() == '\t' || entry.back() == '\n' || entry.back() == '\r')) entry.remove_suffix(1);
        if (entry.size() >= 2 && (entry.front() == '"' || entry.front() == '\'') && entry.back() == entry.front()) {
            entry = entry.substr(1, entry.size() - 2);
        }
        if (entry.empty() || entry.find("var(") != std::string_view::npos || is_generic_or_keyword(entry)) continue;
        bool seen = false;
        for (const std::string& have : *out) if (iequals_ascii(have, entry)) { seen = true; break; }
        if (!seen) out->emplace_back(entry);
    }
}

std::vector<std::string> CascadeEngine::font_family_names() const {
    std::vector<std::string> names;
    for (const CompiledRule& rule : rules_) {
        if (rule.origin != DeclarationOrigin::Author) continue;
        for (const Declaration& d : rule.declarations) {
            if (d.property == "font-family") append_font_family_names(d.value_text, &names);
        }
    }
    return names;
}

} // namespace weva
