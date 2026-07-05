using System.Collections.Generic;
using Weva.Compiled;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout;
using Weva.Reactive;

namespace Weva.Css.Cascade {
    // Per-element-state-digest path. Co-located here (not in CascadeEngine.cs) so
    // sister tasks editing CascadeEngine.cs (CssLength pooling, ComputeAll dict
    // reuse, PaintList pooling) don't merge-conflict.
    //
    // Why a per-element digest? Before this file existed, the cache key embedded
    // the IElementStateProvider's *global* Version. A `:hover` flip on one button
    // bumped state.Version, which made every element's cached IncrementalCacheKey
    // mismatch and forced ComputeAll to re-resolve the entire tree. The sole
    // EndToEnd.HoverToggle bench took ~16 ms p50 against a 0.5 ms target.
    //
    // The digest replaces the global stateVersion in the cache key with a
    // per-element value derived from the bits of the element's *own* state that
    // any selector in the compiled stylesheet actually tests
    // (StateSelectorIndex.GlobalStateMask). For an element whose state didn't
    // change in any *relevant* bit, the digest is unchanged and the cache hits
    // even when state.Version (the provider's global version) bumped.
    //
    // Correctness for ancestor combinators: when an ancestor's state changes,
    // the ancestor's digest changes, the ancestor recomputes (every cache miss
    // builds a fresh ComputedStyle with bumped Version), and that bumped
    // ParentStyleVersion in the descendants' cache keys forces them to miss
    // and re-resolve with the new ancestor state visible. Descendant/child
    // combinators (`.parent:hover .child`) ride this path correctly.
    //
    // v1 simplification: sibling combinators with state on the left compound
    // (`.btn:hover + .next`) cannot be served by the per-element digest alone
    // because the right-hand sibling's own state and ancestor states are
    // unchanged. StateSelectorIndex.RequiresGlobalFallback flags this and
    // forces ResolveStateDigest to fold state.Version into the digest, which
    // restores the v0.5 all-invalidate behaviour for those stylesheets.
    public sealed partial class CascadeEngine {
        StateSelectorIndex stateIndex;

        StateSelectorIndex EnsureStateIndex() {
            if (stateIndex != null) return stateIndex;
            stateIndex = new StateSelectorIndex(compiledSelectors);
            return stateIndex;
        }

        // Computes the state-digest component of the per-element cache key. Returns
        // a long that varies if and only if either:
        //   (a) the element's own state intersected with bits any selector tests
        //       changed since the last cache write, OR
        //   (b) state.Version changed AND a selector in the sheet uses a sibling
        //       combinator gated on a stateful pseudo-class (RequiresGlobalFallback).
        // The provider id is folded in by the caller via IncrementalCacheKey, not
        // by this method.
        long ResolveStateDigest(Element element, IElementStateProvider state) {
            var idx = EnsureStateIndex();
            if (idx.RequiresGlobalFallback && GlobalFallbackSubjectCouldMatch(element)) {
                // A sibling combinator with state on the left compound is in the
                // sheet (e.g. `.a:hover + .b`) AND this element could be the
                // SUBJECT (`.b`) of such a selector. The per-element digest
                // can't detect "my left sibling's state changed", so fold the
                // provider's monotone Version into the slot — any state flip
                // re-cascades this element. Multiplied by a prime so it doesn't
                // collide with state-bit hashes for sheets that ALSO have
                // non-fallback selectors.
                //
                // C5: this used to fire for EVERY element, turning every
                // hover/focus/active flip anywhere into a whole-document
                // re-cascade (~16ms cliff). An element that provably can't
                // match any sibling-state selector's subject is unaffected by
                // sibling state, so it falls through to the cheap per-element
                // digest below instead. The narrowing is sound:
                // SiblingStateSubjectCouldMatch only returns false on a
                // concrete structural mismatch (over-approximates everything
                // else), so we never drop a real target.
                ElementState ownF = state.GetState(element);
                int relevantF = (int)(ownF & idx.GlobalStateMask);
                return state.Version * 1099511628211L + relevantF;
            }
            var mask = idx.GlobalStateMask;
            if (mask == ElementState.None) {
                // No selector in the sheet tests any state-driven pseudo-class. The
                // digest is constant 0, so two compute passes with different
                // state.Version values still cache-hit each other. Common case for
                // stylesheets with zero pseudo-class rules.
                return 0;
            }
            ElementState own = state.GetState(element);
            ElementState relevant = own & mask;
            if (relevant == ElementState.None) return 0;
            // Per-element refinement: filter the global mask down to bits whose
            // subject selectors could actually match THIS element. Section /
            // body / html in a click :active chain carry the Active bit but
            // no rule says `<tag>:active` for them — folding Active into
            // their digest would shift their cache key and force a re-cascade
            // that produces identical content. ResolveElementSubjectStateMask
            // caches the per-element mask keyed on element.Version + the
            // feature-set rebuild ordinal so class/attribute mutations
            // invalidate it.
            relevant &= ResolveElementSubjectStateMask(element, mask);
            return (long)((int)relevant);
        }

