#include "weva/cascade.h"

#include "weva/css_value.h"
#include "weva/env_attr.h"
#include "weva/logical.h"
#include "weva/dom.h"
#include "weva/variable_resolver.h"

#include <algorithm>

namespace weva {

namespace {

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

void classify_selector(const CompoundSequence& seq, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index);

} // namespace

CascadeKey CascadeKey::of(const MatchedDeclaration& m, uint64_t generation) {
    CascadeKey k;
    k.specificity = m.specificity;
    k.source_index = m.source_index;
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
    if (int i = cmp_int(x.source_index, y.source_index); i != 0) return i;
    return cmp_int(x.in_rule_index, y.in_rule_index);
}

void CascadeEngine::clear() {
    rules_.clear();
    pseudo_rules_.clear();
    shape_cache_.clear();
    cache_unsafe_sibling_composition_ = false;
    cache_unsafe_has_ = false;
    shape_key_folds_sibling_index_ = false;
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
                       bool* has_has, bool* folds_index);

void classify_compound(const CompoundSelector& c, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index) {
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
            default:
                break;
        }
        for (const auto& inner : pc.inner_list) {
            classify_selector(*inner, unsafe_sibling, has_has, folds_index);
        }
        for (const auto& inner : pc.nth_of_filter) {
            classify_selector(*inner, unsafe_sibling, has_has, folds_index);
        }
    }
}

void classify_selector(const CompoundSequence& seq, bool* unsafe_sibling,
                       bool* has_has, bool* folds_index) {
    for (Combinator cb : seq.combinators) {
        // `p + p` / `p ~ p`: the match depends on preceding-sibling
        // composition, not just this element's own shape.
        if (cb == Combinator::AdjacentSibling || cb == Combinator::GeneralSibling) {
            *unsafe_sibling = true;
        }
    }
    for (const auto& c : seq.compounds) {
        classify_compound(c, unsafe_sibling, has_has, folds_index);
    }
}

const Element* parent_el(const Element& e) {
    const Node* p = e.parent();
    return (p && p->is_element()) ? static_cast<const Element*>(p) : nullptr;
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
    if (cache_unsafe_has_) return 0;

    uint64_t h = kFnvOffset;
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
        uint64_t anc = 0;
        anc ^= hash_str(a->tag_name());
        anc ^= hash_str(a->id()) * 257ULL;
        anc ^= hash_class_tokens(a->class_name());
        anc ^= static_cast<uint64_t>(state.state_of(*a)) * 2654435761ULL;
        h ^= anc;
        h *= kFnvPrime;
    }

    // The element's own state bits.
    h ^= static_cast<uint64_t>(state.state_of(e)) * 40503ULL;
    h *= kFnvPrime;

    return h == 0 ? 1 : h;   // never collide with the "do not cache" sentinel
}

