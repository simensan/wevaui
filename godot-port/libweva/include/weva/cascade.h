#pragma once
#include "weva/at_property.h"
#include "weva/computed_style.h"
#include "weva/css_rule.h"
#include "weva/media.h"
#include "weva/selector.h"

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <unordered_set>
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

// What a `:hover` or `:active` in the compiled sheets can reach: the keys of
// every compound one of them sits on. `everything` is the honest answer when a
// compound gives no key to go on.
//
// Held as sets rather than a bool because the user-agent sheet contains one
// `:hover` rule, and a bool would therefore be true for every document.
struct StateReach {
    std::unordered_set<std::string> classes;
    std::unordered_set<std::string> ids;
    std::unordered_set<std::string> tags;
    bool everything = false;

    bool empty() const {
        return !everything && classes.empty() && ids.empty() && tags.empty();
    }
};

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

// The ordering fields of a MatchedDeclaration without the borrowed Declaration
// pointer or the selector text — trivially copyable, so a per-property winner
// table costs no allocation and no string construction.
//
// `generation` is what makes such a table reusable: the engine bumps a counter
// per compute() call and a slot counts as set only when its stamp matches, so
// 334 slots never have to be cleared between elements.
struct CascadeKey {
    Specificity specificity;
    int source_index = 0;
    int in_rule_index = 0;
    int layer_ordinal = kUnlayeredOrdinal;
    DeclarationOrigin origin = DeclarationOrigin::Author;
    bool is_inline = false;
    bool important = false;
    uint64_t generation = 0;

    static CascadeKey of(const MatchedDeclaration& m, uint64_t generation);
};