        // Per-element cache mapping Element → its filtered subject-state mask.
        // Entry is valid as long as elementVersionStamp matches the element's
        // current Version. A mutation that adds/removes a class shifts the
        // version so the next call recomputes. featureSetStamp guards
        // stylesheet swaps — if InvalidateAll wipes the engine the cache
        // entries become stale even though Element.Version didn't change.
        readonly Dictionary<Element, ElementSubjectMaskEntry> elementSubjectMaskCache = new();
        long featureSetStamp;
        struct ElementSubjectMaskEntry {
            public ElementState Mask;
            public long ElementVersion;
            public long FeatureSetStamp;
        }

        ElementState ResolveElementSubjectStateMask(Element element, ElementState globalMask) {
            if (elementSubjectMaskCache.TryGetValue(element, out var entry)
                && entry.ElementVersion == element.Version
                && entry.FeatureSetStamp == featureSetStamp) {
                return entry.Mask;
            }
            var rfs = EnsureRuleFeatureSet();
            // Only the bits in globalMask are candidates — bits absent from
            // every selector trivially can't affect this element either.
            ElementState perElement = ElementState.None;
            ElementState bits = globalMask & rfs.SubjectStateMask;
            // De Bruijn / linear bit-scan loop — System.Numerics.BitOperations
            // would be cleaner but it's inaccessible under Unity's .NET
            // Standard 2.1 surface (CS0122). The candidate bit count is
            // bounded by ElementState's <16 defined bits, so iterating up
            // to 16 positions per call is cheap.
            for (int b = 0; b < 16; b++) {
                var bit = (ElementState)(1 << b);
                if ((bits & bit) == 0) continue;
                if (SubjectMatchAffectedByStateBit(element, bit)) perElement |= bit;
            }
            // PERF-2: include `DescendantStateMask` unconditionally. Selectors
            // like `div:hover span { ... }` carry the state pseudo on the
            // LEFT (non-subject) compound of a descendant combinator. The
            // ancestor's recompute is what bumps `ComputedStyle.Version`,
            // which is the signal descendants' cache keys use to invalidate
            // and re-cascade with the new ancestor state visible. If we
            // filtered down to subject-state bits only, the ancestor's
            // digest wouldn't shift on the hover toggle, the ancestor would
            // cache-hit, its Version would stay frozen, and the descendant
            // would never see the new style. The over-approximation is
            // bounded by the stylesheet's actual non-subject state bits
            // (typically a small subset) — at most one extra cache miss per
            // element per state toggle when the descendant rule doesn't
            // actually target a descendant of THIS element. Sibling-on-left
            // combinators are handled by `RequiresGlobalFallback` upstream.
            perElement |= globalMask & rfs.DescendantStateMask;
            entry = new ElementSubjectMaskEntry {
                Mask = perElement,
                ElementVersion = element.Version,
                FeatureSetStamp = featureSetStamp,
            };
            elementSubjectMaskCache[element] = entry;
            return perElement;
        }

        // Bump the feature-set stamp so cached per-element masks invalidate.
        // Called by InvalidateAll and any path that rebuilds compiled
        // selectors. Cheap monotonic counter, no scan.
        void BumpFeatureSetStamp() {
            featureSetStamp++;
            ruleFeatureSet = null;
            featureSelectors = null;
            stateIndex = null;
        }