void CascadeEngine::compile_rules(const std::vector<RulePtr>& rules, DeclarationOrigin origin,
                                  int* source_index, int layer_ordinal) {
    for (const auto& r : rules) {
        if (r->kind() == RuleKind::Style) {
            const auto* sr = static_cast<const StyleRule*>(r.get());
            for (const auto& sel_text : sr->selectors) {
                CompiledSelector cs;
                SelectorParseError err;
                // A selector that fails to parse drops its rule rather than the
                // sheet, matching the C#'s per-rule error containment.
                if (!parse_selector(sel_text, &cs, &err)) continue;
                // Classify BEFORE moving: the shape cache's soundness depends
                // on spotting sibling-composition and :has() selectors anywhere
                // in the sheet.
                classify_selector(cs.sequence, &cache_unsafe_sibling_composition_,
                                  &cache_unsafe_has_, &shape_key_folds_sibling_index_);
                const std::string* pseudo = cs.sequence.pseudo_element();
                std::string pseudo_name = pseudo ? *pseudo : std::string();
                CompiledRule cr;
                cr.selector = std::move(cs);
                cr.rule = sr;
                cr.origin = origin;
                cr.source_index = (*source_index)++;
                cr.layer_ordinal = layer_ordinal;
                if (!pseudo_name.empty()) {
                    pseudo_rules_[pseudo_name].push_back(std::move(cr));
                } else {
                    rules_.push_back(std::move(cr));
                }
            }
            compile_rules(sr->nested_rules, origin, source_index, layer_ordinal);
        } else {
            const auto* ar = static_cast<const GenericAtRule*>(r.get());
            // Conditional at-rules gate their body. A false condition
            // contributes no rules at all, rather than contributing rules that
            // silently apply.
            if (ar->name == "media") {
                if (!evaluate_media_query(ar->prelude, media_)) continue;
            } else if (ar->name == "supports") {
                if (!evaluate_supports(ar->prelude)) continue;
            } else if (ar->name == "container") {
                // @container needs per-element container sizes, which the
                // layout engine has not been ported to supply. Skipping the
                // body would hide styles that should apply; applying it
                // unconditionally shows styles that may not. Applying is the
                // less-wrong default for a UI toolkit, and it is recorded in
                // PORT_PLAN.md rather than left to be discovered.
            }
            compile_rules(ar->nested_rules, origin, source_index, layer_ordinal);
        }
    }
}

void CascadeEngine::add_stylesheet(const Stylesheet* sheet, DeclarationOrigin origin) {
    if (!sheet) return;
    int source_index = static_cast<int>(rules_.size());
    compile_rules(sheet->rules, origin, &source_index, kUnlayeredOrdinal);
}

