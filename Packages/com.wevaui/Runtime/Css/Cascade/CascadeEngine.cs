using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Weva.Compiled;
using Weva.Css.Animation;
using Weva.Css.Cascade.Shorthands;
using Weva.Css.Container;
using Weva.Css.Media;
using Weva.Css.Selectors;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;
using Weva.Profiling;
using Weva.Reactive;

namespace Weva.Css.Cascade {
    public sealed partial class CascadeEngine {
        // CSS Properties and Values API Level 1 — per-engine registry for
        // `@property` typed custom-property descriptors. Populated during
        // CompileSheet; consulted in ComputeFor for inheritance + initial-value.
        readonly AtPropertyRegistry propertyRegistry = new AtPropertyRegistry();

        // Expose registry for tests.
        internal AtPropertyRegistry PropertyRegistry => propertyRegistry;

        readonly List<CompiledRule> compiled = new();
        readonly List<CompiledSelector> compiledSelectors = new();
        // Selectors of every pseudo-element rule (::before/::after/::selection/…).
        // These rules live in side buckets and never enter `compiledSelectors`,
        // which made their state features invisible to the invalidation gate:
        // a sheet where `:hover` appears only as `.btn:hover::after` produced
        // ZERO tracker marks on hover (audit CX5). EnsureRuleFeatureSet folds
        // this list in after the normal selectors so the RuleFeatureSet masks/
        // buckets (and therefore StateBitAffectsElement + the state digests)
        // see them; feature-set indices resolve against `featureSelectors`.
        readonly List<CompiledSelector> pseudoElementSelectors = new();
        // Pseudo-element rules with PseudoElement == "backdrop". Held in a separate
        // bucket because they don't match any DOM element through the standard
        // `Compute(element)` path; they're consulted only by `ComputeBackdrop(host)`
        // when BoxBuilder synthesizes a backdrop box for a top-layer host (open
        // modal dialog or open popover).
        readonly List<CompiledRule> backdropRules = new();
        // Pseudo-element rules with PseudoElement == "before" / "after". Routed
        // into separate buckets and consulted by ComputeBefore(host) /
        // ComputeAfter(host) when BoxBuilder synthesizes the corresponding
        // anonymous pseudo-element child box. A box is generated only when the
        // resolved `content` is non-default ("normal" / "none" → no box).
        readonly List<CompiledRule> beforeRules = new();
        readonly List<CompiledRule> afterRules = new();
        // ::placeholder / ::selection rules. Held in their own buckets and
        // consulted by ComputePlaceholder(host) / ComputeSelection(host).
        // ::placeholder lets authors style the placeholder text on form
        // controls; InputRenderer.DrawTextOverlay reads `color`/`opacity`
        // from the resolved placeholder style. ::selection lets authors
        // style the highlight rect and selected-text color; InputRenderer
        // reads `background-color` and `color` from the resolved selection
        // style and falls back to UA defaults when no rule matches.
        readonly List<CompiledRule> placeholderRules = new();
        readonly List<CompiledRule> selectionRules = new();
        // ::marker rules style the generated list-item marker box. Layout asks
        // ComputeMarker(hostLi) while injecting the anonymous marker child.
        readonly List<CompiledRule> markerRules = new();
        // WebKit scrollbar pseudo-element rule buckets. These are non-standard
        // but widely authored (Chrome shipped them first; most game CSS uses them).
        // When ANY ::-webkit-scrollbar rules match an element the webkit styles take
        // precedence over CSS Scrollbars L1 (scrollbar-color / scrollbar-width) for
        // that element — mirroring Chrome behaviour. The three active buckets are:
        //   ::-webkit-scrollbar          — controls track thickness (width/height)
        //   ::-webkit-scrollbar-thumb    — thumb background-color + border-radius
        //   ::-webkit-scrollbar-track    — track background-color
        // ::-webkit-scrollbar-corner routes to a dedicated bucket so authored
        // `background-color` paints the corner square when both scrollbar axes
        // are visible. -button / -resizer still parse-and-ignore (no paint consumer).
        readonly List<CompiledRule> webkitScrollbarRules = new();
        readonly List<CompiledRule> webkitScrollbarThumbRules = new();
        readonly List<CompiledRule> webkitScrollbarTrackRules = new();
        readonly List<CompiledRule> webkitScrollbarCornerRules = new();
        readonly Dictionary<string, MediaQueryList> mediaCache = new();
        readonly List<MediaQueryList> mediaDependencies = new();
        readonly Dictionary<string, ContainerQueryList> containerCache = new();
        readonly Dictionary<Element, StyleCacheEntry> cache = new();
        // Per-(element, rule-index) cache for resolved ContainerContext. Walking the box
        // tree per rule per element is O(rules × depth); we cache the answer keyed on
        // the element so repeated cascade passes within a frame don't re-walk. The
        // dictionary is keyed by element + rule slot index; the ContainerContext value
        // captures the resolved size at the time of resolution.
        readonly Dictionary<Element, Dictionary<int, ContainerContext>> containerResolutionCache = new();
        // PI5: pool for the inner Dictionary<int, ContainerContext> instances.
        // Without the pool, every cascade pass that re-cascades an element with
        // container-query rules allocates a fresh inner dict on first hit (and
        // the previous one becomes garbage when containerResolutionCache.Clear
        // fires in Apply). Pooling cleared instances drops steady-state alloc
        // to zero. Bound is informational — the pool can grow to the high-water
        // mark of distinct elements ever using container queries within one
        // outer-dict lifetime.
        readonly Stack<Dictionary<int, ContainerContext>> containerInnerDictPool = new();
        // PI5: diagnostic counters — bumped by ContainerMatches on pool pop
        // vs. fresh allocation. Tests assert that steady-state cascade re-runs
        // bump only `containerInnerDictPoolHits` and never the alloc counter.
        long containerInnerDictAllocCount;
        long containerInnerDictPoolHits;
        internal long ContainerInnerDictAllocCountForTests => containerInnerDictAllocCount;
        internal long ContainerInnerDictPoolHitsForTests => containerInnerDictPoolHits;
        internal int ContainerInnerDictPoolSizeForTests => containerInnerDictPool.Count;
        Func<Element, Box> elementToBoxLookup;
        // Per-engine reusable scratch + snapshot pass. Held as fields so each
        // ComputeAll/Compute call reuses the underlying buckets/lists rather than
        // allocating a fresh Dictionary<Element,int> + List<MatchedDeclaration> per
        // element. See CascadePools.cs for lifetime / reset semantics.
        readonly SnapshotPassState snapshotPass = new();
        readonly CascadeScratch scratch = new();
        readonly bool useSnapshot;
        readonly SymbolTable symbols;
        SelectorIndex selectorIndex;
        MediaContext mediaContext;
        long mediaContextVersion;
        long mediaDependencyFingerprint;
        long cacheHits;
        long cacheMisses;
        long indexBuildCount;
        long snapshotBuildCount;
        bool hasScopePseudo;
        bool hasMediaDependentRules;
        // CX2: shape-cache position handling, set at compile time per selector.
        // Index-positional pseudos (:nth-child/:first/:last/:only-child/:empty)
        // fold (sibling index, sibling count, own child count) into the shape
        // key; of-type pseudos and sibling combinators depend on preceding-
        // sibling COMPOSITION, which the key cannot represent — sharing is
        // disabled for such sheets. Pre-CX2 neither was handled and
        // `li:nth-child(odd)` served the first sibling's match set to ALL
        // identical siblings (zebra striping painted every row).
        bool shapeKeyFoldsSiblingIndex;
        bool shapeCacheUnsafeForSiblingComposition;
        // Latest DomSnapshot built by ComputeAll. Held past Deactivate() so downstream
        // consumers (LayoutEngine via LayoutContext.Snapshot) can reuse the snapshot
        // without rebuilding it. Reset to null on InvalidateAll. A subsequent ComputeAll
        // overwrites this with the freshly-built snapshot.
        DomSnapshot lastSnapshot;
        // CssAnimationRunner integration: when attached, every cache miss in ComputeOrHit
        // notifies the runner so transitions/animations can react to style diffs and
        // GetComposedStyle layers running animations atop the cascade output.
        CssAnimationRunner animationRunner;

        // Default surface is intentionally larger than any reasonable @media threshold
        // so historical CascadeEngineTests authored before the evaluator existed (which
        // assume "@media always applies") continue to pass. Callers configure their
        // real surface via the (stylesheets, MediaContext) overload or MediaContext setter.
        public CascadeEngine(IEnumerable<OriginatedStylesheet> stylesheets)
            : this(stylesheets, MediaContext.Default(10000, 10000), true) { }

        public CascadeEngine(IEnumerable<OriginatedStylesheet> stylesheets, MediaContext mediaContext)
            : this(stylesheets, mediaContext, true) { }

        public CascadeEngine(IEnumerable<OriginatedStylesheet> stylesheets, bool useSnapshot)
            : this(stylesheets, MediaContext.Default(10000, 10000), useSnapshot) { }

        public CascadeEngine(IEnumerable<OriginatedStylesheet> stylesheets, MediaContext mediaContext, bool useSnapshot) {
            this.mediaContext = mediaContext;
            this.mediaContextVersion = 1;
            this.useSnapshot = useSnapshot;
            this.symbols = useSnapshot ? new SymbolTable() : null;
            if (stylesheets == null) return;
            int sourceIndex = 0;
            foreach (var os in stylesheets) {
                if (os == null || os.Stylesheet == null) continue;
                CompileSheet(os.Stylesheet, os.Origin, ref sourceIndex);
            }
            mediaDependencyFingerprint = ComputeMediaDependencyFingerprint(this.mediaContext);
        }

        public bool UseSnapshot => useSnapshot;
        public long IndexBuildCount => indexBuildCount;
        public long SnapshotBuildCount => snapshotBuildCount;
        public bool HasMediaDependentRules => hasMediaDependentRules;

        // Latest DomSnapshot built by ComputeAll. Null until the first ComputeAll
        // runs, or if useSnapshot is disabled, or after InvalidateAll. Layout's
        // snapshot path consumes this via LayoutContext.Snapshot so the layout
        // builder reuses the cascade's freshly-built struct-of-arrays view of the
        // DOM instead of paying for a second walk.
        internal DomSnapshot LastSnapshot => lastSnapshot;

        public MediaContext MediaContext {
            get => mediaContext;
            set {
                SetMediaContext(value, true);
            }
        }

        public long MediaContextVersion => mediaContextVersion;

        public void SetMediaContext(MediaContext value, bool bumpVersion) {
            mediaContext = value;
            mediaDependencyFingerprint = ComputeMediaDependencyFingerprint(value);
            if (bumpVersion) {
                mediaContextVersion++;
                // shapeCache stores matched declarations keyed by element shape +
                // ancestor context. The match set depends on active media rules —
                // when mediaContextVersion bumps, previously-cached matches may
                // be stale (a rule gated on @media that just became active/inactive
                // would still serve the old match list). Clear to force fresh
                // CollectMatches on the next cascade pass.
                shapeCache.Clear();
                matchedPropsCache.Clear();
            }
        }

        // Force a cache-version bump so the next ComputeFor for every element
        // misses and re-runs through the resolver chain (VariableResolver +
        // EnvResolver). Used when env() values change without a media context
        // shift — e.g. Screen.safeArea changes from device rotation while the
        // viewport keeps the same dimensions.
        public void BumpEnvironmentVersion() {
            mediaContextVersion++;
            // env() results can differ per-element — purge the shapeCache so
            // each element re-runs CollectMatches with the fresh env() values.
            shapeCache.Clear();
            matchedPropsCache.Clear();
        }

        public bool SetMediaContextForViewportResize(MediaContext value) {
            mediaContext = value;
            if (!hasMediaDependentRules) return false;
            long next = ComputeMediaDependencyFingerprint(value);
            if (next == mediaDependencyFingerprint) return false;
            mediaDependencyFingerprint = next;
            mediaContextVersion++;
            // Same invalidation as SetMediaContext (audit CX4): media filtering
            // happens BEFORE match lists are cached, so a fingerprint change
            // must purge both — otherwise every element cache-misses on the
            // bumped version but is served the stale match set by shape key,
            // and @media styles freeze at the pre-resize breakpoint.
            shapeCache.Clear();
            matchedPropsCache.Clear();
            return true;
        }

        // Hook used to resolve a Box from an Element when evaluating @container rules.
        // Caller (UIDocumentLifecycle) wires this to ElementToBoxIndex.Lookup once layout
        // has produced its first box tree; until then it stays null and @container rules
        // never match (any descendant has no resolvable container). This is the v1
        // chicken-and-egg simplification: container queries see the LAYOUT-AFTER-PREVIOUS-
        // CASCADE size, so changing a container query may take 1-2 frames to settle.
        public Func<Element, Box> ElementToBoxLookup {
            get => elementToBoxLookup;
            set {
                elementToBoxLookup = value;
                ClearContainerResolutionCacheReturningInnersToPool();
            }
        }

        public void InvalidateContainerResolutions() {
            ClearContainerResolutionCacheReturningInnersToPool();
        }

        // PI5: drop every outer entry but recycle the cleared inner dicts back
        // to the pool so the next cascade re-cascade can pop one instead of
        // `new`-ing. Inner.Clear() is O(entries); the entries-per-element count
        // is bounded by container-query-rule count, typically small.
        void ClearContainerResolutionCacheReturningInnersToPool() {
            if (containerResolutionCache.Count == 0) return;
            foreach (var kv in containerResolutionCache) {
                var inner = kv.Value;
                if (inner == null) continue;
                inner.Clear();
                containerInnerDictPool.Push(inner);
            }
            containerResolutionCache.Clear();
        }

        public int CacheSize => cache.Count;
        public long CacheHits => cacheHits;
        public long CacheMisses => cacheMisses;

        // CSS Cascade trace hook for DevTools inspection (W7 phase 1).
        // When StyleInspector.CaptureCascadeTrace is true, CollectMatchesFor
        // runs a fresh managed-path selector match for the given element and
        // returns every MatchedDeclaration that would participate in its cascade
        // (pre-sort, pre-shorthand-expansion — i.e. the raw matched set from
        // authored rules, excluding the shape-cache and matched-props-cache
        // short-circuits that the production path uses).
        //
        // When CaptureCascadeTrace is false (the default) this method returns
        // an empty list without doing any work; the flag is the only gate —
        // check it before calling. The flag is owned by StyleInspector so the
        // DevTools overlay can flip it on without touching CascadeEngine's API.
        //
        // Cost when flag is OFF: one static field read (≈ 0 ns) per call-site
        // guard that the overlay inserts around Dump(); the method itself is
        // never entered. Cost when flag is ON: identical to one extra cascade
        // pass for the picked element (negligible at inspector frame rate).
        public List<MatchedDeclaration> CollectMatchesFor(Element element, IElementStateProvider stateProvider = null) {
            var result = new List<MatchedDeclaration>(16);
            if (element == null) return result;
            var state = stateProvider ?? NullStateProvider.Instance;
            CollectMatchesManaged(element, state, result);
            AddInlineDeclarations(element, result);
            return result;
        }