        // Public-but-internal helper for tests: returns the bits any selector
        // currently in the engine actually tests. Empty stylesheet -> None.
        public ElementState GlobalStateMask {
            get {
                if (compiledSelectors.Count == 0) return ElementState.None;
                return EnsureStateIndex().GlobalStateMask;
            }
        }

        public bool StateRequiresGlobalFallback {
            get {
                if (compiledSelectors.Count == 0) return false;
                return EnsureStateIndex().RequiresGlobalFallback;
            }
        }

        // Per-feature invalidation index — modeled on Blink's RuleFeatureSet.
        // Buckets each (feature, position) into Subject / Descendant / Sibling
        // so callers can ask "does a flip of feature F on element E need to
        // invalidate anything?" without re-cascading first. Built lazily on
        // first read, same lifetime as `stateIndex`. Public accessor lets
        // outside helpers (InteractionStateProvider via AttachCascade) skip
        // marking elements whose own match couldn't possibly shift on the
        // pseudo-state flip.
        RuleFeatureSet ruleFeatureSet;

        internal RuleFeatureSet RuleFeatures {
            get {
                if (compiledSelectors.Count == 0 && pseudoElementSelectors.Count == 0) return null;
                return EnsureRuleFeatureSet();
            }
        }

        // The selector list the RuleFeatureSet was built over — normal
        // compiled selectors followed by pseudo-element rule selectors
        // (CX5). Feature-set indices resolve against THIS list, not
        // `compiledSelectors`. Aliased to `compiledSelectors` when there
        // are no pseudo-element rules (the common case, no copy).
        List<CompiledSelector> featureSelectors;

        RuleFeatureSet EnsureRuleFeatureSet() {
            if (ruleFeatureSet != null) return ruleFeatureSet;
            if (pseudoElementSelectors.Count == 0) {
                featureSelectors = compiledSelectors;
            } else {
                var combined = new List<CompiledSelector>(compiledSelectors.Count + pseudoElementSelectors.Count);
                combined.AddRange(compiledSelectors);
                combined.AddRange(pseudoElementSelectors);
                featureSelectors = combined;
            }
            ruleFeatureSet = new RuleFeatureSet(featureSelectors);
            return ruleFeatureSet;
        }

        // Returns true iff some selector in the stylesheet has a subject
        // compound that (a) tests the named state bit, AND (b) would
        // match the non-state features on `e`. Used by the state provider
        // to drop tracker marks for chain elements that no selector
        // actually targets on the flipping bit — e.g. an `:active` chain
        // walks card → ul → section → body → html, but no rule says
        // `body:active` or `html:active`, so those ancestors don't need
        // a re-cascade.
        //
        // The match is intentionally restricted to the SUBJECT compound
        // here. Descendant/sibling feature buckets (`.parent:hover .child`,
        // `.a:hover + .b`) are handled by StateBitAffectsElement below —
        // the method the state provider actually calls (audit CX1). This
        // subject-only helper stays as the tight inner check and keeps its
        // original contract.
        //
        // Returns true (i.e. "mark dirty") when:
        //   - The stylesheet has no compiled selectors (no info to gate on
        //     — safe default).
        //   - The state bit appears in a subject compound (SubjectStateMask
        //     intersects bit) AND at least one selector's non-state subject
        //     features match e.
        //
        // Returns false (skip the mark) only when we can prove no subject
        // selector targeting `bit` could match `e`. False-positives (returning
        // true unnecessarily) cost a cache miss; false-negatives (returning
        // false when a re-cascade was actually needed) silently keep stale
        // styles. The over-approximation in the "structural pseudo in subject"
        // branch deliberately errs toward true.
        // C5: per-element cache of "could this element be the SUBJECT of a
        // selector that forces the coarse global-state fallback?" Keyed on
        // element.Version + featureSetStamp exactly like elementSubjectMaskCache
        // (a class/attr mutation bumps Version; a stylesheet swap bumps stamp).
        struct GlobalFallbackSubjectEntry {
            public bool Result;
            public long ElementVersion;
            public long FeatureSetStamp;
        }
        readonly Dictionary<Element, GlobalFallbackSubjectEntry> globalFallbackSubjectCache = new();