std::vector<MatchedDeclaration> CascadeEngine::collect_matches(
    const Element& e, const ElementStateProvider& state) const {
    const uint64_t key = try_compute_shape_key(e, state);
    if (key != 0) {
        auto it = shape_cache_.find(key);
        if (it != shape_cache_.end()) {
            ++stats_.hits;
            return it->second;
        }
        ++stats_.misses;
    } else {
        ++stats_.skipped;
    }

    std::vector<MatchedDeclaration> out;
    for (const CompiledRule& cr : rules_) {
        if (!selector_matches(cr.selector, e, state)) continue;
        const Specificity spec = cr.selector.specificity();
        int in_rule = 0;
        for (const Declaration& d : cr.rule->declarations) {
            MatchedDeclaration m;
            m.declaration = &d;
            m.origin = cr.origin;
            m.specificity = spec;
            m.source_index = cr.source_index;
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
    if (key != 0) shape_cache_.emplace(key, out);
    return out;
}

void CascadeEngine::compute(const Element& e, const ElementStateProvider& state,
                            const ComputedStyle* parent, ComputedStyle* out) const {
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
    std::vector<MatchedDeclaration> matches = collect_matches(e, state);
    for (const MatchedDeclaration& m : matches) {
        out->set(m.declaration->property, m.declaration->value_text);
        int id = reg.id_of(m.declaration->property);
        if (id != kCustomPropertyId) {
            out->set_important(id, m.declaration->important);
            winner_keys_[static_cast<size_t>(id)] = CascadeKey::of(m, gen);
        }
    }

    // 2. Inline styles. Parsed here rather than in collect_matches so the
    // Declaration storage does not outlive the call that owns it.
    if (e.has_attribute("style")) {
        std::vector<Declaration> inline_decls;
        CssParseError perr;
        if (parse_inline_declarations(e.get_attribute("style"), /*strict=*/false,
                                      &inline_decls, &perr)) {
            int in_rule = 0;
            for (const Declaration& d : inline_decls) {
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

    // attr() and env() run BEFORE var(), so a custom property whose value is
    // `attr(data-x)` or `env(safe-area-inset-top)` is already substituted by
    // the time a var() reference reads it.
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> env_drops;
        for (int id : out->set_ids()) {
            std::string raw(out->get(id));
            bool changed = false;
            if (raw.find("attr(") != std::string::npos || raw.find("ATTR(") != std::string::npos) {
                raw = resolve_attr(raw, e);
                changed = true;
            }
            if (raw.find("env(") != std::string::npos || raw.find("ENV(") != std::string::npos) {
                std::string resolved;
                if (!resolve_env(raw, &resolved)) { env_drops.push_back(id); continue; }
                raw = std::move(resolved);
                changed = true;
            }
            if (changed) rewrites.emplace_back(id, std::move(raw));
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        // An env() with no usable fallback taints its declaration, same as var().
        for (int id : env_drops) out->unset(id);
    }
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> drops;
        for (int id : out->set_ids()) {
            std::string_view raw = out->get(id);
            if (raw.find("var(") == std::string_view::npos &&
                raw.find("VAR(") == std::string_view::npos) {
                continue;
            }
            std::string resolved;
            if (resolve_variables(raw, *out, &resolved)) {
                rewrites.emplace_back(id, std::move(resolved));
            } else {
                // §3: invalid at computed-value time. The declaration is
                // dropped so the property falls back to its inherited or
                // initial value in step 4 — NOT left as the literal text.
                drops.push_back(id);
            }
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        for (int id : drops) out->set(id, "");
        // A dropped declaration must not keep its slot, or step 4 would see it
        // as "already set" and skip the inherit/initial fill.
        for (int id : drops) out->set_important(id, false);
        dropped_ = drops;
    }

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
}

bool CascadeEngine::compute_pseudo_element(const Element& host, std::string_view pseudo_name,
                                           const ElementStateProvider& state,
                                           const ComputedStyle& host_style,
                                           ComputedStyle* out) const {
    auto it = pseudo_rules_.find(std::string(pseudo_name));
    if (it == pseudo_rules_.end() || it->second.empty()) return false;

    // Match on the ORIGINATING element, ignoring the pseudo-element marker on
    // the rightmost compound — selector_matches deliberately refuses those, so
    // the sequence is matched directly here.
    std::vector<MatchedDeclaration> matches;
    for (const CompiledRule& cr : it->second) {
        if (!selector_matches_sequence_ignoring_pseudo(cr.selector.sequence, host, state)) {
            continue;
        }
        const Specificity spec = cr.selector.specificity();
        int in_rule = 0;
        for (const Declaration& d : cr.rule->declarations) {
            MatchedDeclaration m;
            m.declaration = &d;
            m.origin = cr.origin;
            m.specificity = spec;
            m.source_index = cr.source_index;
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
    // reference --tokens declared on the originating element.
    for (const auto& kv : host_style.custom_properties()) {
        if (!out->contains(kv.first)) out->set(kv.first, kv.second);
    }
    {
        std::vector<std::pair<int, std::string>> rewrites;
        std::vector<int> drops;
        for (int id : out->set_ids()) {
            std::string_view raw = out->get(id);
            if (raw.find("var(") == std::string_view::npos &&
                raw.find("VAR(") == std::string_view::npos) {
                continue;
            }
            std::string resolved;
            if (resolve_variables(raw, *out, &resolved)) rewrites.emplace_back(id, std::move(resolved));
            else drops.push_back(id);
        }
        for (auto& r : rewrites) out->set(r.first, r.second);
        for (int id : drops) out->set(id, "");
        dropped_ = drops;
    }

    // Inheritance source is the ORIGINATING element, not the host's parent.
    const bool has_drops = !dropped_.empty();
    auto was_dropped = [&](int id) {
        return has_drops && std::find(dropped_.begin(), dropped_.end(), id) != dropped_.end();
    };
    for (int id = 0; id < reg.count(); ++id) {
        if (out->contains(id) && !was_dropped(id)) continue;
        if (reg.is_inherited(id) && host_style.contains(id)) {
            out->set(id, host_style.get(id));
        } else {
            std::string_view initial = reg.initial_value(id);
            if (!initial.empty()) out->set(id, initial);
        }
    }
    dropped_.clear();
    return true;
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

} // namespace weva