        internal ComputedStyle GetCachedStyle(Element element) {
            if (element == null) return null;
            return cache.TryGetValue(element, out var entry) ? entry.Style : null;
        }

        public void ResetCacheStats() {
            cacheHits = 0;
            cacheMisses = 0;
        }

        public void Invalidate(Element e) {
            if (e == null) return;
            cache.Remove(e);
            resultMap.Remove(e);
        }

        public void InvalidateSubtree(Element root) {
            if (root == null) return;
            cache.Remove(root);
            resultMap.Remove(root);
            foreach (var c in root.Children) {
                if (c is Element ce) InvalidateSubtree(ce);
            }
        }

        public void InvalidateAll() {
            // PERF-1: before clearing the cache, drain its ComputedStyle values
            // into the pool so the next ComputeAll can recycle their backing
            // arrays instead of allocating fresh ones. This converts the
            // InvalidateAll+ComputeAll pattern (used by every warm benchmark
            // pass) from O(N) new ComputedStyle allocs back to O(0) allocs
            // for the style backing — the dominant allocator at ~1.7 KB/element.
            // Safety: same-element recycling is sound here (RebindForReuse
            // calls Reset() which clears all values). Cross-element reuse is
            // also safe on a MISS path because ComputeFor overwrites every slot
            // before the style is returned. The animation-runner diff path is
            // guarded by the animationRunner check in ComputeOrHit so it still
            // gets a fresh instance when needed.
            foreach (var kv in cache) {
                var style = kv.Value.Style;
                if (style != null && animationRunner == null) {
                    computedStylePool.Push(style);
                }
            }
            cache.Clear();
            lastSnapshot = null;
            ResetStyleArrayState();
            BumpFeatureSetStamp();
            matchedPropsCache.Clear();
            shapeCache.Clear();
        }

