#pragma once
#include "weva/computed_style.h"
#include "weva/css_rule.h"
#include "weva/media.h"
#include "weva/selector.h"

#include <map>
#include <memory>
#include <string>
#include <vector>

// Ports the resolution core of Runtime/Css/Cascade/CascadeEngine.cs: match
// collection, the CSS Cascade 5 §6.4.1 ordering, and inheritance.
//
// Deferred to later slices (each is its own C# file): cascade layers beyond
// the ordinal plumbing, var()/env()/attr() resolution, @supports evaluation,
// counters, pseudo-elements, logical-property mapping, and the incremental
// invalidation state. The ordering axes for layers are present in the
// comparator because removing one axis silently changes who wins.

namespace weva {

enum class DeclarationOrigin { UserAgent = 0, User = 1, Author = 2 };

// Unlayered rules outrank every layered rule for normal declarations, and LOSE
// to them for !important. A large sentinel gives that ordering for free.
constexpr int kUnlayeredOrdinal = 0x7FFFFFFF;

struct MatchedDeclaration {
    const Declaration* declaration = nullptr;
    DeclarationOrigin origin = DeclarationOrigin::Author;
    Specificity specificity;
    int source_index = 0;    // rule order within the document
    bool is_inline = false;
    int in_rule_index = 0;   // declaration order within one rule
    int layer_ordinal = kUnlayeredOrdinal;
    std::string selector_text;
};

// True when x should lose to y — i.e. x sorts EARLIER (lower precedence).
// Exposed for tests because every axis here is a place a port can silently
// invert an outcome.
int compare_for_cascade(const MatchedDeclaration& x, const MatchedDeclaration& y);

struct OriginatedStylesheet {
    const Stylesheet* sheet = nullptr;
    DeclarationOrigin origin = DeclarationOrigin::Author;
};

class CascadeEngine {
public:
    // Conditional at-rules are evaluated at compile time against this context,
    // so a non-matching @media block contributes no rules at all. Changing it
    // requires recompiling the sheets.
    void set_media_context(const MediaContext& ctx) { media_ = ctx; }
    const MediaContext& media_context() const { return media_; }

    void add_stylesheet(const Stylesheet* sheet, DeclarationOrigin origin);
    void clear();

    // Collects every declaration matching `e`, already sorted so the last
    // entry wins. Exposed for DevTools-style cascade traces and for tests.
    std::vector<MatchedDeclaration> collect_matches(
        const Element& e, const ElementStateProvider& state) const;

    // Computes the element's style. `parent` supplies inherited values; pass
    // null for the root.
    void compute(const Element& e, const ElementStateProvider& state,
                 const ComputedStyle* parent, ComputedStyle* out) const;

    // Computes a pseudo-element's style on `host` (name without the colons:
    // "before", "after", "marker", ...).
    //
    // Returns FALSE when no author rule targets that pseudo on that host —
    // which is the signal for "generate no box at all", not "generate an
    // empty one". A pseudo-element inherits from its ORIGINATING element, not
    // from the host's parent, so `host_style` is the inheritance source.
    bool compute_pseudo_element(const Element& host, std::string_view pseudo_name,
                                const ElementStateProvider& state,
                                const ComputedStyle& host_style,
                                ComputedStyle* out) const;

    // CSS 2.1 §12.2: a ::before/::after box exists only when `content`
    // resolves to something other than `none`/`normal`. v1 handles string
    // content; anything else (attr(), counter(), url()) reports false, which
    // the box builder treats as "no pseudo box".
    static bool resolve_pseudo_content(const ComputedStyle& pseudo_style,
                                       std::string* text);

private:
    struct CompiledRule {
        CompiledSelector selector;
        const StyleRule* rule = nullptr;
        DeclarationOrigin origin = DeclarationOrigin::Author;
        int source_index = 0;
        int layer_ordinal = kUnlayeredOrdinal;
    };
    void compile_rules(const std::vector<RulePtr>& rules, DeclarationOrigin origin,
                       int* source_index, int layer_ordinal);

    std::vector<CompiledRule> rules_;
    // Pseudo-element rules never match a real element, so they live in their
    // own buckets keyed by pseudo name rather than being scanned and rejected
    // once per element.
    std::map<std::string, std::vector<CompiledRule>> pseudo_rules_;
    MediaContext media_;
    // Ids whose declaration was invalid at computed-value time in the current
    // compute() call, so the inherit/initial pass knows to refill them.
    mutable std::vector<int> dropped_;
};

} // namespace weva
