#include "weva/cascade.h"

#include "weva/dom.h"

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

} // namespace

int compare_for_cascade(const MatchedDeclaration& x, const MatchedDeclaration& y) {
    // Earlier in the sorted list = lower precedence; the LAST entry wins.

    // Importance is the dominant axis.
    if (x.declaration->important != y.declaration->important) {
        return x.declaration->important ? 1 : -1;
    }

    // Within an importance class the origin ordering flips.
    if (x.declaration->important) {
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
    if (x.declaration->important) {
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

void CascadeEngine::clear() { rules_.clear(); }

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
                CompiledRule cr;
                cr.selector = std::move(cs);
                cr.rule = sr;
                cr.origin = origin;
                cr.source_index = (*source_index)++;
                cr.layer_ordinal = layer_ordinal;
                rules_.push_back(std::move(cr));
            }
            compile_rules(sr->nested_rules, origin, source_index, layer_ordinal);
        } else {
            const auto* ar = static_cast<const GenericAtRule*>(r.get());
            // Conditional at-rules (@media, @supports, @container) are not
            // evaluated yet; their bodies are compiled unconditionally so the
            // rules exist. Evaluating the conditions is a later slice — until
            // then a non-matching @media block WILL apply, which is why this
            // is called out rather than left implicit.
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
    return out;
}

void CascadeEngine::compute(const Element& e, const ElementStateProvider& state,
                            const ComputedStyle* parent, ComputedStyle* out) const {
    out->clear();
    auto& reg = CssPropertyRegistry::instance();

    // 1. Declarations from stylesheets, in cascade order — later wins.
    std::vector<MatchedDeclaration> matches = collect_matches(e, state);
    for (const MatchedDeclaration& m : matches) {
        out->set(m.declaration->property, m.declaration->value_text);
        int id = reg.id_of(m.declaration->property);
        if (id != kCustomPropertyId) out->set_important(id, m.declaration->important);
    }

    // 2. Inline styles. Parsed here rather than in collect_matches so the
    // Declaration storage does not outlive the call that owns it.
    if (e.has_attribute("style")) {
        std::vector<Declaration> inline_decls;
        CssParseError perr;
        if (parse_inline_declarations(e.get_attribute("style"), /*strict=*/false,
                                      &inline_decls, &perr)) {
            for (const Declaration& d : inline_decls) {
                int id = reg.id_of(d.property);
                // An inline normal declaration loses to an existing !important
                // one; an inline !important beats a normal one.
                if (id != kCustomPropertyId && out->is_important(id) && !d.important) continue;
                out->set(d.property, d.value_text);
                if (id != kCustomPropertyId) out->set_important(id, d.important);
            }
        }
    }

    // 3. Inheritance, then initial values for anything still unset.
    for (int id = 0; id < reg.count(); ++id) {
        if (out->contains(id)) continue;
        if (parent && reg.is_inherited(id) && parent->contains(id)) {
            out->set(id, parent->get(id));
        } else {
            std::string_view initial = reg.initial_value(id);
            if (!initial.empty()) out->set(id, initial);
        }
    }

    // 4. Custom properties inherit unconditionally (no @property registry yet,
    // so `inherits: false` is not honoured — noted rather than silently wrong).
    if (parent) {
        for (const auto& kv : parent->custom_properties()) {
            if (!out->contains(kv.first)) out->set(kv.first, kv.second);
        }
    }
}

} // namespace weva