// True when x should lose to y — i.e. x sorts EARLIER (lower precedence).
// Exposed for tests because every axis here is a place a port can silently
// invert an outcome.
int compare_for_cascade(const MatchedDeclaration& x, const MatchedDeclaration& y);
int compare_for_cascade(const CascadeKey& x, const CascadeKey& y);

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

    // Typed custom properties declared by `@property` in the compiled sheets.
    const AtPropertyRegistry& property_registry() const { return property_registry_; }

    // Match-cache statistics, for tests and profiling.
    struct CacheStats { int64_t hits = 0; int64_t misses = 0; int64_t skipped = 0; };
    const CacheStats& cache_stats() const { return stats_; }
    void reset_cache_stats() { stats_ = CacheStats{}; }
    void invalidate_cache() { shape_cache_.clear(); }

    // Whether any compiled selector can match on something other than the
    // element's own subtree. Both are already tracked for the shape cache;
    // they answer a second question too, which is how far an attribute change
    // can reach. A sibling combinator lets it reach the elements after it; a
    // :has() lets it reach its ancestors, and then anything they select.
    bool has_sibling_selectors() const { return cache_unsafe_sibling_composition_; }
    bool has_has_selectors() const { return cache_unsafe_has_; }
    // What `:hover` and `:active` can reach.
    //
    // A pointer move flips a hover chain on every frame it moves, and each flip
    // marks its elements for restyle -- which re-cascades their SUBTREES. When
    // no rule can match the flipped element differently, none of that can move
    // a single computed value, and the whole walk is waste: `stats.html` was
    // paying between 0.5 and 8 ms of cascade on every frame the pointer moved,
    // for a sheet with no `:hover` in it.
    const StateReach& hover_reach() const { return hover_reach_; }
    const StateReach& active_reach() const { return active_reach_; }
    // Whether a state flip on `e` can change what matches it.
    bool state_observable(const StateReach& reach, const Element& e) const;

    // Collects every declaration matching `e`, already sorted so the last
    // entry wins. Exposed for DevTools-style cascade traces and for tests.
    //
    // Returns a REFERENCE into storage the engine owns, valid until the next
    // call. It used to return by value, which made every cache hit pay for a
    // heap allocation and a copy of the whole match list -- so the shape cache
    // charged for itself on every element it was supposed to be saving.
    const std::vector<MatchedDeclaration>& collect_matches(
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
    // The box builder's form: concatenates a `content` list — strings,
    // attr() read from `host`, open-quote/close-quote as the English pair —
    // and yields an empty string (still a box) for counter()/url()/image
    // items, which have no text rendering here yet.
    static bool resolve_pseudo_content(const ComputedStyle& pseudo_style, const Element* host,
                                       std::string* text);

private:
    struct CompiledRule {
        CompiledSelector selector;
        const StyleRule* rule = nullptr;
        DeclarationOrigin origin = DeclarationOrigin::Author;
        int source_index = 0;
        int layer_ordinal = kUnlayeredOrdinal;
        // The rule's declarations with every shorthand replaced by its
        // longhands. Owned here rather than borrowed from the stylesheet
        // because expansion synthesises declarations that exist nowhere in the
        // source.
        //
        // A MatchedDeclaration points into this vector's heap buffer, which
        // survives `rules_` growing: moving a vector transfers the buffer, it
        // does not copy it. The vector must therefore never be modified after
        // compilation.
        std::vector<Declaration> declarations;
    };
    void compile_rules(const std::vector<RulePtr>& rules, DeclarationOrigin origin,
                       int* source_index, int layer_ordinal);

    // CSS Cascade 5 §6.4.4. Cascade layers, in the order they were first named
    // — by an `@layer a, b, c;` statement or by the first `@layer a { ... }`
    // block that mentions them. The ordinal IS the index, so a later layer wins
    // for normal declarations and loses for `!important`, which is what
    // compare_declarations already implements. Unlayered rules keep
    // kUnlayeredOrdinal and so beat every layer.
    std::vector<std::string> layer_names_;
    // The enclosing layer's full name while compiling a nested `@layer` block,
    // so `@layer a { @layer b { ... } }` names the inner layer `a.b`.
    std::string layer_prefix_;
    int layer_ordinal_for(std::string_view name);
    // Resolves one element's custom properties: CSS-wide keywords, `@property`
    // syntax validation, and initial-value seeding. Runs before substitution.
    void resolve_custom_properties(ComputedStyle* out, const ComputedStyle* parent) const;

    // Shape-keyed match cache. Two elements whose tag/id/classes/attributes
    // AND whose whole ancestor chain hash identically must match the same rule
    // set, so the match list can be shared. Getting the key wrong does not
    // fail loudly — it silently serves one element's styles to another.
    uint64_t try_compute_shape_key(const Element& e, const ElementStateProvider& state) const;

    std::vector<CompiledRule> rules_;
    mutable std::map<uint64_t, std::vector<MatchedDeclaration>> shape_cache_;
    // Where an UNCACHEABLE element's matches live: an element with an inline
    // style, or any element at all when the sheets use sibling combinators or
    // :has(). Reused rather than allocated per element.
    mutable std::vector<MatchedDeclaration> uncached_matches_;
    mutable CacheStats stats_;
    // Sheet-wide opt-outs, computed once at rule-compile time.
    bool cache_unsafe_sibling_composition_ = false;  // `p + p`, :nth-of-type, ...
    bool cache_unsafe_has_ = false;                  // :has() depends on descendants
    StateReach hover_reach_;                         // what :hover can match
    StateReach active_reach_;                        // what :active can match
    bool shape_key_folds_sibling_index_ = false;     // :nth-child, :first-child, :empty
    // Pseudo-element rules never match a real element, so they live in their
    // own buckets keyed by pseudo name rather than being scanned and rejected
    // once per element.
    std::map<std::string, std::vector<CompiledRule>> pseudo_rules_;
    MediaContext media_;
    AtPropertyRegistry property_registry_;
    // Ids whose declaration was invalid at computed-value time in the current
    // compute() call, so the inherit/initial pass knows to refill them.
    mutable std::vector<int> dropped_;
    // Per-property winning cascade key, used only by the logical-property
    // mapping. Sized once and reused across elements; `cascade_generation_`
    // distinguishes this call's entries from the previous element's.
    mutable std::vector<CascadeKey> winner_keys_;
    mutable uint64_t cascade_generation_ = 0;
};

} // namespace weva