        // True iff `element` could match the SUBJECT (rightmost compound) of
        // some selector that tripped RequiresGlobalFallback — i.e. a
        // sibling-state selector (`.a:hover + .b`), a stateful `:has(...)`
        // (`.card:has(:hover)`), or a stateful `:nth-of` filter. Those are the
        // selectors whose subject can change match from a state flip the
        // per-element digest can't see, so a matching element must keep the
        // global-version fold.
        //
        // SOUND for the narrowing: SubjectCompoundCouldMatch returns false ONLY
        // on a concrete structural mismatch of the subject (tag / id / class /
        // has-attr); every pseudo / `:is` / `:not` / unknown form
        // over-approximates to "could match". So a real target is never
        // dropped. Conservative on null / no selectors.
        public bool GlobalFallbackSubjectCouldMatch(Element element) {
            if (element == null) return true;
            if (compiledSelectors.Count == 0) return false;
            if (globalFallbackSubjectCache.TryGetValue(element, out var entry)
                && entry.ElementVersion == element.Version
                && entry.FeatureSetStamp == featureSetStamp) {
                return entry.Result;
            }
            var idx = EnsureStateIndex();
            var fallbackSelectors = idx.GlobalFallbackSelectors;
            bool result = false;
            for (int i = 0; i < fallbackSelectors.Count; i++) {
                var sel = compiledSelectors[fallbackSelectors[i]];
                // bit = None: skip no pseudo — every state pseudo in the subject
                // over-approximates to "match" anyway, which keeps us sound.
                if (SubjectCompoundCouldMatch(sel.Sequence, element, ElementState.None)) {
                    result = true;
                    break;
                }
            }
            globalFallbackSubjectCache[element] = new GlobalFallbackSubjectEntry {
                Result = result,
                ElementVersion = element.Version,
                FeatureSetStamp = featureSetStamp,
            };
            return result;
        }

        public bool SubjectMatchAffectedByStateBit(Element e, ElementState bit) {
            if (e == null) return false;
            if (compiledSelectors.Count == 0 && pseudoElementSelectors.Count == 0) return true;
            var rfs = EnsureRuleFeatureSet();
            if ((rfs.SubjectStateMask & bit) == 0) return false;
            var sels = rfs.SubjectSelectorsForState(bit);
            if (sels.Count == 0) return false;
            for (int i = 0; i < sels.Count; i++) {
                var sel = featureSelectors[sels[i]];
                if (SubjectCompoundCouldMatch(sel.Sequence, e, bit)) return true;
            }
            return false;
        }

        // The SetFlag gate (audit CX1). A state flip on `e` must produce a
        // tracker mark when `bit` appears in ANY compound of a selector
        // whose flipping compound could match `e` — not just the subject:
        //
        //   `.parent:hover .child`  — :hover on the LEFT of a descendant
        //     combinator. Marking the flipping element is sufficient: the
        //     per-element digest folds DescendantStateMask (PERF-2), so the
        //     parent misses, its ComputedStyle.Version bumps, and the
        //     descendant's cache key invalidates through the parent chain.
        //   `.a:hover + .b`         — :hover left of a sibling combinator.
        //     The mark triggers a cascade pass; RequiresGlobalFallback (C5)
        //     forces the full walk that re-resolves the sibling.
        //
        // Pre-CX1 this consulted only the subject bucket, so a sheet where
        // the pseudo lives exclusively in non-subject compounds produced
        // ZERO marks — no cascade pass ever ran and the styles were
        // permanently dead. The per-bucket compound matching below keeps
        // the gate tight for its original purpose: an :active/:hover chain
        // flip on body/html stays unmarked unless some rule's state-
        // carrying compound could actually match body/html.
        public bool StateBitAffectsElement(Element e, ElementState bit) {
            if (e == null) return false;
            if (compiledSelectors.Count == 0 && pseudoElementSelectors.Count == 0) return true;
            var rfs = EnsureRuleFeatureSet();
            if ((rfs.SubjectStateMask & bit) != 0 && SubjectMatchAffectedByStateBit(e, bit)) return true;
            if ((rfs.DescendantStateMask & bit) != 0
                && AnyLeftStateCompoundCouldMatch(rfs.DescendantSelectorsForState(bit), e, bit)) return true;
            if ((rfs.SiblingStateMask & bit) != 0
                && AnyLeftStateCompoundCouldMatch(rfs.SiblingSelectorsForState(bit), e, bit)) return true;
            return false;
        }