        // Reads the InvalidationTracker's per-stage dirty sets and drops cache entries
        // for any element marked Style or Structure. Does not Clear the tracker —
        // callers manage tracker lifecycle themselves. Layout invalidations also force
        // a fresh container-resolution sweep next pass (an ancestor's box size change
        // can flip whether a descendant's @container rule applies).
        public void Apply(InvalidationTracker tracker) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.CascadeIncrementalApply)) {
                if (tracker == null) return;
                var kind = InvalidationKind.Style | InvalidationKind.Structure;
                applyDropScratch.Clear();
                // DirtyEntries returns the Dictionary directly — foreach
                // uses the struct enumerator, no yield-return state-machine
                // alloc per call. Apply fires every Update().
                foreach (var kv in tracker.DirtyEntries) {
                    if ((kv.Value & kind) == 0) continue;
                    if (kv.Key is Element e) applyDropScratch.Add(e);
                }
                for (int i = 0; i < applyDropScratch.Count; i++) {
                    var e = applyDropScratch[i];
                    cache.Remove(e);
                    resultMap.Remove(e);
                }
                applyDropScratch.Clear();
                if (tracker.HasAny(InvalidationKind.Layout | InvalidationKind.Structure)) {
                    ClearContainerResolutionCacheReturningInnersToPool();
                }
            }
        }

        void CompileSheet(Stylesheet sheet, DeclarationOrigin origin, ref int sourceIndex) {
            foreach (var rule in sheet.Rules) {
                CompileRule(rule, origin, ref sourceIndex);
            }
        }

        void CompileRule(Rule rule, DeclarationOrigin origin, ref int sourceIndex) {
            CompileRuleNested(rule, origin, ref sourceIndex, null, null, null, CssLayer.UnlayeredOrdinal, null);
        }

        void CompileRuleNested(Rule rule, DeclarationOrigin origin, ref int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal) {
            CompileRuleNested(rule, origin, ref sourceIndex, media, container, containerName, layerOrdinal, null);
        }

        void CompileRuleNested(Rule rule, DeclarationOrigin origin, ref int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal, ScopeContext scope) {
            // Forward to the chain-aware overload. Single container entry and
            // single scope entry are the common non-nested cases.
            ContainerChainEntry[] chain = container != null
                ? new[] { new ContainerChainEntry(container, containerName) }
                : null;
            ScopeContext[] scopeChain = scope != null ? new[] { scope } : null;
            CompileRuleNestedChain(rule, origin, ref sourceIndex, media, chain, scopeChain, layerOrdinal);
        }

        // Chain-aware inner compiler. `containerChain` is ordered outermost-first;
        // when a new @container is entered we prepend its entry to produce a new
        // array where [0] is the outermost condition. `scopeChain` is likewise
        // outermost-first.
        void CompileRuleNestedChain(Rule rule, DeclarationOrigin origin, ref int sourceIndex,
                                    MediaQueryList media, ContainerChainEntry[] containerChain,
                                    ScopeContext[] scopeChain, int layerOrdinal) {
            switch (rule) {
                case StyleRule sr:
                    CompileStyleRuleChain(sr, origin, ref sourceIndex, media, containerChain, scopeChain, layerOrdinal);
                    break;
                case LayerRule lr:
                    if (!lr.IsBlock) {
                        if (!string.IsNullOrEmpty(currentLayerName) && lr.Names != null) {
                            var prefixed = new List<string>(lr.Names.Count);
                            for (int i = 0; i < lr.Names.Count; i++) {
                                var n = lr.Names[i];
                                prefixed.Add(string.IsNullOrEmpty(n) ? n : currentLayerName + "." + n);
                            }
                            RegisterLayerOrdering(prefixed);
                        } else {
                            RegisterLayerOrdering(lr.Names);
                        }
                    } else {
                        string layerName = lr.Names.Count > 0 ? lr.Names[0] : null;
                        // Nested-block form joins parent name with `.` per CSS Cascade L5 §6.4.2.
                        string effectiveName = (!string.IsNullOrEmpty(currentLayerName) && !string.IsNullOrEmpty(layerName))
                            ? currentLayerName + "." + layerName
                            : layerName;
                        int innerOrdinal = RegisterLayer(effectiveName);
                        string savedLayerName = currentLayerName;
                        currentLayerName = effectiveName;
                        try {
                            foreach (var inner in lr.Rules) {
                                CompileRuleNestedChain(inner, origin, ref sourceIndex, media, containerChain, scopeChain, innerOrdinal);
                            }
                        } finally {
                            currentLayerName = savedLayerName;
                        }
                    }
                    break;
                case MediaRule mr:
                    hasMediaDependentRules = true;
                    var parsed = GetOrParseMediaQuery(mr.ConditionText);
                    // EC11: a null parsed result means the media query failed to
                    // parse. Per CSS Cascade L4 the rule is silently dropped (the
                    // warning was already emitted by GetOrParseMediaQuery). Do NOT
                    // call CombineMedia with null — that would fall through to the
                    // outer media condition and accidentally apply the block.
                    if (parsed == null) break;
                    var combinedMedia = CombineMedia(media, parsed);
                    foreach (var inner in mr.Rules) {
                        CompileRuleNestedChain(inner, origin, ref sourceIndex, combinedMedia, containerChain, scopeChain, layerOrdinal);
                    }
                    break;
                case SupportsRule sup:
                    if (SupportsEvaluator.Evaluate(sup.ConditionText)) {
                        foreach (var inner in sup.Rules) {
                            CompileRuleNestedChain(inner, origin, ref sourceIndex, media, containerChain, scopeChain, layerOrdinal);
                        }
                    }
                    break;
                case ScopeRule sc: {
                    // CSS Cascade L6 §7 — nested @scope rules form a conjunction:
                    // a target must satisfy EVERY enclosing @scope window, not just
                    // the innermost. We accumulate them outermost-first so
                    // CollectMatches can walk from the outermost scope inward.
                    var innerScope = new ScopeContext(sc.ScopeStartSelectors, sc.ScopeEndSelectors);
                    ScopeContext[] newScopeChain = AppendScope(scopeChain, innerScope);
                    foreach (var inner in sc.Rules) {
                        CompileRuleNestedChain(inner, origin, ref sourceIndex, media, containerChain, newScopeChain, layerOrdinal);
                    }
                    break;
                }
                case AtPropertyRule apr:
                    // CSS Properties & Values L1 — register the typed descriptor.
                    // The parser already validated all three required descriptors; any
                    // at-rule that failed validation was dropped (null) before reaching
                    // here. We record it without touching the layer/media context.
                    if (apr != null) {
                        propertyRegistry.Register(new PropertyDescriptor(
                            apr.Name, apr.Syntax, apr.InitialValue, apr.Inherits));
                    }
                    break;
                case ContainerRule cr:
                    // CssParser eagerly extracts the leading bare ident as `cr.Name`. When it
                    // can't (e.g. "not (...)" or whitespace-ambiguous prelude), Name is null
                    // and we re-parse the prelude as a single unit so the runtime parser is
                    // the source of truth for both name and condition.
                    string ruleName = cr.Name;
                    ContainerQueryList parsedC;
                    if (string.IsNullOrEmpty(ruleName)) {
                        var fullParsed = GetOrParseContainerPrelude(cr.ConditionText ?? "");
                        ruleName = fullParsed.Name;
                        parsedC = fullParsed.Condition;
                    } else {
                        parsedC = GetOrParseContainerCondition(cr.ConditionText);
                    }
                    // CSS Containment L3 §3 — nested @container rules form a
                    // conjunction: every condition in the nest must be satisfied by
                    // the appropriate container ancestor in the element tree.
                    // We accumulate the chain outermost-first; ContainerMatches
                    // walks from innermost (last entry) outward, finding the
                    // container for each condition above the previously matched one.
                    ContainerChainEntry[] newChain = PrependContainer(containerChain,
                        new ContainerChainEntry(parsedC, ruleName));
                    foreach (var inner in cr.Rules) {
                        CompileRuleNestedChain(inner, origin, ref sourceIndex, media, newChain, scopeChain, layerOrdinal);
                    }
                    break;
            }
        }

        // Prepend a new container entry at the front (outermost position) of the chain.
        // Result: [existing..., newEntry] where newEntry is the innermost condition.
        // Wait — we want [outermost, ..., innermost]. The outermost was already in the chain.
        // When we enter a new @container, it becomes the new innermost. So we APPEND.
        // ContainerMatches walks from the last (innermost) entry backward.
        static ContainerChainEntry[] PrependContainer(ContainerChainEntry[] existing, ContainerChainEntry newEntry) {
            if (existing == null || existing.Length == 0) {
                return new[] { newEntry };
            }
            // Append newEntry (innermost) to the existing outermost-first chain.
            var result = new ContainerChainEntry[existing.Length + 1];
            for (int i = 0; i < existing.Length; i++) result[i] = existing[i];
            result[existing.Length] = newEntry;
            return result;
        }

        // Append a new scope at the end (innermost position) of the scope chain.
        static ScopeContext[] AppendScope(ScopeContext[] existing, ScopeContext newScope) {
            if (existing == null || existing.Length == 0) {
                return new[] { newScope };
            }
            var result = new ScopeContext[existing.Length + 1];
            for (int i = 0; i < existing.Length; i++) result[i] = existing[i];
            result[existing.Length] = newScope;
            return result;
        }

        static MediaQueryList CombineMedia(MediaQueryList outer, MediaQueryList inner) {
            if (outer == null) return inner;
            if (inner == null) return outer;
            // Conjunction: both must match. Simulated by an MediaAndQuery wrapping the
            // two outer/inner trees. We allocate a fresh MediaQueryList containing one
            // combined query so cascade evaluation stays a single Evaluate() call.
            var combinedItems = new List<MediaQuery>();
            for (int i = 0; i < outer.Items.Count; i++) {
                for (int j = 0; j < inner.Items.Count; j++) {
                    combinedItems.Add(new MediaAndQuery(new List<MediaQuery> { outer.Items[i], inner.Items[j] }));
                }
            }
            return new MediaQueryList(combinedItems);
        }

        MediaQueryList GetOrParseMediaQuery(string text) {
            string key = text ?? "";
            if (mediaCache.TryGetValue(key, out var existing)) return existing;
            MediaQueryList parsed;
            try {
                parsed = MediaQueryParser.Parse(key);
            } catch (MediaQueryParseException ex) {
                // EC11: by-design drop per CSS Cascade L4 ("rules that fail to
                // parse should be ignored"). Warn once per malformed rule so
                // an `@media (foo: bar)` typo is visible without changing
                // behavior. Cached value is null below so the warn-once dedupe
                // is naturally one-per-text.
                WarnCascadeParseFailure("EC11/media", key, ex);
                parsed = null;
            }
            mediaCache[key] = parsed;
            return parsed;
        }

        ContainerQueryList GetOrParseContainerCondition(string text) {
            string key = text ?? "";
            if (containerCache.TryGetValue(key, out var existing)) return existing;
            ContainerQueryList parsed;
            try {
                parsed = ContainerQueryParser.ParseCondition(key);
            } catch (ContainerQueryParseException ex) {
                // EC11: same disposition as the @media branch above. Cached
                // null below; warn-once per text.
                WarnCascadeParseFailure("EC11/container-condition", key, ex);
                parsed = null;
            }
            containerCache[key] = parsed;
            return parsed;
        }

        ContainerQueryParseResult GetOrParseContainerPrelude(string text) {
            try {
                return ContainerQueryParser.Parse(text ?? "");
            } catch (ContainerQueryParseException ex) {
                // EC11: by-design drop of a malformed @container prelude.
                // No cache here — warn-once dedupe is keyed on the prelude
                // text in the helper.
                WarnCascadeParseFailure("EC11/container-prelude", text ?? "", ex);
                return new ContainerQueryParseResult(null, null);
            }
        }

        void CompileStyleRule(StyleRule sr, DeclarationOrigin origin, ref int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal) {
            CompileStyleRule(sr, origin, ref sourceIndex, media, container, containerName, layerOrdinal, null);
        }

        void CompileStyleRule(StyleRule sr, DeclarationOrigin origin, ref int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal, ScopeContext scope) {
            ContainerChainEntry[] chain = container != null
                ? new[] { new ContainerChainEntry(container, containerName) }
                : null;
            ScopeContext[] scopeChain = scope != null ? new[] { scope } : null;
            CompileStyleRuleChain(sr, origin, ref sourceIndex, media, chain, scopeChain, layerOrdinal);
        }

        void CompileStyleRuleChain(StyleRule sr, DeclarationOrigin origin, ref int sourceIndex,
                                   MediaQueryList media, ContainerChainEntry[] containerChain,
                                   ScopeContext[] scopeChain, int layerOrdinal) {
            // Flatten chain back to legacy single-entry for backward compat
            // when there is exactly one container condition (common case).
            ContainerQueryList container = null;
            string containerName = null;
            if (containerChain != null && containerChain.Length > 0) {
                // Pick the innermost (last) entry for the legacy Container/ContainerName
                // fields; the full chain is stored for multi-level evaluation.
                container = containerChain[containerChain.Length - 1].Condition;
                containerName = containerChain[containerChain.Length - 1].Name;
            }
            ScopeContext scope = null;
            if (scopeChain != null && scopeChain.Length > 0) {
                scope = scopeChain[scopeChain.Length - 1];
            }
            RegisterMediaDependency(media);
            foreach (var selectorText in sr.Selectors) {
                CompiledSelector cs;
                try {
                    cs = SelectorParser.Parse(selectorText);
                } catch (SelectorParseException ex) {
                    // EC11: by-design rule drop. SelectorParser already logs
                    // its own warn-then-throw for soft-stubs like `:not(<complex>)`,
                    // but a hard parse error (mismatched brackets, etc.) only
                    // surfaces via this catch; warn-once per offending selector
                    // text so the author sees the dropped rule.
                    WarnCascadeParseFailure("EC11/selector", selectorText ?? "", ex);
                    continue;
                }
                if (HasDetector.ContainsPseudo(cs, PseudoClassKind.Scope)) hasScopePseudo = true;
                if (cs.PseudoElement != null) {
                    // CX5: record the selector for the feature-set build even
                    // though the rule itself routes to a side bucket — state
                    // pseudos in pseudo-element rules must still produce
                    // invalidation marks and digest shifts.
                    pseudoElementSelectors.Add(cs);
                    // ::backdrop / ::before / ::after / ::placeholder /
                    // ::selection / ::marker each have a paint path: route their rules
                    // into their respective bucket consumed by the matching
                    // ComputeFoo(host) entry point.
                    if (cs.PseudoElement == "backdrop") {
                        int bidx = sourceIndex++;
                        backdropRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "before") {
                        int bidx = sourceIndex++;
                        beforeRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "after") {
                        int bidx = sourceIndex++;
                        afterRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "placeholder") {
                        int bidx = sourceIndex++;
                        placeholderRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "selection") {
                        int bidx = sourceIndex++;
                        selectionRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "marker") {
                        int bidx = sourceIndex++;
                        markerRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "-webkit-scrollbar") {
                        int bidx = sourceIndex++;
                        webkitScrollbarRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "-webkit-scrollbar-thumb") {
                        int bidx = sourceIndex++;
                        webkitScrollbarThumbRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "-webkit-scrollbar-track") {
                        int bidx = sourceIndex++;
                        webkitScrollbarTrackRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    } else if (cs.PseudoElement == "-webkit-scrollbar-corner") {
                        // Corner paint: fills the overlap square when both axes scroll.
                        int bidx = sourceIndex++;
                        webkitScrollbarCornerRules.Add(new CompiledRule(cs, sr.Declarations, origin, bidx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                    }
                    // ::-webkit-scrollbar-button / -resizer: parsed, still ignored
                    // (no paint consumer). Rule survives without error so the cascade
                    // doesn't drop surrounding rules.
                    continue;
                }
                int idx = sourceIndex++;
                // CX2: classify position-dependence for the shape cache (see
                // field docs). Only normal rules matter — pseudo-element rule
                // buckets never flow through the shape-keyed match cache.
                if (!shapeKeyFoldsSiblingIndex && HasDetector.ContainsIndexPositionalPseudo(cs))
                    shapeKeyFoldsSiblingIndex = true;
                if (!shapeCacheUnsafeForSiblingComposition && HasDetector.ContainsSiblingCompositionDependence(cs))
                    shapeCacheUnsafeForSiblingComposition = true;
                compiled.Add(new CompiledRule(cs, sr.Declarations, origin, idx, media, container, containerName, layerOrdinal, scope, containerChain, scopeChain));
                compiledSelectors.Add(cs);
            }
        }

        void RegisterMediaDependency(MediaQueryList media) {
            if (media == null) return;
            for (int i = 0; i < mediaDependencies.Count; i++) {
                if (ReferenceEquals(mediaDependencies[i], media)) return;
            }
            mediaDependencies.Add(media);
        }

        long ComputeMediaDependencyFingerprint(MediaContext context) {
            if (mediaDependencies.Count == 0) return 0;
            unchecked {
                long h = 1469598103934665603L;
                for (int i = 0; i < mediaDependencies.Count; i++) {
                    bool matches = mediaDependencies[i]?.Evaluate(context) ?? false;
                    h ^= matches ? 1099511628211L : 1469598103934665603L;
                    h *= 1099511628211L;
                }
                return h;
            }
        }

        SelectorIndex EnsureIndex() {
            if (selectorIndex != null) return selectorIndex;
            indexBuildCount++;
            selectorIndex = new SelectorIndex(symbols, compiledSelectors);
            return selectorIndex;
        }

        public ComputedStyle Compute(Element element, IElementStateProvider stateProvider = null) {
            if (element == null) return null;
            var state = stateProvider ?? NullStateProvider.Instance;
            // Reuse the engine's scratch parent-chain list. Compute is the
            // only caller and it consumes the list synchronously within
            // this method, so no aliasing across calls. The static
            // BuildParentChain allocated a fresh List<Element> + reversed
            // it per call — fired once per dirty element per frame from
            // UIDocumentLifecycle.RefreshPaintOnlyStyles (60 Hz × N tiles
            // on the spinning-gem demo).
            parentChainScratch.Clear();
            BuildParentChainInto(element, parentChainScratch);
            // PERF: reuse the snapshot from the most recent ComputeAll. Without
            // this, CollectMatchesManaged walks every compiled selector for
            // every cascade miss — quadratic on doc-size × paint-dirty-count.
            // CollectMatchesFromSnapshot prunes the candidate set via the
            // SelectorIndex first. The snapshot is valid until snapshotDirty
            // flips (set by DOM mutations); we re-Reset the snapshotPass
            // when the underlying snapshot identity changes so the
            // ElementToNodeId map stays in sync.
            SnapshotPassState pass = null;
            if (useSnapshot && !hasScopePseudo && !snapshotDirty
                && lastSnapshot != null && compiledSelectors.Count > 0) {
                EnsureIndex();
                if (!snapshotPass.Active || !ReferenceEquals(snapshotPass.Snapshot, lastSnapshot)) {
                    snapshotPass.Reset(lastSnapshot, selectorIndex, compiledSelectors);
                }
                pass = snapshotPass;
            }
            ComputedStyle parentStyle = null;
            ComputedStyle current = null;
            for (int i = 0; i < parentChainScratch.Count; i++) {
                current = ComputeOrHit(parentChainScratch[i], parentStyle, state, pass);
                parentStyle = current;
            }
            return current;
        }
        readonly List<Element> parentChainScratch = new List<Element>(16);
        // Free-list of ComputedStyle instances whose backing string[]+bool[]
        // arrays we can reuse across cascade re-computes. Pushed to in
        // ComputeOrHit after OnStyleChange consumes the diff; popped from
        // before allocating in ComputeFor. Single-threaded by contract.
        readonly Stack<ComputedStyle> computedStylePool = new Stack<ComputedStyle>(64);

        // Last mediaContextVersion observed by a successful ComputeAll. The
        // incremental path reads this to decide whether a media context change
        // since the prior pass invalidates the whole tree (which makes
        // dirty-subtree skipping unsafe — every element's cache key would
        // mismatch). Updated at the end of any ComputeAll that successfully
        // walks the tree (full OR incremental).
        long lastObservedMediaContextVersion = -1;

        // Scratch HashSet for incremental walks. Reused across calls; cleared
        // at entry, populated with the ancestor closure of the dirty hint set
        // (each dirty element + all of its ancestors up to the document root).
        // The walk visits only elements present in this set, then for any
        // element whose re-cascade actually changed its ComputedStyle.Version
        // it falls back to the full Walk for that element's descendants
        // (since their cache keys' ParentStyleVersion field just shifted).
        readonly System.Collections.Generic.HashSet<Element> ancestorClosureScratch = new();

        // Per-parent index of in-walkSet children. Built alongside walkSet
        // during the dirty-closure pass. WalkIncremental iterates this
        // small list instead of all of e.Children — for a parent like
        // <section> with 250 children where only one is in the closure,
        // this collapses 250 HashSet.Contains calls (~2.5µs of overhead)
        // down to a single dictionary lookup and 1-element iteration.
        // Lists are pooled to avoid per-pass allocation; Reset is called
        // at the start of each ComputeAllIncremental.
        readonly System.Collections.Generic.Dictionary<Element, System.Collections.Generic.List<Element>> walkSetByParentScratch = new();
        readonly System.Collections.Generic.Stack<System.Collections.Generic.List<Element>> walkSetListPool = new();

        void ResetWalkSetByParent() {
            foreach (var list in walkSetByParentScratch.Values) {
                list.Clear();
                walkSetListPool.Push(list);
            }
            walkSetByParentScratch.Clear();
        }

        void BuildClosureFromHint(Element hint, System.Collections.Generic.HashSet<Element> walkSet) {
            if (hint == null) return;
            for (Element n = hint; n != null; n = n.Parent as Element) {
                if (!walkSet.Add(n)) break;
                if (n.Parent is Element pe) {
                    if (!walkSetByParentScratch.TryGetValue(pe, out var list)) {
                        list = RentWalkSetList();
                        walkSetByParentScratch[pe] = list;
                    }
                    list.Add(n);
                }
            }
        }

        System.Collections.Generic.List<Element> RentWalkSetList() {
            if (walkSetListPool.Count > 0) return walkSetListPool.Pop();
            return new System.Collections.Generic.List<Element>(4);
        }

        public IReadOnlyDictionary<Element, ComputedStyle> ComputeAll(Document doc, IElementStateProvider stateProvider = null) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.CascadeComputeAll)) {
                // Reuse the engine-owned result map and StyleArray rather than
                // allocating fresh per call. Dictionary.Clear is O(n) but keeps
                // the bucket array, so subsequent inserts hit existing storage.
                resultMap.Clear();
                if (doc == null) {
                    styleArray.Clear();
                    return resultMap;
                }
                EnsureSnapshotSubscription(doc);
                var state = stateProvider ?? NullStateProvider.Instance;
                SnapshotPassState pass = null;
                if (useSnapshot && !hasScopePseudo && compiledSelectors.Count > 0) {
                    DomSnapshot snapshot;
                    bool reuseSnapshot = !snapshotDirty && lastSnapshot != null;
                    if (reuseSnapshot) {
                        snapshot = lastSnapshot;
                    } else {
                        using (PerfMarkerScope.Auto(UIProfilerMarkers.SnapshotBuild)) {
                            // Refill the persistent snapshot in place so its 11 typed
                            // arrays are recycled across passes. Steady-state on a
                            // stable tree shape allocates zero. Fresh Build is reserved
                            // for the very first pass and for callers that explicitly
                            // construct a one-shot snapshot.
                            if (lastSnapshot == null) {
                                snapshot = DomSnapshot.Build(doc, symbols);
                            } else {
                                snapshot = lastSnapshot;
                                snapshot.Refill(doc, symbols);
                            }
                        }
                        lastSnapshot = snapshot;
                        snapshotDirty = false;
                        // Full Refill rebuilt every node; any queued per-node
                        // refreshes are superseded. Clear to prevent double-
                        // refresh on the next pass.
                        snapshotDirtyNodes.Clear();
                    }
                    snapshotBuildCount++;
                    EnsureIndex();
                    snapshotPass.Reset(snapshot, selectorIndex, compiledSelectors);
                    pass = snapshotPass;
                    AlignStyleArrayTo(snapshot);
                } else {
                    // Managed path has no snapshot; clear the parallel array so
                    // stale NodeId-indexed entries don't leak into consumers.
                    styleArray.Clear();
                }
                try {
                    foreach (var child in doc.Children) {
                        Walk(child, null, state, resultMap, pass);
                    }
                } finally {
                    // Drop snapshot/index references so we don't pin large state between
                    // calls; the dictionary buckets themselves stay allocated.
                    if (pass != null) pass.Deactivate();
                }
                lastObservedMediaContextVersion = mediaContextVersion;
                // Full walk handles tree-shape changes implicitly — every
                // element is visited and re-cascaded if its key shifted, so
                // nth-child / sibling-combinator output is by definition fresh.
                treeShapeDirty = false;
                return resultMap;
            }
        }

        // Incremental cascade. Re-evaluates only the ancestor closure of
        // `dirtyHints` (those elements + every element in their parent chain),
        // recursing into a subtree only when an element's ComputedStyle
        // actually changed Version. Falls back to the full ComputeAll when
        // any precondition that would compromise correctness fails:
        //   - Initial pass: resultMap is empty; nothing to incrementally
        //     update against.
        //   - Tree-shape changed since last pass: treeShapeDirty == true; a
        //     child add/remove invalidates not just the changed parent but
        //     also any selectors whose match depends on tree position
        //     (nth-child, sibling combinators, descendant selectors crossing
        //     the mutated edge). Attribute-only mutations (the common case
        //     for a click that toggles a class) do NOT set treeShapeDirty;
        //     the snapshot is refilled inline below but the walk stays
        //     incremental.
        //   - :scope / :has() pseudo in stylesheet: hasScopePseudo == true;
        //     :has() flips an ancestor's match when a descendant's state
        //     changes, so a state flip on element X can affect the cascade
        //     for ancestors of X that we'd otherwise treat as clean.
        //   - Sibling-combinator selectors gated on stateful pseudo-classes
        //     (`.btn:hover + .next`): RequiresGlobalFallback == true; per-
        //     element digest cannot detect "my left sibling's state changed",
        //     so we have to re-evaluate everything.
        //   - Media context changed since last pass: every element's cache
        //     key includes mediaContextVersion; a change shifts ALL keys,
        //     not just the dirty ones, so dirty-subtree skipping would leave
        //     stale entries everywhere.
        //
        // Callers should pass tracker.GetDirty(Style | PseudoClassState |
        // Structure). Layout-only or Paint-only dirty doesn't trigger
        // ComputeAll at all in UIDocumentLifecycle, so those marks won't
        // appear in the hints.
        //
        // Returns the same resultMap reference as ComputeAll. For the
        // incremental path, entries for non-walked elements carry over
        // from the previous pass — that's the whole point — so the
        // caller must not assume "missing from resultMap" means "removed
        // from doc"; an element is only removed from resultMap when the
        // next full cascade runs and the element is no longer in the
        // walked tree.
        public IReadOnlyDictionary<Element, ComputedStyle> ComputeAllIncremental(
            Document doc, IElementStateProvider stateProvider, System.Collections.Generic.IEnumerable<Element> dirtyHints) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.CascadeComputeAll)) {
                if (doc == null) {
                    resultMap.Clear();
                    styleArray.Clear();
                    return resultMap;
                }
                EnsureSnapshotSubscription(doc);
                var state = stateProvider ?? NullStateProvider.Instance;

                // Decide whether incremental is safe; fall through to full
                // cascade when any precondition fails. The full path also
                // handles the dirtyHints == null case implicitly by walking
                // the whole tree. Note: snapshotDirty alone does NOT bail —
                // attribute mutations need a snapshot refill but the walk
                // itself can stay narrow. Only tree-shape mutations force
                // the full Walk (see treeShapeDirty docs above).
                bool canIncremental = dirtyHints != null
                    && resultMap.Count > 0
                    && !treeShapeDirty
                    && !hasScopePseudo
                    && mediaContextVersion == lastObservedMediaContextVersion
                    && (compiledSelectors.Count == 0
                        || !EnsureStateIndex().RequiresGlobalFallback);
                if (!canIncremental) {
                    return ComputeAll(doc, stateProvider);
                }

                // Set up the snapshot pass. If snapshotDirty is set (attribute
                // mutation since last pass), bring the persistent snapshot up
                // to date. Two paths:
                //   - Per-node RefreshNode for each entry in
                //     snapshotDirtyNodes: O(dirty) work, append-at-end keeps
                //     existing nodeIds stable. Used when treeShapeDirty is
                //     false (which it must be on this path anyway — see the
                //     precondition gate above).
                //   - Full Refill when lastSnapshot is null (initial build)
                //     or the dirty set is empty (shouldn't happen since
                //     snapshotDirty implies at least one mutation, but the
                //     fallback is correct).
                SnapshotPassState pass = null;
                if (useSnapshot && compiledSelectors.Count > 0) {
                    if (snapshotDirty || lastSnapshot == null) {
                        using (PerfMarkerScope.Auto(UIProfilerMarkers.SnapshotBuild)) {
                            if (lastSnapshot == null) {
                                lastSnapshot = DomSnapshot.Build(doc, symbols);
                            } else if (snapshotDirtyNodes.Count > 0 && !treeShapeDirty) {
                                foreach (var n in snapshotDirtyNodes) {
                                    lastSnapshot.RefreshNode(n, symbols);
                                }
                            } else {
                                lastSnapshot.Refill(doc, symbols);
                            }
                        }
                        snapshotBuildCount++;
                        snapshotDirty = false;
                        snapshotDirtyNodes.Clear();
                    }
                    EnsureIndex();
                    snapshotPass.Reset(lastSnapshot, selectorIndex, compiledSelectors);
                    pass = snapshotPass;
                    // AlignTo (not AlignStyleArrayTo) — the full helper does a
                    // styleArray.Clear() which wipes entries for clean nodes;
                    // those entries are still valid for the incremental pass
                    // because tree shape is unchanged and the per-element cache
                    // is intact. Only resize the backing buffer to track the
                    // (possibly unchanged) snapshot node count.
                    styleArray.AlignTo(lastSnapshot);
                }

                // Build the ancestor closure. Each `n.Parent as Element` step
                // walks DOM parents (skipping non-Element nodes implicitly via
                // the `as` cast — text nodes don't have an Element parent, but
                // a dirty element's parent chain is always Element-typed until
                // it reaches the document root, which is a Node, not an
                // Element, and exits the loop). HashSet.Add returns false when
                // the element is already in the closure, which means we've
                // walked through this ancestor before — break to avoid the
                // O(depth^2) chain re-walk when many dirty elements share
                // ancestors.
                var walkSet = ancestorClosureScratch;
                walkSet.Clear();
                ResetWalkSetByParent();
                // Fast-path the two common collection shapes to avoid
                // IEnumerable<T>.GetEnumerator()'s heap-allocated
                // enumerator. T[]'s GetEnumerator boxes a struct into
                // SZGenericArrayEnumerator, allocating ~24B per call on
                // the warm-flip hot path. With dirtyHints typed as
                // IReadOnlyList<T> at the call site (UIDocumentLifecycle
                // passes an Element[] from the tracker scan), this collapses
                // the per-call alloc cost to zero.
                if (dirtyHints is Element[] hintsArr) {
                    for (int i = 0; i < hintsArr.Length; i++) {
                        BuildClosureFromHint(hintsArr[i], walkSet);
                    }
                } else if (dirtyHints is System.Collections.Generic.IList<Element> hintsList) {
                    for (int i = 0; i < hintsList.Count; i++) {
                        BuildClosureFromHint(hintsList[i], walkSet);
                    }
                } else {
                    foreach (var elem in dirtyHints) {
                        BuildClosureFromHint(elem, walkSet);
                    }
                }

                // Empty closure means nothing to do — every dirty hint was
                // null or detached. Return the previous resultMap unchanged.
                if (walkSet.Count == 0) {
                    if (pass != null) pass.Deactivate();
                    return resultMap;
                }

                // Find the topmost walkSet element so we can skip the
                // doc → walkRoot traversal entirely. The previous loop
                // started from each direct doc child and recursed into
                // every node looking for walkSet hits; for a 2000-node
                // tree with a dirty closure of 5 elements that wastes
                // ~10µs on hash lookups and recursion frames per pass.
                // walkRoot is the element in walkSet whose Element-typed
                // parent is NOT in walkSet (i.e. the boundary between
                // clean and dirty). For a connected tree there's exactly
                // one — the LCA of all dirty hints.
                Element walkRoot = null;
                foreach (var n in walkSet) {
                    var parent = n.Parent as Element;
                    if (parent == null || !walkSet.Contains(parent)) {
                        walkRoot = n;
                        break;
                    }
                }

                try {
                    if (walkRoot != null) {
                        // Look up walkRoot's parent style from the cache —
                        // walkRoot.Parent is by definition NOT in walkSet
                        // (clean), so its cached style is current.
                        ComputedStyle rootParentStyle = null;
                        if (walkRoot.Parent is Element pe && cache.TryGetValue(pe, out var pentry)) {
                            rootParentStyle = pentry.Style;
                        }
                        WalkIncremental(walkRoot, rootParentStyle, state, resultMap, pass, walkSet);
                    } else {
                        // Fallback: no clean Element-parent boundary found
                        // (shouldn't normally happen since walkSet is non-
                        // empty). Walk from doc as the legacy path did.
                        foreach (var child in doc.Children) {
                            WalkIncremental(child, null, state, resultMap, pass, walkSet);
                        }
                    }
                } finally {
                    if (pass != null) pass.Deactivate();
                    walkSet.Clear();
                }
                lastObservedMediaContextVersion = mediaContextVersion;
                return resultMap;
            }
        }

        void WalkIncremental(Node node, ComputedStyle parentStyle, IElementStateProvider state,
                             Dictionary<Element, ComputedStyle> result, SnapshotPassState pass,
                             System.Collections.Generic.HashSet<Element> walkSet) {
            if (node is Element e) {
                // walkSet.Contains check elided — callers (walkRoot entry
                // path and the walkSetByParent iteration below) only invoke
                // this with elements known to be in walkSet. The legacy
                // doc.Children fallback path is the one exception, but it
                // bails fast on non-Element nodes immediately and only
                // descends through their (typically empty) children list.
                System.Diagnostics.Debug.Assert(walkSet.Contains(e),
                    "WalkIncremental: caller must ensure element is in walkSet");

                // Capture the prior style's Version BEFORE ComputeOrHit
                // runs: on a cache miss with style recycling enabled,
                // ComputeFor reuses the same ComputedStyle instance and
                // bumps its Version in-place. Reading entry.Style.Version
                // AFTER would observe the new value, defeating the change
                // detection. A fresh-build element (no prior entry) gets
                // prevVersion = 0; any cs.Version > 0 then registers as a
                // change, which forces a full walk of its children — the
                // correct behavior for elements newly attached to the
                // cache.
                long prevVersion = 0;
                if (cache.TryGetValue(e, out var prevEntry) && prevEntry.Style != null) {
                    prevVersion = prevEntry.Style.Version;
                }
                var cs = ComputeOrHit(e, parentStyle, state, pass);
                result[e] = cs;
                if (pass != null && pass.TryGetNodeId(e, out int nodeId)) {
                    styleArray.Set(nodeId, cs);
                }
                bool styleChanged = prevVersion != cs.Version;

                if (styleChanged) {
                    // ParentStyleVersion in every descendant's cache
                    // key just shifted. Drop back to the full Walk so
                    // descendants' caches are re-checked even if they
                    // weren't in the dirty closure.
                    var kids = e.Children;
                    int count = kids.Count;
                    for (int i = 0; i < count; i++) {
                        Walk(kids[i], cs, state, result, pass);
                    }
                } else if (walkSetByParentScratch.TryGetValue(e, out var dirtyKids)) {
                    // Only walkSet children need visiting. The per-parent
                    // index built during the closure pass collapses an
                    // O(children) HashSet.Contains sweep down to a single
                    // dictionary lookup + a small list iteration. For a
                    // <section> with 250 children and one dirty grand-
                    // child, this is the difference between 250 hash
                    // probes and 1.
                    for (int i = 0; i < dirtyKids.Count; i++) {
                        WalkIncremental(dirtyKids[i], cs, state, result, pass, walkSet);
                    }
                }
                // else: no in-walkSet children for this element — done.
                return;
            }
            // Non-Element node (TextNode and friends). Recurse without
            // closure-gating: the gate is per-Element, and text nodes
            // never have an entry in walkSet. Their Element children, if
            // any (there aren't, but for symmetry with Walk), still get
            // checked at the next Element node.
            var nkids = node.Children;
            int ncount = nkids.Count;
            for (int i = 0; i < ncount; i++) {
                WalkIncremental(nkids[i], parentStyle, state, result, pass, walkSet);
            }
        }

        void Walk(Node node, ComputedStyle parentStyle, IElementStateProvider state, Dictionary<Element, ComputedStyle> result, SnapshotPassState pass) {
            if (node is Element e) {
                var cs = ComputeOrHit(e, parentStyle, state, pass);
                result[e] = cs;
                if (pass != null && pass.TryGetNodeId(e, out int nodeId)) {
                    styleArray.Set(nodeId, cs);
                }
                // Index loop instead of foreach: Children is typed
                // IReadOnlyList<Node>, so a foreach allocates a boxed
                // enumerator per call (~40 B). On a large production HUD
                // Walk runs ~1500-2000 times per ComputeAll, so the
                // boxing alone churns 60-80 KB per cascade pass and
                // makes click-cascade Updates land in the GC. The
                // typed list supports indexer access; the cost is the
                // same as the enumerator's MoveNext+Current.
                var kids = e.Children;
                int count = kids.Count;
                for (int i = 0; i < count; i++) {
                    Walk(kids[i], cs, state, result, pass);
                }
                return;
            }
            var nkids = node.Children;
            int ncount = nkids.Count;
            for (int i = 0; i < ncount; i++) {
                Walk(nkids[i], parentStyle, state, result, pass);
            }
        }

        public void AttachAnimationRunner(CssAnimationRunner runner) {
            animationRunner = runner;
        }

        public CssAnimationRunner AnimationRunner => animationRunner;

        public ComputedStyle GetComposedStyle(Element element, IElementStateProvider stateProvider = null) {
            var baseStyle = Compute(element, stateProvider);
            if (animationRunner == null) return baseStyle;
            return animationRunner.Compose(element, baseStyle);
        }

        ComputedStyle ComputeOrHit(Element element, ComputedStyle parentStyle, IElementStateProvider state, SnapshotPassState pass) {
            long parentV = parentStyle != null ? parentStyle.Version : 0;
            // ResolveStateDigest replaces the prior `state.Version` so that a
            // pseudo-class flip on one element doesn't invalidate cached entries
            // for elements whose own state (filtered to bits any selector actually
            // tests) is unchanged. See CascadeEngine.IncrementalState.cs for the
            // correctness invariants and the v1 sibling-combinator fallback.
            long stateV = ResolveStateDigest(element, state);
            int providerId = RuntimeHelpers.GetHashCode(state);
            var key = new IncrementalCacheKey(element.Version, parentV, mediaContextVersion, stateV, providerId);

            if (cache.TryGetValue(element, out var entry) && entry.Key.Equals(key)) {
                cacheHits++;
                return entry.Style;
            }

            cacheMisses++;
            ComputedStyle previousStyle = null;
            ulong previousLayoutDigest = 0UL;
            bool hadPreviousEntry = false;
            string previousDisplay = null;
            if (cache.TryGetValue(element, out var stale)) {
                previousStyle = stale.Style;
                previousLayoutDigest = stale.LayoutDigest;
                hadPreviousEntry = true;
                // Snapshot `display` BEFORE we re-cascade so the post-cascade
                // none-boundary check below can compare against the prior
                // value. We snapshot from the cached style rather than diffing
                // after, because the recycling path (when enabled) overwrites
                // previousStyle in place during ComputeFor.
                previousDisplay = previousStyle?.Get(CssProperties.DisplayId);
            }
            // Per-element pool: when a re-cascade replaces an element's
            // style, the previous style is ALWAYS safe to recycle as the
            // new style for THE SAME ELEMENT. The earlier disabled-pool
            // path failed because it cross-rebound styles between
            // elements (a `.bg-aurora` Element's style getting recycled
            // into the body Element on a later cascade pass) — that
            // really did smear filter:blur into unrelated boxes.
            //
            // Same-element recycling is sound because every Box that ever
            // held this Element's old style will either (a) be re-laid-
            // out before paint runs (taking the new style's values via
            // RefreshPaintOnlyStyles) or (b) be reading the same
            // string[] backing array — which the recycled style has now
            // overwritten with fresh values resolved against the *same*
            // element's selectors. The values may have shifted (an
            // animation tick) but they cannot be the wrong element's
            // values, which was the original smearing failure mode.
            //
            // OnStyleChange below does a property-by-property diff between
            // `previousStyle` and `style` to start CSS transitions per
            // CSS Transitions L2 §3.1 — feeding it the same object twice
            // makes every property compare equal and silently drops the
            // transition. Layout dirtying is safe under recycling because we
            // capture LayoutDigest above, but the transition diff has no such
            // hashable proxy. Only elements with a transition need the diff,
            // so we suppress recycling JUST for those (WantsStyleDiff), not
            // for every element under an attached runner.
            // C4: recycle previousStyle in place (the dominant per-interaction
            // allocation — ~1.7 KB/element) unless OnStyleChange needs it
            // intact to diff a transition start. Previously ANY attached
            // animation runner disabled recycling for every element, but only
            // elements with a transition actually need the separate previous;
            // a hover that re-cascades a transition-free subtree now recycles.
            ComputedStyle recyclable =
                (animationRunner == null || !animationRunner.WantsStyleDiff(element))
                    ? previousStyle : null;
            var style = ComputeFor(element, parentStyle, state, pass, recyclable);
            ulong newLayoutDigest = ComputeLayoutDigest(style);
            cache[element] = new StyleCacheEntry(key, style, newLayoutDigest);
            if (animationRunner != null) {
                animationRunner.OnStyleChange(element, previousStyle, style);
            }
            // previousStyle is not pooled — see above. The GC reclaims it
            // when no Box still holds the reference.
            // v0.7: surface layout-affecting property deltas to the per-pass
            // dirty set. The set is later drained onto an InvalidationTracker
            // by ApplyLayoutInvalidation, which lets the layout engine narrow
            // its re-layout to the elements whose computed layout properties
            // actually changed. Skip on the first-ever resolution for the
            // element (hadPreviousEntry == false) — no diff is possible.
            //
            // Same-element ComputedStyle recycling overwrites `previousStyle`
            // in place during ComputeFor, so a value-by-value diff between
            // previousStyle and style would be self-comparing and always
            // return false. We capture the layout-affecting digest from the
            // pre-recycle cache entry above and compare against the freshly
            // computed digest here — that recovers the before/after invariant
            // the diff needs.
            if (hadPreviousEntry && previousLayoutDigest != newLayoutDigest) {
                NoteLayoutDirtyFromCascade(element);
            }
            // Structure invalidation triggers a box-tree rebuild. Only the
            // `display: none ↔ <anything-else>` flip genuinely needs the
            // rebuild (none → no box exists; non-none → box must be built
            // or destroyed). Every other display change (block → flex,
            // inline → inline-block, ...) is handled by Layout. ClassBinding
            // used to mark Structure unconditionally on every class flip,
            // which made the box tree rebuild on every cooldown tick and
            // briefly orphaned the hit-tester (the cause of a production HUD's
            // `.skill-tooltip.visible` flicker on sustained hover). Source-
            // of-truth for "we need a rebuild" lives here now — only the
            // cascade can see whether the resolved `display` actually
            // crossed the boundary.
            string newDisplay = style?.Get(CssProperties.DisplayId);
            if (hadPreviousEntry) {
                bool wasNone = previousDisplay == "none";
                bool isNone = newDisplay == "none";
                if (wasNone != isNone) {
                    NoteStructureDirtyFromCascade(element);
                }
            }
            return style;
        }

        // Per-pass counters for the matched-properties cache. Exposed via
        // CascadeEngine properties for benchmarks.
        long matchedPropsHits;
        long matchedPropsMisses;
        public long MatchedPropsHits => matchedPropsHits;
        public long MatchedPropsMisses => matchedPropsMisses;

        // Shared style entries indexed by a recursive MatchedKey derived from
        // (parent's MatchedKey, this element's matched-declaration set,
        // state digest). Two elements with the same key produce identical
        // computed values; the second-and-on lookups CopyFrom the cached
        // canonical instance instead of running the full ComputeFor.
        readonly Dictionary<ulong, ComputedStyle> matchedPropsCache = new();

        // Shape-keyed selector match cache. Keyed on (parent.MatchedKey,
        // element shape, ancestor class chain, state digest) — shape
        // captures tag/id/classes/attribute name-value pairs that
        // selectors test. Skips the CollectMatchesFromSnapshot pass for
        // siblings with identical selector-relevant features. Counters
        // drive benchmark visibility.
        readonly Dictionary<ulong, MatchedDeclaration[]> shapeCache = new();

        partial void ClearShapeCache() {
            shapeCache.Clear();
        }

        // Commutative hash over whitespace-separated class tokens in the
        // raw class attribute string. Equivalent to Element.ClassList's
        // iteration but doesn't allocate the yield-return state machine
        // (~50B each) — the shape-key compute fires this ~5-6 times per
        // warm flip on the ancestor walk, so the allocation gap is real.
        // Same separator set as Element.ClassList: ASCII whitespace.
        static ulong HashClassTokens(string classAttr) {
            if (string.IsNullOrEmpty(classAttr)) return 0;
            ulong hash = 0;
            int len = classAttr.Length;
            int i = 0;
            while (i < len) {
                while (i < len && IsClassWhitespace(classAttr[i])) i++;
                int start = i;
                while (i < len && !IsClassWhitespace(classAttr[i])) i++;
                if (i > start) {
                    // string.GetHashCode is what foreach (... in ClassList)
                    // would call on each token — we just avoid the
                    // intermediate string.Substring by hashing the span
                    // ourselves.
                    int tokenHash = 0;
                    unchecked {
                        // FNV-1a-ish per-char fold; matches no specific
                        // .NET hash impl but is commutative-via-XOR-with
                        // the same Knuth constant below.
                        tokenHash = (int)2166136261u;
                        for (int j = start; j < i; j++) {
                            tokenHash = (tokenHash ^ classAttr[j]) * 16777619;
                        }
                    }
                    hash ^= (ulong)tokenHash * 2654435761UL;
                }
            }
            return hash;
        }

        static bool IsClassWhitespace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }
        long shapeCacheHits;
        long shapeCacheMisses;
        public long ShapeCacheHits => shapeCacheHits;
        public long ShapeCacheMisses => shapeCacheMisses;

        // Computes a shape hash for the matches-cache key. Returns 0 to
        // bypass when the element has an inline style attribute (the
        // inline declarations are element-specific and not in the
        // computed shape).
        ulong TryComputeShapeKey(Element element, ComputedStyle parentStyle, IElementStateProvider state) {
            if (element == null) return 0;
            // Inline style is per-element — AddInlineDeclarations injects
            // values that aren't captured by tag/class/attribute shape.
            // Conservative: skip cache for elements with `style=""`.
            if (element.HasAttribute("style")) return 0;
            // Parent opt-out propagates: descendants of an opted-out
            // parent can't reliably share matches either (the parent's
            // ancestor context isn't represented in the cache key chain).
            if (parentStyle != null && parentStyle.MatchedKey == 0) return 0;
            // CX2: sibling combinators (`p + p`) / of-type pseudos make the
            // match depend on preceding-sibling COMPOSITION (which tags come
            // before), which no per-element key can represent — two
            // identical-shape elements with different preceding siblings
            // would share a match set. Disable sharing for such sheets.
            if (shapeCacheUnsafeForSiblingComposition) return 0;
            // CX3: a :has() subject's match set depends on DESCENDANT content
            // the shape key cannot represent — and a descendant mutation
            // cannot invalidate an ancestor's shape entry (the key is a hash,
            // there's no reverse index). With sharing on, the HasSensitive
            // tracker mark correctly dropped the ancestor's cache entry but
            // the recompute was served the STALE match set by shape key.
            // Disable sharing for sheets containing :has() (rare, and already
            // the expensive selector class in browsers).
            if (HasAnyHasSelector) return 0;

            ulong h = 14695981039346656037UL;
            if (element.TagName != null) {
                h ^= (ulong)element.TagName.GetHashCode();
                h *= 1099511628211UL;
            }
            var id = element.Id;
            if (!string.IsNullOrEmpty(id)) {
                h ^= (ulong)id.GetHashCode();
                h *= 1099511628211UL;
            }
            // Classes: commutative XOR so token order in the class
            // attribute doesn't shift the hash. Iterate the raw class
            // attribute directly instead of going through Element.ClassList
            // — that property uses `yield return` which allocates an
            // iterator state machine per call (~50B). On the warm-flip
            // path this fires ~5 times per cascade (ancestor chain walk
            // below included), accounting for ~250B of the per-flip GC
            // budget.
            ulong classHash = HashClassTokens(element.ClassName);
            h ^= classHash;
            h *= 1099511628211UL;
            // Attributes: include name AND value because attribute selectors
            // test both (`[data-state="open"]` ≠ `[data-state="closed"]`).
            // Skip class/id since those have their own folding above.
            ulong attrHash = 0;
            var attrs = element.Attributes;
            int attrCount = attrs.Count;
            for (int i = 0; i < attrCount; i++) {
                var n = attrs.NameAt(i);
                if (n == "class" || n == "id") continue;
                var v = attrs[n];
                ulong nh = (ulong)n.GetHashCode() * 2654435761UL;
                ulong vh = v == null ? 0UL : (ulong)v.GetHashCode() * 14695981039346656037UL;
                attrHash ^= nh ^ vh;
            }
            h ^= attrHash;
            h *= 1099511628211UL;
            // Encode the FULL ancestor class/tag/id chain. parent.MatchedKey
            // alone is insufficient because an ancestor's class can change
            // without changing the ancestor's matched-declaration set — a
            // rule like ".parent #c" matches the descendant only, so
            // flipping the ancestor's class between ".parent" and ".other"
            // leaves the ancestor's own MatchedKey unchanged (neither
            // class triggers any rule whose SUBJECT is the ancestor).
            // Walk up and fold each ancestor's tag/id/classes into the
            // shape key directly. Cost: ~5 ancestors × ~50ns per shape
            // key compute = ~250ns; for 1500 elements that's <1ms.
            //
            // Also fold in ancestor STATE bits. A rule like `div:hover span`
            // has state on the LEFT of a descendant combinator — the span's
            // own stateDigest is 0 (span has no hover) but the match set
            // changes when div's hover flips. Including ancestor state bits
            // in the shapeKey ensures distinct keys for `div:hover span` vs
            // `div span`.
            var stateIdx = EnsureStateIndex();
            var ancestorStateMask = stateIdx.GlobalStateMask;
            for (Element a = element.Parent as Element; a != null; a = a.Parent as Element) {
                ulong anc = 0;
                if (a.TagName != null) anc ^= (ulong)a.TagName.GetHashCode();
                var aid = a.Id;
                if (!string.IsNullOrEmpty(aid)) anc ^= (ulong)aid.GetHashCode() * 257UL;
                anc ^= HashClassTokens(a.ClassName);
                // Fold ancestor state into the key so `parent:hover child` rules
                // produce distinct shape keys when parent hover state differs.
                if (ancestorStateMask != ElementState.None) {
                    ElementState ancState = state.GetState(a) & ancestorStateMask;
                    anc ^= (ulong)(int)ancState * 2654435761UL;
                }
                // CX2: positional pseudos can sit on ANCESTOR compounds too
                // (`ul:first-child li`) — fold each ancestor's position so
                // identical-shape ancestors at different indices produce
                // distinct keys for their descendants.
                if (shapeKeyFoldsSiblingIndex) {
                    SiblingPosition(a, out int aIdx, out int aCnt);
                    anc ^= ((ulong)(uint)aIdx * 2654435761UL)
                         ^ ((ulong)(uint)aCnt * 40503UL)
                         ^ ((ulong)(uint)a.Children.Count * 257UL);
                }
                h ^= anc;
                h *= 1099511628211UL;
            }
            // State digest — :hover / :active flips change which rules match.
            ulong stateDigest = (ulong)ResolveStateDigest(element, state);
            h ^= stateDigest;
            h *= 1099511628211UL;
            // CX2: index-positional pseudos (:nth-child, :first/last/only-
            // child, :nth-last-child, :empty) differentiate identical-shape
            // siblings by position. Fold the element's index among element
            // siblings (nth/first), the parent's element-child count
            // (nth-last/last/only), and the element's own node count
            // (:empty) so each position gets its own match-set entry.
            // Pre-CX2 `li:nth-child(odd)` served row 1's matches to every
            // identical <li>.
            if (shapeKeyFoldsSiblingIndex) {
                SiblingPosition(element, out int sibIndex, out int sibCount);
                ulong pos = ((ulong)(uint)sibIndex * 2654435761UL)
                          ^ ((ulong)(uint)sibCount * 40503UL)
                          ^ ((ulong)(uint)element.Children.Count * 257UL);
                h ^= pos;
                h *= 1099511628211UL;
            }
            if (h == 0) h = 1;
            return h;
        }

        // Index of `e` among its parent's ELEMENT children (CSS :nth-child
        // counts elements only) plus the total element-sibling count. Root /
        // detached elements report (0, 0).
        static void SiblingPosition(Element e, out int index, out int elementCount) {
            index = 0;
            elementCount = 0;
            var parent = e.Parent;
            if (parent == null) return;
            var kids = parent.Children;
            for (int i = 0; i < kids.Count; i++) {
                if (kids[i] is Element el) {
                    if (ReferenceEquals(el, e)) index = elementCount;
                    elementCount++;
                }
            }
        }

        ComputedStyle ComputeFor(Element element, ComputedStyle parentStyle, IElementStateProvider state, SnapshotPassState pass, ComputedStyle recyclable = null) {
            // Per-element scratch buffers are reused across the cascade walk; clearing
            // is O(n) but doesn't free underlying storage so subsequent inserts are
            // amortized free after the first warmup element.
            var s = scratch;
            s.ResetPerElement();
            var matches = s.Matches;

            // Shape-keyed selector match cache. For two elements with the
            // same (parent.MatchedKey, own shape signature, state digest)
            // the matched declaration set is byte-for-byte identical —
            // selectors care only about the element's tag/id/classes/attrs
            // and the ancestor context, both captured in the key. Skip
            // CollectMatchesFromSnapshot and reuse the cached array. For
            // production-shape fixtures this saves ~2µs per element on
            // cold pass (~3ms total on 1500 elements).
            ulong shapeKey = TryComputeShapeKey(element, parentStyle, state);
            if (shapeKey != 0 && shapeCache.TryGetValue(shapeKey, out var cachedMatches)) {
                matches.Clear();
                for (int i = 0; i < cachedMatches.Length; i++) matches.Add(cachedMatches[i]);
                shapeCacheHits++;
            } else {
                if (pass != null && pass.TryGetNodeId(element, out int nodeId)) {
                    CollectMatchesFromSnapshot(element, state, pass, nodeId, matches);
                } else {
                    CollectMatchesManaged(element, state, matches);
                }
                AddInlineDeclarations(element, matches);
                if (shapeKey != 0) {
                    // Snapshot the matches list so subsequent shorthand
                    // expansion (which mutates s.Matches) doesn't corrupt
                    // the cache entry.
                    var arr = new MatchedDeclaration[matches.Count];
                    for (int i = 0; i < matches.Count; i++) arr[i] = matches[i];
                    shapeCache[shapeKey] = arr;
                    shapeCacheMisses++;
                }
            }

            // Matched-properties cache lookup. Two elements with the same
            // (parent.MatchedKey, matched-declaration set, state digest)
            // produce identical computed values, so the second-and-on
            // visitors can CopyFrom the canonical cache entry instead of
            // running the full ComputeFor resolve chain. Element-specific
            // dependencies (attr() in any matched value) make the result
            // non-shareable — TryComputeMatchedKey returns 0 to bypass.
            ulong matchedKey = TryComputeMatchedKey(matches, parentStyle, element, state);
            if (matchedKey != 0 && matchedPropsCache.TryGetValue(matchedKey, out var cachedTemplate)) {
                matchedPropsHits++;
                ComputedStyle shareTarget;
                if (recyclable != null) {
                    recyclable.RebindForReuse(element);
                    shareTarget = recyclable;
                } else if (animationRunner == null && computedStylePool.Count > 0) {
                    // PERF-1: matched-props-cache hit on the cold path. Same pool
                    // pop logic as the full-compute branch above.
                    var pooled = computedStylePool.Pop();
                    pooled.RebindForReuse(element);
                    shareTarget = pooled;
                } else {
                    shareTarget = new ComputedStyle(element, 192);
                }
                shareTarget.CopyFrom(cachedTemplate);
                shareTarget.MatchedKey = matchedKey;
                return shareTarget;
            }
            if (matchedKey != 0) matchedPropsMisses++;

            // Expand shorthands into ExpandedMatches, then swap so `matches` becomes the
            // post-expansion list. We swap rather than copying back to keep this O(emit).
            ExpandShorthandMatchesInto(matches, s.ExpandedMatches, s);
            // Hand `ExpandedMatches` to the rest of the pipeline; `Matches` is now stale
            // pre-expansion data and won't be touched again this element.
            var expanded = s.ExpandedMatches;

            expanded.Sort(CompareForCascadeDelegate);

            // Pre-size the result dictionary so we don't pay rehash cost as the ~150
            // CssProperties.All defaults + custom props get filled in. 192 is a generous
            // ceiling for both 0-rule (initial-only) and rule-heavy elements.
            // Recycle a pooled ComputedStyle backing — Reset() clears values
            // + bumps Version while preserving the values[] / occupied[]
            // arrays. The Element property is read-only after construction,
            // so a pooled instance carries a stale Element reference; we use
            // RebindForReuse to point it at the new element.
            ComputedStyle style;
            if (recyclable != null) {
                recyclable.RebindForReuse(element);
                style = recyclable;
            } else if (animationRunner == null && computedStylePool.Count > 0) {
                // PERF-1: cold-miss path (no same-element recyclable available,
                // e.g. first pass after InvalidateAll). Pop a cross-element
                // pooled instance whose backing arrays are pre-sized. This is
                // safe because:
                //   (a) RebindForReuse calls Reset() which clears all values,
                //       so no stale data from the previous owner leaks.
                //   (b) animationRunner == null guards the transition-diff path
                //       that requires a separately-allocated previousStyle.
                //   (c) The pool was populated in InvalidateAll from the same
                //       cache — same engine, same registered-property count, so
                //       the backing arrays are already at the right size.
                var pooled = computedStylePool.Pop();
                pooled.RebindForReuse(element);
                style = pooled;
            } else {
                style = new ComputedStyle(element, 192);
            }
            var perPropertyWinner = s.PerPropertyWinner;
            for (int i = 0; i < expanded.Count; i++) {
                var m = expanded[i];
                // CSS Syntax 3 §5 / CSS Cascade L5 §3: per-property keyword
                // validation. A declaration whose value is not in the property's
                // allowed keyword set is treated as if not specified — skip it so
                // the cascade falls back to the next-lower-priority match.
                // Custom properties, var()-containing values, and CSS-wide keywords
                // bypass validation (see CssPropertyKeywordValidator for the full
                // bypass contract). Properties with no registered keyword set are
                // always accepted (pass-through for unvalidated properties).
                if (!CssPropertyKeywordValidator.IsValidValue(m.Declaration.PropertyId, m.Declaration.ValueText)) {
                    continue;
                }
                perPropertyWinner[m.Declaration.Property] = m;
            }
            ApplyLogicalPropertyAliases(perPropertyWinner, parentStyle);

            // First pass: assign raw winning values (still possibly keywords or var()).
            var rawValues = s.RawValues;
            foreach (var kv in perPropertyWinner) {
                rawValues[kv.Key] = kv.Value.Declaration.ValueText;
            }

            // Resolve custom properties first so non-custom var() lookups can see them.
            // Custom properties cascade like normal properties; their values may themselves
            // contain var() references and CSS-wide keywords.
            foreach (var kv in rawValues) {
                if (!CssProperties.IsCustomProperty(kv.Key)) continue;
                // ATPROP-1: KeywordResolver treats every custom property as
                // inheriting (CssProperties.IsInherited returns true for any
                // `--name`), so `unset` on a custom property always falls
                // through to ResolveInherit. For a property registered with
                // `@property inherits:false` CSS Cascade L5 §7.3 mandates
                // `unset` = `initial`. Intercept here using the registry the
                // resolver doesn't have access to.
                string resolved;
                string trimmedRaw = kv.Value?.Trim();
                if (trimmedRaw != null
                    && string.Equals(trimmedRaw, "unset", System.StringComparison.OrdinalIgnoreCase)
                    && propertyRegistry.IsNonInheriting(kv.Key)) {
                    resolved = propertyRegistry.GetInitialValue(kv.Key);
                } else {
                    resolved = KeywordResolver.Resolve(kv.Key, kv.Value, parentStyle);
                }
                // CSS Properties & Values L1 — validate authored value against
                // the registered syntax. If the value fails validation the
                // declaration is treated as invalid at computed-value time and
                // the descriptor's initial-value is used instead.
                if (!propertyRegistry.ValidateValue(kv.Key, resolved)) {
                    string initial = propertyRegistry.GetInitialValue(kv.Key);
                    if (initial != null) style.Set(kv.Key, initial);
                    // If there's no registered initial, omit so FillInherited
                    // can supply the parent's value (or nothing).
                } else {
                    style.Set(kv.Key, resolved);
                }
            }

            // Inherit any custom properties not explicitly set on this element.
            // PA: iterate the parent's underlying customs dict directly so we
            // get Dictionary<K,V>.Enumerator (struct, no alloc) instead of
            // ComputedStyle.Enumerate() which yields and allocates an
            // iterator state machine per call.
            // CSS Properties & Values L1: respect `inherits: false` — a
            // non-inheriting property is NOT copied from the parent even when
            // unset on this element (the descriptor's initial-value applies instead).
            if (parentStyle != null) {
                var parentCustoms = parentStyle.CustomPropertiesOrNull;
                if (parentCustoms != null) {
                    foreach (var kv in parentCustoms) {
                        if (style.Contains(kv.Key)) continue;
                        if (propertyRegistry.IsNonInheriting(kv.Key)) {
                            // @property says inherits: false — use initial-value, not parent's value.
                            string initial = propertyRegistry.GetInitialValue(kv.Key);
                            if (initial != null) style.Set(kv.Key, initial);
                        } else {
                            style.Set(kv.Key, kv.Value);
                        }
                    }
                }
            }

            // For @property descriptors whose value is still unset at this point,
            // seed the initial-value. This covers two cases:
            //   1. Non-inheriting property (inherits: false): initial-value is ALWAYS
            //      used when not explicitly set, regardless of parent.
            //   2. Inheriting property (inherits: true) at the root element (no
            //      parent): initial-value is used since there's nothing to inherit.
            if (propertyRegistry.Count > 0) {
                foreach (var desc in propertyRegistry.All) {
                    if (!style.Contains(desc.Name) && desc.InitialValue != null) {
                        style.Set(desc.Name, desc.InitialValue);
                    }
                }
            }

            // Resolve var() inside custom property values now that all customs are seeded.
            var customsResolved = s.CustomsResolved;
            var styleCustoms = style.CustomPropertiesOrNull;
            if (styleCustoms != null) {
                foreach (var kv in styleCustoms) {
                    string resolvedCustom = VariableResolver.Resolve(kv.Value, style);
                    resolvedCustom = EnvResolver.Resolve(resolvedCustom);
                    resolvedCustom = AttrResolver.Resolve(resolvedCustom, element);
                    resolvedCustom = LightDarkResolver.Resolve(resolvedCustom, ResolveEffectiveColorScheme(style, mediaContext));
                    customsResolved[kv.Key] = resolvedCustom;
                }
            }
            foreach (var kv in customsResolved) {
                style.Set(kv.Key, kv.Value);
            }

            // Now resolve normal (non-custom) properties.
            // ExpandShorthandMatchesInto bypasses any shorthand whose value contains
            // var() — at that point the var() reference hasn't been substituted, so
            // the shorthand parser can't tokenize the value. We re-attempt expansion
            // here, AFTER var() resolution, for any shorthand winner that still has
            // no longhand winner. This is what makes `background: var(--panel)` and
            // `border: 1px solid var(--line)` actually paint: pre-expansion sees
            // `var(--line)` as opaque and skips, but post-resolution the value has
            // become `1px solid #383d4a` and expands cleanly into per-side longhands.
            foreach (var kv in rawValues) {
                if (CssProperties.IsCustomProperty(kv.Key)) continue;
                // CSS Custom Properties L1 §3 — an unresolvable var() with no
                // usable fallback makes the WHOLE declaration invalid at
                // computed-value time. Skip the Set so FillInherited below
                // writes either the inherited value (for inherited
                // properties) or the property's initial value (non-inherited).
                if (!VariableResolver.TryResolve(kv.Value, style, out string withVars)) {
                    continue;
                }
                // CSS Environment Variables L1 — env() resolves at the same
                // cascade phase as var(); an unresolvable env() with no
                // fallback also taints the declaration so the cascade can
                // drop it.
                if (!EnvResolver.TryResolve(withVars, out string withEnv)) {
                    continue;
                }
                string withAttr = AttrResolver.Resolve(withEnv, element);
                string withLightDark = LightDarkResolver.Resolve(withAttr, ResolveEffectiveColorScheme(style, mediaContext));
                // Look up winner once and reuse the Declaration's cached
                // PropertyId — saves a CssProperties.GetId hash on every Set
                // PLUS the redundant lookup that the !important branch used
                // to do via GetId(kv.Key). The cascade hits this loop ~20
                // times per element × 1500 elements per cold pass; eliminating
                // the per-property hash here is a measurable cold-pass win.
                perPropertyWinner.TryGetValue(kv.Key, out var winnerMatch);
                // CSS Cascade L5 §7.4 / §7.5 — substitute the rolled-back
                // value text for `revert` / `revert-layer` BEFORE running
                // the CSS-wide keyword resolver. The resolver handles the
                // no-rollback-target fallback (still maps to initial).
                string preResolved = KeywordResolver.PreResolveRollback(kv.Key, withLightDark, expanded, winnerMatch);
                string resolved = KeywordResolver.Resolve(kv.Key, preResolved, parentStyle);
                int pid = winnerMatch.Declaration.PropertyId;
                if (pid >= 0) {
                    style.Set(pid, resolved);
                } else {
                    style.Set(kv.Key, resolved);
                }
                // CSS Cascading L4 §6.4.1: animations sit below !important
                // author declarations in the cascade order. Stamp the
                // importance bit on each winning property id so the
                // animation runner can refuse to overlay them.
                if (winnerMatch.Declaration.Important && pid >= 0) {
                    style.MarkImportant(pid, true);
                }

                // CSS Values L4 §6.3 — attr()-containing shorthands were kept
                // intact during pre-expansion so the expander could receive the
                // already-resolved value. Re-expand here, same as var() shorthands.
                if (ContainsSubstitutionMarker(kv.Value) && ShorthandRegistry.TryGet(kv.Key, out var lateExpander)) {
                    // The cascade winner for this shorthand key is `shorthandWinner`;
                    // its longhands inherit its origin/specificity/source. For each
                    // late-expanded longhand we may already have an independent
                    // longhand winner in `perPropertyWinner` — either because the
                    // author wrote an explicit longhand declaration, or because a
                    // lower-priority shorthand was pre-expanded into per-side
                    // longhands at parse time (e.g. UA's `border: 1px solid #ccc`
                    // expands to `border-top-color: #ccc` etc.). Only skip if that
                    // existing winner truly outranks this shorthand per the cascade
                    // (CompareForCascade > 0). Otherwise we must overwrite, so the
                    // higher-priority shorthand's per-side values actually paint.
                    var shorthandWinner = perPropertyWinner[kv.Key];
                    foreach (var lh in lateExpander.Expand(resolved ?? "")) {
                        if (perPropertyWinner.TryGetValue(lh.Key, out var existingWinner)
                            && CompareForCascade(shorthandWinner, existingWinner) < 0) {
                            // Existing longhand winner outranks this shorthand —
                            // it'll resolve in its own iteration of the rawValues
                            // loop and we must not clobber it here.
                            continue;
                        }
                        string lhResolved = KeywordResolver.Resolve(lh.Key, lh.Value, parentStyle);
                        style.Set(lh.Key, lhResolved);
                        // Inherit the !important bit from the shorthand
                        // winner to each late-expanded longhand: the
                        // author wrote `border: 1px solid red !important`,
                        // so every per-side longhand inherits importance.
                        if (shorthandWinner.Declaration.Important) {
                            int lhId = CssProperties.GetId(lh.Key);
                            if (lhId >= 0) style.MarkImportant(lhId, true);
                        }
                    }
                }
            }

            // Inheritance + initial fill-in for properties not set explicitly.
            FillInherited(style, parentStyle);

            // Cache for siblings/cousins with the same matched key. Store a
            // canonical copy — the per-element cache continues to hold the
            // element-bound style (this is what the recycle path expects)
            // and the matched-properties cache holds a separate immutable
            // template that future CopyFroms reference.
            style.MatchedKey = matchedKey;
            if (matchedKey != 0 && !matchedPropsCache.ContainsKey(matchedKey)) {
                var template = new ComputedStyle(element, 192);
                template.CopyFrom(style);
                template.MatchedKey = matchedKey;
                matchedPropsCache[matchedKey] = template;
            }

            return style;
        }

        // Recursive matched-key hash. Two parts:
        //   (a) Commutative XOR fold over each matched Declaration so the
        //       order in which they appear in `matches` doesn't matter
        //       (CollectMatches may visit candidates in different orders
        //       for two elements with the same matched set).
        //   (b) FNV-1a chain mixing the matchesHash with parent's
        //       MatchedKey and state digest. This is sequenced (not
        //       commutative) so an empty-matches element CAN'T collide
        //       with its parent — empty matchesHash = 0 still feeds
        //       through the chain producing a distinct key from the
        //       parent's own.
        //
        // Returns 0 when the matched set is non-shareable: any
        // attr() in any value, or parent itself wasn't cached (parent's
        // MatchedKey == 0 but parentStyle != null implies parent opted
        // out — we must too, to avoid colliding across uncached parents).
        ulong TryComputeMatchedKey(List<MatchedDeclaration> matches, ComputedStyle parentStyle, Element element, IElementStateProvider state) {
            if (matches == null) return 0;
            // Parent opted out → can't cache descendants either, otherwise
            // two opted-out parents' descendants would share a cache slot.
            if (parentStyle != null && parentStyle.MatchedKey == 0) return 0;
            ulong matchesHash = 0;
            for (int i = 0; i < matches.Count; i++) {
                var d = matches[i].Declaration;
                if (d == null) return 0;
                var vt = d.ValueText;
                if (vt != null && vt.IndexOf("attr(", System.StringComparison.Ordinal) >= 0) return 0;
                ulong id = (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d);
                var m = matches[i];
                ulong mix = id ^ ((ulong)m.Specificity.A * 73UL)
                              ^ ((ulong)m.Specificity.B * 257UL)
                              ^ ((ulong)m.Specificity.C * 1031UL)
                              ^ ((ulong)m.LayerOrdinal * 4099UL);
                // Knuth multiplicative constant for additional bit
                // dispersion before commutative XOR fold.
                matchesHash ^= mix * 2654435761UL;
            }
            ulong parentKey = parentStyle != null ? parentStyle.MatchedKey : 0UL;
            ulong stateDigest = (ulong)ResolveStateDigest(element, state);
            // FNV-1a chain: each step is sequenced, so {matchesHash=0,
            // parent=X, state=0} produces a key DISTINCT from {matchesHash=
            // X, parent=0, state=0}. Without the chain step, an empty-
            // matches child collided with its parent (XOR-ing with 0 is
            // identity). The chain also makes state-only changes produce
            // visibly different keys.
            ulong h = 14695981039346656037UL;
            h ^= matchesHash; h *= 1099511628211UL;
            h ^= parentKey;   h *= 1099511628211UL;
            h ^= stateDigest; h *= 1099511628211UL;
            if (h == 0) h = 1;
            return h;
        }

        void CollectMatchesManaged(Element element, IElementStateProvider state, List<MatchedDeclaration> matches) {
            for (int ri = 0; ri < compiled.Count; ri++) {
                var rule = compiled[ri];
                if (rule.Media != null && !rule.Media.Evaluate(mediaContext)) continue;
                // B1 fix: evaluate all nested @container conditions (full chain).
                if (!ContainerChainMatches(element, ri, rule)) continue;
                // B2 fix: evaluate all nested @scope windows (full chain).
                Element scopeRoot = null;
                if (!ScopeChainContains(element, state, rule, out scopeRoot)) continue;
                if (!SelectorMatcher.Matches(rule.Selector, element, state, scopeRoot)) continue;
                string selectorText = rule.Selector.SourceText;
                int declIndex = 0;
                foreach (var decl in rule.Declarations) {
                    matches.Add(new MatchedDeclaration(decl, rule.Origin, rule.Selector.Specificity, rule.SourceIndex, false, declIndex, rule.LayerOrdinal, selectorText));
                    declIndex++;
                }
            }
        }

        // Snapshot-driven collection. Compounds[] sequences (rule indices) returned by
        // SnapshotMatcher.MatchInto are aligned with `compiled` since we register them
        // in the same order at compile time. The managed verifier inside SnapshotMatcher
        // remains the source of truth for sibling combinators / pseudos / :nth — what
        // the snapshot path saves is the per-element O(rules) sweep through `compiled`.
        void CollectMatchesFromSnapshot(Element element, IElementStateProvider state, SnapshotPassState pass, int nodeId, List<MatchedDeclaration> matches) {
            // Both Scratch (IntsBuffer used inside SelectorIndex.CandidateSelectors) and
            // MatchedIndices (the rule-id output) are engine-owned and reset per element.
            pass.Scratch.Reset();
            pass.MatchedIndices.Clear();
            SnapshotMatcher.MatchInto(pass.Snapshot, nodeId, pass.Index, pass.Selectors, state, pass.Scratch, pass.MatchedIndices);
            var matched = pass.MatchedIndices;
            for (int k = 0; k < matched.Count; k++) {
                int ruleIdx = matched[k];
                var rule = compiled[ruleIdx];
                if (rule.Media != null && !rule.Media.Evaluate(mediaContext)) continue;
                // B1 fix: evaluate all nested @container conditions (full chain).
                if (!ContainerChainMatches(element, ruleIdx, rule)) continue;
                // B2 fix: evaluate all nested @scope windows (full chain).
                Element scopeRoot = null;
                if (!ScopeChainContains(element, state, rule, out scopeRoot)) continue;
                if (hasScopePseudo && !SelectorMatcher.Matches(rule.Selector, element, state, scopeRoot)) continue;
                string selectorText = rule.Selector.SourceText;
                int declIndex = 0;
                foreach (var decl in rule.Declarations) {
                    matches.Add(new MatchedDeclaration(decl, rule.Origin, rule.Selector.Specificity, rule.SourceIndex, false, declIndex, rule.LayerOrdinal, selectorText));
                    declIndex++;
                }
            }
        }

        // CSS Cascade L6 §7: returns true iff `element` is within every scope in
        // rule.ScopeChain (outermost-first). `scopeRoot` is set to the innermost
        // scope-root for SelectorMatcher's :scope pseudo expansion.
        bool ScopeChainContains(Element element, IElementStateProvider state, CompiledRule rule, out Element scopeRoot) {
            scopeRoot = null;
            var chain = rule.ScopeChain;
            if (chain == null || chain.Length == 0) return true;
            // Walk chain outermost-first; all must contain the element.
            // Last iteration sets scopeRoot to the innermost root.
            for (int i = 0; i < chain.Length; i++) {
                Element root = chain[i].FindScopeRoot(element, state);
                if (root == null) return false;
                scopeRoot = root;
            }
            return true;
        }

        // CSS Containment L3 §3: returns true iff every condition in the container
        // chain is satisfied by the appropriate ancestor in the element tree.
        // Chain is outermost-first; last entry is the innermost @container.
        // We evaluate from innermost to outermost, threading the matched box upward.
        bool ContainerChainMatches(Element element, int ruleIdx, CompiledRule rule) {
            var chain = rule.ContainerChain;
            if (chain == null || chain.Length == 0) return true;

            // Single-level: use the original cached ContainerMatches path.
            if (chain.Length == 1) {
                // CON-3: a null Condition means the @container prelude failed
                // to parse (EC11). The spec says the rule is silently dropped,
                // which on this side means "never matches". Returning true
                // here previously made unparseable @container rules apply
                // unconditionally — the inverse of the intended drop.
                if (chain[0].Condition == null) return false;
                return ContainerMatches(element, ruleIdx, rule);
            }

            // Multi-level nested @container. Walk from innermost to outermost.
            if (elementToBoxLookup == null) return false;
            var startBox = elementToBoxLookup(element);
            if (startBox == null) return false;

            Box searchAbove = startBox;
            for (int i = chain.Length - 1; i >= 0; i--) {
                var entry = chain[i];
                // CON-3 (multi-level mirror of the single-level fix above):
                // a null Condition means the @container at this level had an
                // unparseable prelude. Per EC11 the rule should be dropped
                // entirely, so any nested rules whose chain includes this
                // failed level must never match.
                if (entry.Condition == null) return false;
                Box matched = FindMatchingContainerBox(searchAbove, entry.Name);
                if (matched == null) return false;
                ContainerContext ctx = ContainerResolver.ContextFromBox(matched)
                    .WithStyleResolver(ContainerCustomPropertyResolver);
                if (!entry.Condition.Evaluate(ctx)) return false;
                searchAbove = matched;
            }
            return true;
        }

        // Walks box tree from startBox.Parent upward, returning the first box
        // whose container-type is non-None (matching optional name).
        static Box FindMatchingContainerBox(Box startBox, string name) {
            if (startBox == null) return null;
            var box = startBox.Parent;
            while (box != null) {
                if (ContainerResolver.BoxQualifiesAsContainer(box, name)) return box;
                box = box.Parent;
            }
            return null;
        }

        bool ContainerMatches(Element element, int ruleIdx, CompiledRule rule) {
            // Resolved ContainerContext is cached per (element, ruleIdx). The cache is
            // dropped wholesale on Apply(tracker) for any element marked Style|Layout|
            // Structure, and when ElementToBoxLookup is reassigned. v1: changes to a
            // container's size mid-frame are not detected mid-cascade — they require
            // the next layout pass to mark dependents dirty.
            if (!containerResolutionCache.TryGetValue(element, out var perElem)) {
                // PI5: pop a recycled inner dict if available — Clear()ed before
                // return to the pool so the popped instance is empty. Falls back
                // to a fresh dict only when the pool is cold (first cascade pass,
                // or after a HWM grew past previous use).
                if (containerInnerDictPool.Count > 0) {
                    perElem = containerInnerDictPool.Pop();
                    containerInnerDictPoolHits++;
                } else {
                    perElem = new Dictionary<int, ContainerContext>();
                    containerInnerDictAllocCount++;
                }
                containerResolutionCache[element] = perElem;
            }
            if (!perElem.TryGetValue(ruleIdx, out var ctx)) {
                ctx = elementToBoxLookup != null
                    ? ContainerResolver.Resolve(element, rule.ContainerName, elementToBoxLookup)
                    : ContainerContext.None;
                // CON-2: attach the style-query resolver so style(--prop: …)
                // can read the container element's computed custom properties.
                ctx = ctx.WithStyleResolver(ContainerCustomPropertyResolver);
                perElem[ruleIdx] = ctx;
            }
            return rule.Container.Evaluate(ctx);
        }

        // CON-2: resolves an element's computed custom property for style()
        // queries. Reads from the per-element style cache rather than re-running
        // Compute (which uses non-reentrant scratch state). During a Compute
        // pass the container is an ancestor of the queried element and was
        // already computed earlier in the same parent-chain walk, so its entry
        // is present. Cached as a field so WithStyleResolver doesn't allocate a
        // fresh delegate per (element, rule) context.
        Func<Element, string, string> containerCustomPropertyResolver;
        Func<Element, string, string> ContainerCustomPropertyResolver =>
            containerCustomPropertyResolver ??= ResolveContainerCustomProperty;

        string ResolveContainerCustomProperty(Element element, string property) {
            if (element == null || string.IsNullOrEmpty(property)) return null;
            if (cache.TryGetValue(element, out var entry) && entry.Style != null) {
                return entry.Style.Get(property);
            }
            return null;
        }

        // PA5: bitset-driven fill-pass. The previous implementation scanned
        // every registered property id (~190) per element on a cache miss, did
        // `style.Contains(id)` (an array load) and `CssProperties.Get(id)` (a
        // second array load), then either took the inherited value or the
        // initial value. We can split that into two cheaper passes:
        //
        //   1. INHERITED LEG. Walk `parent.bits & ~child.bits & inheritedMask`,
        //      word by word, and for each set bit copy the parent's value into
        //      the child. This skips the ~150+ non-inherited properties (no bit
        //      in `inheritedMask`) and all properties already set on the child
        //      (no bit in `~child.bits`). On a typical element only a handful
        //      of inherited properties (color, font-*, line-height, …) flow
        //      from the parent, so the inner loop runs ~6× instead of ~190×.
        //
        //   2. INITIAL LEG. Every other still-unoccupied slot gets the
        //      property's initial value. We still walk every id here, BUT each
        //      iteration is now a single Contains(id) check (the cheap branch)
        //      followed by the unavoidable Set(initial). The Set itself
        //      dominates the cost — pre-PA5 already paid the same Set — so the
        //      win is concentrated on the inherited leg.
        static void FillInherited(ComputedStyle style, ComputedStyle parent) {
            int count = CssProperties.RegisteredCount;
            ulong[] inheritedMask = CssProperties.GetInheritedMask();

            if (parent != null) {
                var childBits = style.OccupiedBits;
                var parentBits = parent.OccupiedBits;
                if (childBits != null && parentBits != null) {
                    int words = inheritedMask.Length;
                    if (parentBits.Length < words) words = parentBits.Length;
                    if (childBits.Length < words) words = childBits.Length;
                    for (int w = 0; w < words; w++) {
                        // Inherited slots set on the parent but NOT on the
                        // child. ~childBits[w] is fine: any "extra" bits past
                        // `count` are masked out by inheritedMask (only bits for
                        // real registered ids are set there).
                        ulong todo = parentBits[w] & ~childBits[w] & inheritedMask[w];
                        while (todo != 0) {
                            // Pop lowest set bit. (long)todo lets BitOperations
                            // give us a 0-63 lane index without needing .NET 7+
                            // intrinsics.
                            int lane = TrailingZeroCount64(todo);
                            todo &= todo - 1;
                            int id = (w << 6) | lane;
                            if (id >= count) continue;
                            if (parent.TryGet(id, out var inherited)) {
                                style.Set(id, inherited);
                            }
                        }
                    }
                }
            }

            style.FillInitials(count);
        }

        // Software trailing-zero count over 64 bits. Inlined so the JIT can
        // see the constant-folded constants. Used by the FillInherited bitset
        // walk to find the next inherited-but-unset property id. (We could
        // call System.Numerics.BitOperations.TrailingZeroCount(ulong), but
        // its availability is target-framework-dependent in this project —
        // a six-line DeBruijn fallback is portable and dominated entirely by
        // the surrounding Set(id, value) cost.)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int TrailingZeroCount64(ulong v) {
            if (v == 0UL) return 64;

            int count = 0;
            if ((v & 0xFFFFFFFFUL) == 0UL) { count += 32; v >>= 32; }
            if ((v & 0xFFFFUL) == 0UL) { count += 16; v >>= 16; }
            if ((v & 0xFFUL) == 0UL) { count += 8; v >>= 8; }
            if ((v & 0xFUL) == 0UL) { count += 4; v >>= 4; }
            if ((v & 0x3UL) == 0UL) { count += 2; v >>= 2; }
            if ((v & 0x1UL) == 0UL) count += 1;
            return count;
        }

        // Reused inline-style declaration buffer. The cascade pass walks
        // animated `<div style="...">` elements every frame; without this
        // pool every refresh allocates a fresh `List<Declaration>` (plus
        // the throwaway Stylesheet/Rule wrappers the old path produced).
        readonly List<Declaration> inlineDeclScratch = new(4);
        static readonly ParseOptions InlineParseOptions = new() { ThrowOnError = false };

        void AddInlineDeclarations(Element element, List<MatchedDeclaration> output) {
            string inline = element.GetAttribute("style");
            if (string.IsNullOrEmpty(inline)) return;
            inlineDeclScratch.Clear();
            // Direct declaration parsing — avoids the `*{...}` wrapping
            // path's Stylesheet + StyleRule + Selector("*") allocations.
            CssParser.ParseInlineDeclarations(inline, inlineDeclScratch, InlineParseOptions);
            // Inline styles always win over selector-based rules of any specificity. They
            // compare equal to one another by source order.
            int orderBase = 1_000_000_000;
            for (int i = 0; i < inlineDeclScratch.Count; i++) {
                output.Add(new MatchedDeclaration(inlineDeclScratch[i], DeclarationOrigin.Author, new Specificity(0, 0, 0), orderBase + i, true));
            }
        }

        static List<Element> BuildParentChain(Element element) {
            var chain = new List<Element>();
            BuildParentChainInto(element, chain);
            return chain;
        }

        // Fills `chain` with the element's ancestor chain root-first. Used
        // by the hot-path Compute caller via a reusable scratch list so we
        // skip the per-call \`new List<Element>()\` allocation.
        static void BuildParentChainInto(Element element, List<Element> chain) {
            var n = element as Node;
            while (n is Element e) {
                chain.Add(e);
                n = e.Parent;
            }
            chain.Reverse();
        }

        // Per CSS spec, when a shorthand declaration is present in the cascade it expands
        // into its longhands, with each missing longhand reset to its initial value. The
        // expanded longhands inherit the shorthand's origin, specificity, source index,
        // !important flag, and inline-ness — so a later explicit longhand at higher source
        // index naturally wins (and `border: solid; border-color: red` resolves to red,
        // because the shorthand's currentcolor reset is at the earlier source index).
        //
        // var()-bearing shorthand values are passed through verbatim so the existing
        // var-resolution pass can substitute them; the resulting longhand under the
        // shorthand name is still consulted by code paths that read the shorthand. Full
        // re-expansion of shorthand values containing var() into their longhands at
        // computed-value time is a v1 limitation tracked in PLAN.md §11.
        //
        // Both `matches` and `output` are engine-owned scratch buffers; `output` is
        // pre-cleared in CascadeScratch.ResetPerElement() before we're called.
        // PA: Declaration pool param. Each longhand produced by a shorthand
        // expander used to allocate a fresh `new Declaration(...)`; renting
        // from `pool` instead recycles instances across frames since
        // ComputedStyle stores raw value strings (never the Declaration
        // reference) and `perPropertyWinner`/`expanded` are scratch.
        static void ExpandShorthandMatchesInto(List<MatchedDeclaration> matches, List<MatchedDeclaration> output, CascadeScratch pool) {
            for (int i = 0; i < matches.Count; i++) {
                var m = matches[i];
                string name = m.Declaration.Property;
                if (CssProperties.IsCustomProperty(name)) {
                    output.Add(m);
                    continue;
                }
                if (!ShorthandRegistry.TryGet(name, out var expander)) {
                    output.Add(m);
                    continue;
                }
                string valueText = m.Declaration.ValueText ?? "";
                // CSS Values L4 §6.3 — defer shorthand expansion past var()/attr()
                // substitution: the expander cannot tokenize an unresolved reference.
                // Single-pass combined check: '(' guard short-circuits before
                // either OrdinalIgnoreCase scan (most shorthand values have no parens).
                if (ContainsSubstitutionMarker(valueText)) {
                    output.Add(m);
                    continue;
                }
                bool emittedAny = false;
                foreach (var kv in expander.Expand(valueText)) {
                    var sub = pool.RentDeclaration(kv.Key, kv.Value, m.Declaration.Important);
                    // Propagate the source declaration's LayerOrdinal — the
                    // 6-arg ctor defaults it to Unlayered, which silently
                    // moved every shorthand-expanded longhand (margin,
                    // padding, border, font, ...) into the unlayered tier
                    // for cascade ordering. Authors using @layer to
                    // override base shorthands then saw their override
                    // tied with (or losing to) lower-precedence layers.
                    output.Add(new MatchedDeclaration(sub, m.Origin, m.Specificity, m.SourceIndex, m.IsInline, m.InRuleIndex, m.LayerOrdinal, m.SelectorText));
                    emittedAny = true;
                }
                // Malformed shorthand with no var() → no longhands emitted; the cascade
                // keeps prior values for those longhands and the shorthand itself is
                // dropped (so style.Get(shorthand) returns initial).
                if (!emittedAny) continue;
            }
        }

        // Returns true when `s` contains var() or attr(), meaning shorthand
        // expansion must be deferred to computed-value time (var()/attr() not
        // yet substituted). Single-pass implementation: the '(' guard makes
        // the common case (no parens) a single cheap ordinal scan instead of
        // two OrdinalIgnoreCase IndexOf calls.
        //
        // CSS Values L4 §6.2 (var) / §6.3 (attr).
        static bool ContainsSubstitutionMarker(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.IndexOf('(') < 0) return false;
            return s.IndexOf("var(", System.StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("attr(", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // CSS Color Adjustment 1 §3 — resolve the EFFECTIVE color scheme
        // for `light-dark()` on this element. Priority order:
        //   1. Element's `color-scheme` property (inherited, registered in
        //      #251 follow-up). The `only` keyword forces the listed
        //      scheme(s) regardless of user preference.
        //   2. MediaContext.ColorScheme — the host application's resolved
        //      user-pref scheme.
        // Fixed in #257. Without this, every element saw the global
        // mediaContext scheme regardless of its own `color-scheme: dark`
        // declaration.
        static Weva.Css.Media.ColorScheme ResolveEffectiveColorScheme(
            ComputedStyle style, Weva.Css.Media.MediaContext mediaContext) {
            if (style == null) return mediaContext.ColorScheme;
            string raw = style.Get("color-scheme");
            if (string.IsNullOrEmpty(raw) || raw == "normal") return mediaContext.ColorScheme;
            // `color-scheme: only dark` / `only light` forces the listed
            // scheme regardless of MediaContext.
            bool isOnly = raw.IndexOf("only", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasDark = raw.IndexOf("dark", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasLight = raw.IndexOf("light", System.StringComparison.OrdinalIgnoreCase) >= 0;
            // Single keyword (or `only <keyword>`) — that's the choice.
            if (isOnly || (hasDark ^ hasLight)) {
                return hasDark ? Weva.Css.Media.ColorScheme.Dark : Weva.Css.Media.ColorScheme.Light;
            }
            // Both `light dark` (without `only`) — let MediaContext pick.
            if (hasDark && hasLight) return mediaContext.ColorScheme;
            return mediaContext.ColorScheme;
        }

        // Cached Comparison<T> delegate for the cascade comparator. A method-
        // group conversion at every Sort() call allocates a fresh ~32-byte
        // delegate; this static instance reuses the same one across every
        // ComputeFor / ComputeBackdrop / ComputePseudoElement.
        static readonly System.Comparison<MatchedDeclaration> CompareForCascadeDelegate = CompareForCascade;

        static int CompareForCascade(MatchedDeclaration x, MatchedDeclaration y) {
            // Earlier in the list = lower precedence; later = higher (winner).
            // Important is the dominant axis. Within an importance class, the origin
            // ordering flips between normal and important per the spec.
            if (x.Declaration.Important != y.Declaration.Important) {
                return x.Declaration.Important ? 1 : -1;
            }
            if (x.Declaration.Important) {
                int oimp = CompareImportantOrigin(x.Origin, y.Origin);
                if (oimp != 0) return oimp;
            } else {
                int oimp = CompareNormalOrigin(x.Origin, y.Origin);
                if (oimp != 0) return oimp;
            }
            // CSS Cascade and Inheritance Module Level 5 §6.4.1 step 4 — layer
            // ordering. Inline declarations carry CssLayer.UnlayeredOrdinal
            // (treated as unlayered for the purposes of the layer axis).
            //   Normal: later layer (higher ordinal) wins; unlayered (UnlayeredOrdinal)
            //           outranks every layered rule. Inline declarations bypass
            //           the layer axis altogether — an inline rule beats any
            //           layered rule via the post-layer "inline" tiebreak even
            //           when the layered rule has a more-specific selector.
            //   !important: REVERSED per spec (§6.4.1 step 5) — earlier layer
            //                wins; unlayered (including inline !important) LOSES
            //                to layered !important. We therefore MUST apply the
            //                layer comparison even when one side is inline.
            if (x.Declaration.Important) {
                // !important: layer axis applies regardless of inline-ness.
                // Inline !important carries UnlayeredOrdinal so any layered
                // !important (lower ordinal) outranks it.
                if (x.LayerOrdinal != y.LayerOrdinal) {
                    return y.LayerOrdinal.CompareTo(x.LayerOrdinal);
                }
            } else {
                // Normal: inline bypasses the cascade-layer axis (post-layer
                // "inline" tiebreak below resolves the winner instead).
                if (!x.IsInline && !y.IsInline) {
                    if (x.LayerOrdinal != y.LayerOrdinal) {
                        return x.LayerOrdinal.CompareTo(y.LayerOrdinal);
                    }
                }
            }
            if (x.IsInline != y.IsInline) return x.IsInline ? 1 : -1;
            int spec = x.Specificity.CompareTo(y.Specificity);
            if (spec != 0) return spec;
            int si = x.SourceIndex.CompareTo(y.SourceIndex);
            if (si != 0) return si;
            return x.InRuleIndex.CompareTo(y.InRuleIndex);
        }

        static int CompareNormalOrigin(DeclarationOrigin a, DeclarationOrigin b) {
            // UA < User < Author for normal declarations.
            return ((int)a).CompareTo((int)b);
        }

        static int CompareImportantOrigin(DeclarationOrigin a, DeclarationOrigin b) {
            // Author < User < UA for !important.
            return ((int)b).CompareTo((int)a);
        }

        // CSS Containment L3 §3 — one entry in a nested @container chain.
        // The chain array in CompiledRule is ordered outermost-first.
        // ContainerChainMatches walks it from innermost (last) to outermost (first)
        // so each condition is evaluated against the right ancestor in the box tree.
        readonly struct ContainerChainEntry {
            public readonly ContainerQueryList Condition;
            public readonly string Name;
            public ContainerChainEntry(ContainerQueryList condition, string name) {
                Condition = condition;
                Name = name;
            }
        }

        sealed class CompiledRule {
            public CompiledSelector Selector { get; }
            public List<Declaration> Declarations { get; }
            public DeclarationOrigin Origin { get; }
            public int SourceIndex { get; }
            public MediaQueryList Media { get; }
            // Innermost @container condition (legacy single-level fast path).
            public ContainerQueryList Container { get; }
            public string ContainerName { get; }
            // CSS Containment L3 §3 — full chain for nested @container rules.
            // Outermost-first; last entry matches Container/ContainerName above.
            // Null or length-0 when no @container applies; length-1 for a single
            // (non-nested) @container rule.
            public ContainerChainEntry[] ContainerChain { get; }
            // CSS Cascade Module Level 5 §6.4.2 — layer ordinal recorded at
            // compile time. Unlayered rules carry CssLayer.UnlayeredOrdinal so
            // they outrank every named/anonymous layer.
            public int LayerOrdinal { get; }
            // Innermost @scope context (legacy single-level fast path).
            public ScopeContext Scope { get; }
            // CSS Cascade L6 §7 — full chain for nested @scope rules.
            // Outermost-first; last entry matches Scope above.
            // Null or length-0 when not inside any @scope; length-1 for a single
            // (non-nested) @scope rule.
            public ScopeContext[] ScopeChain { get; }

            public CompiledRule(CompiledSelector selector, List<Declaration> declarations, DeclarationOrigin origin, int sourceIndex, MediaQueryList media)
                : this(selector, declarations, origin, sourceIndex, media, null, null, CssLayer.UnlayeredOrdinal, null, null, null) { }

            public CompiledRule(CompiledSelector selector, List<Declaration> declarations, DeclarationOrigin origin, int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName)
                : this(selector, declarations, origin, sourceIndex, media, container, containerName, CssLayer.UnlayeredOrdinal, null, null, null) { }

            public CompiledRule(CompiledSelector selector, List<Declaration> declarations, DeclarationOrigin origin, int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal)
                : this(selector, declarations, origin, sourceIndex, media, container, containerName, layerOrdinal, null, null, null) { }

            public CompiledRule(CompiledSelector selector, List<Declaration> declarations, DeclarationOrigin origin, int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal, ScopeContext scope)
                : this(selector, declarations, origin, sourceIndex, media, container, containerName, layerOrdinal, scope, null, null) { }

            public CompiledRule(CompiledSelector selector, List<Declaration> declarations, DeclarationOrigin origin, int sourceIndex, MediaQueryList media, ContainerQueryList container, string containerName, int layerOrdinal, ScopeContext scope, ContainerChainEntry[] containerChain, ScopeContext[] scopeChain) {
                Selector = selector;
                // Pre-expand shorthands at compile time. Authors write
                // `.skill-slot-cd-sweep { inset: 0; }` and the runtime cascade
                // walked ShorthandRegistry every cache miss to turn `inset: 0`
                // into top/right/bottom/left longhands. The text never changes
                // — the work is identical every frame, every element. Doing
                // it once at compile time hands a longhand-only declaration
                // list to the cascade, so ExpandShorthandMatchesInto can
                // straight-passthrough rule matches without entering the
                // expander. Shorthands whose value contains `var()` are left
                // intact — they need post-var() resolution at runtime; same
                // for unknown property names. Inline declarations are still
                // expanded per-frame in AddInlineDeclarations (they're
                // authored per-frame and don't go through this path).
                Declarations = PreExpandShorthands(declarations);
                Origin = origin;
                SourceIndex = sourceIndex;
                Media = media;
                Container = container;
                ContainerName = containerName;
                ContainerChain = containerChain;
                LayerOrdinal = layerOrdinal;
                Scope = scope;
                ScopeChain = scopeChain;
            }

            static List<Declaration> PreExpandShorthands(List<Declaration> source) {
                if (source == null || source.Count == 0) return source;
                // Quick scan: any shorthand without var() or attr()? If not,
                // return the source list unchanged (no work, no allocation).
                bool anyToExpand = false;
                for (int i = 0; i < source.Count; i++) {
                    var d = source[i];
                    if (d.Property == null) continue;
                    if (CssProperties.IsCustomProperty(d.Property)) continue;
                    if (!ShorthandRegistry.TryGet(d.Property, out _)) continue;
                    string v = d.ValueText ?? "";
                    // CSS Values L4 §6.3 — defer past var()/attr() substitution.
                    // Combined single-pass check: '(' guard avoids both scans
                    // for values with no parens (the common case).
                    if (ContainsSubstitutionMarker(v)) continue;
                    anyToExpand = true;
                    break;
                }
                if (!anyToExpand) return source;
                var output = new List<Declaration>(source.Count + 8);
                for (int i = 0; i < source.Count; i++) {
                    var d = source[i];
                    if (d.Property != null
                        && !CssProperties.IsCustomProperty(d.Property)
                        && ShorthandRegistry.TryGet(d.Property, out var expander)) {
                        string v = d.ValueText ?? "";
                        if (!ContainsSubstitutionMarker(v)) {
                            bool emitted = false;
                            foreach (var kv in expander.Expand(v)) {
                                output.Add(new Declaration(kv.Key, kv.Value, d.Important));
                                emitted = true;
                            }
                            if (emitted) continue;
                            // Expansion returned nothing — keep the original
                            // shorthand. Matches the runtime path where a
                            // malformed shorthand silently drops longhands
                            // but the cascade still sees the original entry.
                        }
                    }
                    output.Add(d);
                }
                return output;
            }
        }

        readonly struct StyleCacheEntry {
            public readonly IncrementalCacheKey Key;
            public readonly ComputedStyle Style;
            // Digest of layout-affecting property values captured at write
            // time. Survives same-element ComputedStyle recycling — once we
            // reuse the previous style as the recyclable backing, the in-
            // memory values are overwritten before LayoutAffectingProperty
            // Changed runs, so a fresh enumeration would see "no diff". The
            // digest is computed before recycling and compared against a
            // freshly computed digest after the new cascade, restoring the
            // before/after invariant the diff needs.
            public readonly ulong LayoutDigest;

            public StyleCacheEntry(IncrementalCacheKey key, ComputedStyle style, ulong layoutDigest) {
                Key = key;
                Style = style;
                LayoutDigest = layoutDigest;
            }
        }

        // EC11 — observability for the four by-design parse-exception drops in
        // the cascade (@media, @container condition, @container prelude,
        // selector). The cascade ignores parse-failed rules per L4; this helper
        // surfaces them once per malformed text so a CSS typo is debuggable
        // without changing behavior. Process-static dedupe mirrors the
        // ColorResolver DD2/DD3 pattern.
        //
        // Dedupe key shape: "<source>:" + rawText. Two different rules with
        // the same broken text dedupe to one warning, which matches "one
        // warning per kind of failure" in UICssDiagnostics's own contract.
        static readonly HashSet<string> s_WarnedCascadeKeys = new HashSet<string>();

        static void WarnCascadeParseFailure(string source, string raw, Exception ex) {
            string key = source + ":" + (raw ?? "");
            lock (s_WarnedCascadeKeys) {
                if (!s_WarnedCascadeKeys.Add(key)) return;
            }
            UICssDiagnostics.Warn(
                source,
                "parse failed (" + (ex?.GetType().Name ?? "Exception") +
                "); rule dropped per CSS Cascade L4. Offending text: '" +
                (raw ?? "") + "'");
        }

        // Test hook — wipes the dedupe set so a re-running test can observe a
        // warning that was already emitted by an earlier test in the same
        // session. Not part of the production contract.
        internal static void ResetWarnings_TestOnly() {
            lock (s_WarnedCascadeKeys) s_WarnedCascadeKeys.Clear();
            UICssDiagnostics.ResetForTests();
        }
    }
}