        // True iff some selector in `sels` has a NON-subject compound that
        // (a) tests `bit` and (b) could match `e` on its non-state features.
        // The flipping element is the one the state-carrying left compound
        // targets, so that's the compound we match against `e`.
        bool AnyLeftStateCompoundCouldMatch(System.Collections.Generic.IReadOnlyList<int> sels, Element e, ElementState bit) {
            for (int i = 0; i < sels.Count; i++) {
                var seq = featureSelectors[sels[i]].Sequence;
                if (seq == null || seq.Compounds.Count < 2) continue;
                for (int c = 0; c < seq.Compounds.Count - 1; c++) {
                    var compound = seq.Compounds[c];
                    if ((Weva.Compiled.RuleFeatureSet.CompoundStateBits(compound) & bit) == 0) continue;
                    if (CompoundCouldMatch(compound, e, bit)) return true;
                }
            }
            return false;
        }

        // Returns true iff the non-state-pseudo parts of the rightmost
        // compound all match `e`. Structural pseudos (:nth-child, :empty,
        // :is(...), :not(...) etc.) are treated as "match" — over-approxi-
        // mates which keeps the gate safe. The state pseudo we're asking
        // about (`bit`) is skipped (its match would flip by definition).
        // Non-state pseudos that don't depend on `bit` are also treated as
        // match (a sibling-state `:focus` pseudo in the same compound would
        // need its own invalidation pass).
        static bool SubjectCompoundCouldMatch(Weva.Css.Selectors.CompoundSequence seq, Element e, ElementState bit) {
            if (seq == null || seq.Compounds.Count == 0) return false;
            var subject = seq.Compounds[seq.Compounds.Count - 1];
            return CompoundCouldMatch(subject, e, bit);
        }

        // Compound-level version of the check above — used for both the
        // subject compound and (CX1) the state-carrying LEFT compounds of
        // descendant/sibling selectors. Returns false ONLY on a concrete
        // structural mismatch (tag / id / class / attr-presence); every
        // pseudo over-approximates to "could match", which keeps the gate
        // sound (never under-invalidates).
        static bool CompoundCouldMatch(Weva.Css.Selectors.CompoundSelector subject, Element e, ElementState bit) {
            if (subject == null) return false;
            for (int p = 0; p < subject.Parts.Count; p++) {
                var part = subject.Parts[p];
                switch (part) {
                    case Weva.Css.Selectors.UniversalSelector _:
                        continue;
                    case Weva.Css.Selectors.TypeSelector ts:
                        if (!string.Equals(e.TagName, ts.TagName, System.StringComparison.OrdinalIgnoreCase)) return false;
                        continue;
                    case Weva.Css.Selectors.IdSelector ids:
                        if (!string.Equals(e.Id, ids.Id, System.StringComparison.Ordinal)) return false;
                        continue;
                    case Weva.Css.Selectors.ClassSelector cs:
                        if (!ElementHasClass(e, cs.ClassName)) return false;
                        continue;
                    case Weva.Css.Selectors.AttributeSelector attr:
                        if (!e.HasAttribute(attr.Name)) return false;
                        // We deliberately don't evaluate the operator/value
                        // — over-approximation: if the element has the
                        // attribute, treat the selector as "might match".
                        continue;
                    case Weva.Css.Selectors.PseudoClassSelector pc:
                        // Skip the bit we're asking about — its match will
                        // flip by definition. Other pseudos (structural,
                        // other state, :is/:not) over-approximate to true.
                        continue;
                }
            }
            return true;
        }

        static bool ElementHasClass(Element e, string className) {
            if (e == null || string.IsNullOrEmpty(className)) return false;
            var classAttr = e.ClassName;
            if (string.IsNullOrEmpty(classAttr)) return false;
            // Avoid the ClassList iterator allocation by scanning the raw
            // attribute string directly. Class names are whitespace-
            // separated (any of space / tab / CR / LF / FF per HTML).
            int len = classAttr.Length;
            int needleLen = className.Length;
            for (int i = 0; i <= len - needleLen; i++) {
                if (i > 0 && !IsClassSeparator(classAttr[i - 1])) continue;
                bool match = true;
                for (int j = 0; j < needleLen; j++) {
                    if (classAttr[i + j] != className[j]) { match = false; break; }
                }
                if (!match) continue;
                int end = i + needleLen;
                if (end == len || IsClassSeparator(classAttr[end])) return true;
            }
            return false;
        }

        static bool IsClassSeparator(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        // Set populated by ComputeOrHit on each cache miss whose previousStyle
        // and freshly-resolved style differ on at least one layout-affecting
        // property. Read by ApplyLayoutInvalidation(tracker) to mark Layout
        // dirty narrowly — only on elements whose computed-style change can
        // actually affect the layout pass, not on the entire document.
        //
        // Why is this in the cascade engine and not the layout engine?
        // The layout engine doesn't see ComputedStyle diffs — it only sees
        // the final styleOf delegate. The cascade is the only stage that
        // observes both old and new computed styles and can identify the
        // specific properties that flipped. v0.6's per-element state digest
        // already gates which elements re-cascade; this set is what those
        // re-cascaded elements produce when they discovered a layout-property
        // delta.
        readonly HashSet<Element> layoutDirtyFromCascade = new();
        // Per-pass set of elements whose `display` crossed the `none ↔ shown`
        // boundary. Drained by ApplyLayoutInvalidation into the supplied
        // tracker as InvalidationKind.Structure so the layout engine knows
        // to rebuild the box subtree (rather than just re-laying out the
        // existing boxes). Separate from layoutDirtyFromCascade because
        // most layout dirties don't need structural rebuild, and structural
        // rebuild is the expensive path we want to gate carefully.
        readonly HashSet<Element> structureDirtyFromCascade = new();

        internal void NoteLayoutDirtyFromCascade(Element element) {
            if (element != null) layoutDirtyFromCascade.Add(element);
        }

        internal void NoteStructureDirtyFromCascade(Element element) {
            if (element != null) structureDirtyFromCascade.Add(element);
        }

        // Compares previousStyle and newStyle on layout-affecting properties.
        // Returns true if at least one differs. Custom properties are skipped:
        // they only affect layout transitively through var() in normal
        // properties, and the cascade re-resolves the consuming property
        // whenever the var() value changes — which surfaces the diff on the
        // consuming property itself.
        internal static bool LayoutAffectingPropertyChanged(ComputedStyle previousStyle, ComputedStyle newStyle) {
            if (previousStyle == null || newStyle == null) return previousStyle != newStyle;
            if (ReferenceEquals(previousStyle, newStyle)) return false;
            // Iterate the union of explicit keys. CssProperties.IsCustomProperty
            // skipped per the comment above. We compare via TryGet to avoid
            // double-touching the dictionary on hits.
            foreach (var kv in newStyle.Enumerate()) {
                if (CssProperties.IsCustomProperty(kv.Key)) continue;
                if (!LayoutAffectingProperties.IsLayoutAffecting(kv.Key)) continue;
                previousStyle.TryGet(kv.Key, out var prev);
                if (!string.Equals(prev, kv.Value)) return true;
            }
            // A property present in previousStyle but absent from newStyle (e.g.
            // dropped by a no-longer-matching rule) also counts as a change. The
            // forward iteration above would miss this case, so do a second
            // sweep on the (typically smaller) layout-affecting subset of the
            // previous style.
            foreach (var kv in previousStyle.Enumerate()) {
                if (CssProperties.IsCustomProperty(kv.Key)) continue;
                if (!LayoutAffectingProperties.IsLayoutAffecting(kv.Key)) continue;
                if (!newStyle.Contains(kv.Key)) return true;
            }
            return false;
        }

        // Cached, sorted list of CssProperties IDs for every name in the
        // LayoutAffectingProperties set. Built lazily on first digest call
        // and reused for the lifetime of the process. Stable order matters
        // — the digest XORs property id into the hash, so a moving ordering
        // would still produce the same digest, but determinism makes the
        // function easier to reason about under future schema changes.
        static int[] layoutAffectingIdsCache;

        static int[] LayoutAffectingIds() {
            if (layoutAffectingIdsCache != null) return layoutAffectingIdsCache;
            var ids = new List<int>(128);
            int count = CssProperties.RegisteredCount;
            for (int id = 0; id < count; id++) {
                string name = CssProperties.GetName(id);
                if (name == null) continue;
                if (LayoutAffectingProperties.IsLayoutAffecting(name)) ids.Add(id);
            }
            ids.Sort();
            layoutAffectingIdsCache = ids.ToArray();
            return layoutAffectingIdsCache;
        }

        // FNV-1a digest of the layout-affecting property values currently set
        // on `style`. Captures both presence ("property is set") and value
        // identity (string.GetHashCode of the raw value). Two calls returning
        // equal digests mean — modulo a ~2^-64 hash collision — that no
        // layout-affecting property differs between the two snapshots.
        //
        // Use case: take a digest before recycling the previous ComputedStyle
        // into the new cascade pass; recompute after; non-equal digests bubble
        // the element into the layout-dirty set. This restores the diff
        // semantics that ReferenceEquals(previousStyle, newStyle) erased when
        // we started reusing the previous style as the recyclable backing.
        internal static ulong ComputeLayoutDigest(ComputedStyle style) {
            if (style == null) return 0UL;
            int[] ids = LayoutAffectingIds();
            ulong h = 14695981039346656037UL; // FNV-1a 64-bit offset basis
            const ulong prime = 1099511628211UL;
            for (int i = 0; i < ids.Length; i++) {
                int id = ids[i];
                if (!style.TryGet(id, out var value)) continue;
                h ^= (uint)id;
                h *= prime;
                if (value != null) {
                    h ^= (ulong)(uint)value.GetHashCode();
                    h *= prime;
                }
            }
            return h;
        }

        // Drains the per-pass layout-dirty set onto the supplied tracker. Each
        // element gets InvalidationKind.Layout marked. Callers invoke this
        // immediately after ComputeAll returns so the layout engine sees a
        // ready-to-consume tracker. The set is cleared after the drain so the
        // next ComputeAll starts from a clean slate.
        public int ApplyLayoutInvalidation(InvalidationTracker tracker) {
            if (tracker == null) {
                int n = layoutDirtyFromCascade.Count + structureDirtyFromCascade.Count;
                layoutDirtyFromCascade.Clear();
                structureDirtyFromCascade.Clear();
                return n;
            }
            int count = 0;
            foreach (var e in layoutDirtyFromCascade) {
                tracker.MarkDirty(e, InvalidationKind.Layout);
                count++;
            }
            layoutDirtyFromCascade.Clear();
            // Structure marks force the layout engine to rebuild the box
            // subtree rooted at this element. Triggered only when the
            // cascade observed `display` crossing the `none ↔ shown`
            // boundary — see ComputeOrHit's post-cascade check. Layout
            // gets stamped too because a structural rebuild always implies
            // a re-layout of the affected subtree.
            foreach (var e in structureDirtyFromCascade) {
                tracker.MarkDirty(e, InvalidationKind.Structure | InvalidationKind.Layout);
                count++;
            }
            structureDirtyFromCascade.Clear();
            return count;
        }

        // Read-only view of the layout-dirty set populated by the most recent
        // ComputeAll. Tests and devtools use this to verify the cascade
        // narrowly identified which elements need re-layout instead of
        // marking the whole document.
        public IReadOnlyCollection<Element> LayoutDirtyElements => layoutDirtyFromCascade;
        public IReadOnlyCollection<Element> StructureDirtyElements => structureDirtyFromCascade;
    }
}
