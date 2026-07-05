using System;
using System.Collections.Generic;
using Weva.Animation;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Css.Animation {
    /// <summary>
    /// Drives CSS transitions and animations for a document: resolves keyframes against the
    /// cascade, ticks running records each frame, and composes per-element animated overrides
    /// for layout/paint to consume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>DOM-mutation subscription (<c>attachedDoc</c> / <c>mutationListener</c>).</b>
    /// The eight element-keyed dictionaries (<c>transitions</c> / <c>animations</c> /
    /// <c>*ByElement</c> / <c>animatedElements</c> / <c>transitioningElements</c> /
    /// <c>elementSpecsCache</c> / <c>composedCache</c>) all hold strong Element references;
    /// without a removal hook, an Element with <c>animation-iteration-count: infinite</c>
    /// removed from the tree mid-animation stays pinned forever because the per-tick sweep
    /// at <c>TickInternal</c> only deletes records by (Element, string) key when the
    /// animation NATURALLY completes. Mirrors the EventDispatcher / FormControlsRegistry /
    /// BindingSet subscription pattern. See MS2 in CODE_AUDIT_FINDINGS.md.
    /// </para>
    /// <para>
    /// <b>Element-indexed mirror dicts (<c>animationsByElement</c> / <c>transitionsByElement</c>).</b>
    /// <c>Compose()</c> previously walked the WHOLE <c>animations</c> / <c>transitions</c>
    /// dictionary and filtered by element, making it O(total_active) per call. On a grid
    /// where every tile has its own keyframe animation and Compose is called once per tile
    /// per frame, that's O(N²) — measured at ~4600 dict iterations per frame on the 64-tile
    /// demo. These mirror dicts make Compose's per-element walk O(per-element_active)
    /// instead. Maintained alongside the primary dicts at every Add / Remove site.
    /// </para>
    /// </remarks>
    public sealed class CssAnimationRunner : IDisposable {
        readonly CascadeEngine cascade;
        readonly Func<Element, ComputedStyle> cachedStyleForInvalidation;
        readonly KeyframesResolver resolver;
        readonly IUIClock clock;
        // DOM-mutation subscription — see class <remarks> for why this exists.
        Document attachedDoc;
        Action<DomMutation> mutationListener;
        bool disposed;
        readonly Dictionary<(Element, string), RunningTransitionRecord> transitions = new();
        readonly Dictionary<(Element, string), RunningAnimationRecord> animations = new();
        // Element-indexed views of the two dicts above — see class <remarks>.
        readonly Dictionary<Element, List<RunningAnimationRecord>> animationsByElement = new();
        readonly Dictionary<Element, List<RunningTransitionRecord>> transitionsByElement = new();
        // Element-keyed sets mirror the (element, name)-keyed dicts above.
        // Compose's hot path checks "does this element have ANY active
        // animation or transition?" once per call — without these sets it
        // walked the full transitions and animations dicts on every miss
        // (1000 elements with 50 animating = ~50k dict iterations per
        // layout pass just to discover most elements aren't animated).
        // The sets make the check O(1).
        readonly HashSet<Element> animatedElements = new();
        readonly HashSet<Element> transitioningElements = new();
        public bool HasActiveCompositions => animatedElements.Count != 0 || transitioningElements.Count != 0;
        // Per-element spec-parse cache. The transition-*/animation-* longhand
        // strings rarely change after the initial cascade, but OnStyleChange
        // fires on every re-cascade — including the 60 Hz cascade misses
        // caused by animation overlays updating `transform`/`opacity`/etc.
        // Without this cache, every animated element re-parsed its spec
        // lists every frame and allocated ~14 transient List<>s per call
        // (5 in BuildFromLonghands, 8 in BuildAnimSpecsFromLonghands, 1
        // HashSet<string> in UpdateAnimationsFor). Cache hit returns the
        // previously-built immutable spec list with zero allocation.
        readonly Dictionary<Element, SpecsCache> elementSpecsCache = new();
        // Reusable scratch set for UpdateAnimationsFor's declared-name diff —
        // sized once, Clear()-and-fill on each call instead of allocating a
        // fresh HashSet per element per Tick. Single-threaded by contract.
        readonly HashSet<string> declaredNamesScratch = new();
        // P4: per-instance "done" sweep scratches for TickInternal. Pre-fix
        // TickInternal lazily allocated `List<(Element, string)>` (one for
        // transitions, one for animations) each frame any record finished —
        // bursty when many staggered animations end on the same tick. Hoisted
        // to instance fields; cleared at the entry to each sweep section,
        // filled during, never reallocated. 8 entries pre-sizes the typical
        // burst size (a few staggered tiles wrapping up in one frame).
        readonly List<(Element, string)> scratchDoneTransitions = new(8);
        readonly List<(Element, string)> scratchDoneAnimations = new(8);
        InvalidationTracker tracker;

        sealed class SpecsCache {
            // Snapshot of the input string instances used to build the cached
            // lists. Identity comparison is enough — the cascade keeps the
            // same string reference until a property actually changes value.
            public string TShorthand, TProperty, TDuration, TDelay, TEasing, TBehavior;
            public IReadOnlyList<TransitionSpec> Transitions;
            public string AName, AShorthand, ADuration, ADelay, AEasing, AIters, ADirs, AFills, APlays, AComps;
            public IReadOnlyList<AnimationSpec> Animations;
        }

        public CssAnimationRunner(CascadeEngine cascade, IEnumerable<Stylesheet> stylesheets, IUIClock clock) {
            this.cascade = cascade;
            this.cachedStyleForInvalidation = cascade != null ? cascade.GetCachedStyle : (Func<Element, ComputedStyle>)null;
            this.resolver = new KeyframesResolver(stylesheets);
            this.clock = clock ?? new SystemUIClock();
        }

        public KeyframesResolver KeyframesResolver => resolver;

        public LengthContext LengthContext { get; set; } = LengthContext.Default;

        // When set, each Tick that updates a running transition or animation marks the
        // affected element with InvalidationKind.Paint so paint reruns next frame.
        public InvalidationTracker InvalidationTracker {
            get => tracker;
            set => tracker = value;
        }

        public int RunningTransitionCount => transitions.Count;
        public int RunningAnimationCount => animations.Count;

        // Test-only accessors for the MS2 leak-regression suite. Lets the
        // tests assert that DOM removal compacts every element-keyed
        // dictionary, not just the two exposed via the Running* counts.
        // Kept `internal` so production code cannot grow a dependency on
        // these dictionaries' internal shapes.
        internal int AnimationsByElementCount => animationsByElement.Count;
        internal int TransitionsByElementCount => transitionsByElement.Count;
        internal int AnimatedElementsCount => animatedElements.Count;
        internal int TransitioningElementsCount => transitioningElements.Count;
        internal int ElementSpecsCacheCount => elementSpecsCache.Count;
        internal int ComposedCacheCount => composedCache.Count;
        internal bool ContainsElementInAnyDictionary(Element e) {
            if (e == null) return false;
            if (animatedElements.Contains(e)) return true;
            if (transitioningElements.Contains(e)) return true;
            if (animationsByElement.ContainsKey(e)) return true;
            if (transitionsByElement.ContainsKey(e)) return true;
            if (elementSpecsCache.ContainsKey(e)) return true;
            if (composedCache.ContainsKey(e)) return true;
            foreach (var k in animations.Keys) if (k.Item1 == e) return true;
            foreach (var k in transitions.Keys) if (k.Item1 == e) return true;
            return false;
        }

        public bool HasRunningAnimations(Element e) {
            if (e == null) return false;
            return transitioningElements.Contains(e) || animatedElements.Contains(e);
        }

        // True when OnStyleChange will need the element's PREVIOUS computed
        // style intact to detect a transition start, so the cascade must NOT
        // recycle previousStyle in place for this element (audit C4). Only the
        // transition path reads `previous`; UpdateAnimationsFor reads only
        // `current`, so a purely-animated (or static) element is safe to
        // recycle. We keep `previous` when the element is mid-transition or has
        // parsed transition specs from a prior cascade. ParseTransitionSpecsFor
        // returns null for the `all 0s ease 0s` initial value, so a non-empty
        // spec list means a real authored transition — we must NOT sniff the
        // `transition` shorthand string, whose initial computed value is
        // non-empty ("all 0s ...") for EVERY element. The lone uncovered case —
        // a transition FIRST added in the same cascade that also changes
        // another property — starts one frame late (next cascade the specs are
        // cached), an accepted, documented limitation.
        public bool WantsStyleDiff(Element element) {
            if (element == null) return false;
            if (transitioningElements.Contains(element)) return true;
            return elementSpecsCache.TryGetValue(element, out var sc)
                && sc.Transitions != null && sc.Transitions.Count > 0;
        }

        public void Stop(Element e) {
            if (e == null) return;
            RemoveElement(e);
        }

        public void StopAll() {
            transitions.Clear();
            animations.Clear();
            transitioningElements.Clear();
            animatedElements.Clear();
            // Mirror clears for the element-indexed views — without these,
            // Compose's per-element walk would still observe stale records.
            transitionsByElement.Clear();
            animationsByElement.Clear();
            elementSpecsCache.Clear();
            // Composed-style cache is element-keyed too — clearing the
            // primary dicts without this leaks every previously-animated
            // element's overlaid ComputedStyle.
            composedCache.Clear();
        }

        // Strips `e` from every element-keyed structure the runner owns
        // (eight in total: the (Element, string)-keyed transitions /
        // animations dicts, their element-indexed mirror lists
        // transitionsByElement / animationsByElement, the two
        // membership-set sets animatedElements / transitioningElements,
        // the elementSpecsCache, and the composedCache). The DOM-mutation
        // hook calls this for every Element in the removed subtree; Stop
        // routes through here too so the two cleanup paths stay aligned.
        //
        // Silently drops the state — no `animationcancel` / `transitioncancel`
        // events are dispatched (those are out-of-scope for v1 per
        // AuthoringGuide §16) and no MarkDirty is issued, since the element
        // is no longer in the tree and the cascade will not query it again.
        void RemoveElement(Element e) {
            if (e == null) return;
            // Drop element-indexed mirror lists first so the per-frame
            // Compose walk doesn't observe records mid-removal.
            transitionsByElement.Remove(e);
            animationsByElement.Remove(e);
            // RemoveByElement handles the (Element, string)-keyed primary
            // dicts and the membership sets in one pass each.
            RemoveByElement(transitions, e, transitioningElements);
            RemoveByElement(animations, e, animatedElements);
            elementSpecsCache.Remove(e);
            composedCache.Remove(e);
        }

        // Subscribes to the document's mutation events so element-removed
        // events compact the eight element-keyed dictionaries above. Pairs
        // with Dispose. Idempotent: calling twice with the same doc is a
        // no-op; calling with a different doc detaches the old subscription
        // first.
        public void AttachToDocument(Document doc) {
            if (disposed) throw new ObjectDisposedException(nameof(CssAnimationRunner));
            if (attachedDoc == doc) return;
            DetachFromDocument();
            if (doc == null) return;
            attachedDoc = doc;
            mutationListener = OnDomMutation;
            doc.Mutated += mutationListener;
        }

        void DetachFromDocument() {
            if (attachedDoc != null && mutationListener != null) {
                attachedDoc.Mutated -= mutationListener;
            }
            attachedDoc = null;
            mutationListener = null;
        }

        // Element-removed: walk the removed subtree top-down and drop every
        // descendant Element from the runner's internal state. Other
        // mutation kinds (ChildAdded, Attribute*, TextChanged) need no
        // action — animation state is only created via OnStyleChange and is
        // not keyed by attribute / text content.
        void OnDomMutation(DomMutation m) {
            if (disposed) return;
            if (m.Kind != DomMutationKind.ChildRemoved) return;
            RemoveSubtree(m.Subject);
        }

        void RemoveSubtree(Node root) {
            if (root == null) return;
            if (root is Element e) RemoveElement(e);
            var kids = root.Children;
            for (int i = 0; i < kids.Count; i++) RemoveSubtree(kids[i]);
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            DetachFromDocument();
            StopAll();
        }

        static void RemoveByElement<T>(Dictionary<(Element, string), T> map, Element e, HashSet<Element> set) {
            // The membership set is a superset of the map's elements (every
            // map write pairs with a set.Add), so an element outside the set
            // has nothing to remove. Without this, every style-changed element
            // WITHOUT animations scanned every running animation's key — a
            // 50-element hover re-cascade over 400 running animations paid
            // ~20k tuple compares for nothing.
            if (!set.Contains(e)) return;
            List<(Element, string)> kill = null;
            foreach (var k in map.Keys) {
                if (k.Item1 == e) {
                    kill ??= new List<(Element, string)>();
                    kill.Add(k);
                }
            }
            if (kill != null) foreach (var k in kill) map.Remove(k);
            // After bulk-removing, the element is no longer in any of this
            // map's keys, so it leaves the set unconditionally.
            set.Remove(e);
            // Per-element view cleanup is the caller's responsibility — see
            // Stop(Element) for the canonical drop, and the
            // animation-removed branch in UpdateAnimationsFor for the
            // narrower per-name case (which removes individual list entries
            // before falling through here).
        }

        public void OnStyleChange(Element element, ComputedStyle previous, ComputedStyle current) {
            if (element == null || current == null) return;

            // Transitions: a property in transition-property changed value -> start.
            // Id-keyed reads skip the string→id hashmap walk that Get(string)
            // does on every access — animation-* / transition-* longhands are
            // read on every style change, so the saving is per-element.
            string transitionText = current.Get(CssProperties.TransitionId) ?? current.Get(CssProperties.TransitionPropertyId);
            var specs = ParseTransitionSpecsForCached(element, current);

            if (specs != null && specs.Count > 0 && previous != null) {
                foreach (var spec in specs) {
                    if (spec.DurationSeconds <= 0 && spec.DelaySeconds <= 0) continue;
                    if (string.Equals(spec.Property, "all", StringComparison.OrdinalIgnoreCase)) {
                        foreach (var kv in current.Enumerate()) {
                            string prop = kv.Key;
                            if (!PropertyKindRegistry.IsAnimatable(prop)) continue;
                            if (PropertyKindRegistry.Of(prop) == PropertyKind.Discrete) continue;
                            string before = previous.Get(prop);
                            string now = kv.Value;
                            if (before == null || before == now) continue;
                            StartTransitionFor(element, prop, before, now, spec);
                        }
                    } else {
                        string prop = spec.Property;
                        if (!PropertyKindRegistry.IsAnimatable(prop)) continue;
                        // CSS Transitions L2 §3.1: discrete properties only participate
                        // in transitions when transition-behavior: allow-discrete is set.
                        // With the default "normal", a discrete-valued property change
                        // snaps immediately — no transition record is created.
                        if (PropertyKindRegistry.Of(prop) == PropertyKind.Discrete && !spec.AllowDiscrete) continue;
                        string before = previous.Get(prop);
                        string now = current.Get(prop);
                        if (before == null || before == now) continue;
                        StartTransitionFor(element, prop, before, now, spec);
                    }
                }
            }

            // Animations: animation-name list dictates which @keyframes are playing.
            UpdateAnimationsFor(element, current);
        }

        // Per-element cached entry point. Reads the 5 transition-* longhand
        // strings, identity-compares them against the previous build's
        // inputs, and returns the cached spec list on a hit. Cache miss
        // routes through the existing ParseTransitionSpecsFor.
        IReadOnlyList<TransitionSpec> ParseTransitionSpecsForCached(Element element, ComputedStyle style) {
            string shorthand = style.Get(CssProperties.TransitionId);
            string property = style.Get(CssProperties.TransitionPropertyId);
            string duration = style.Get(CssProperties.TransitionDurationId);
            string delay = style.Get(CssProperties.TransitionDelayId);
            string easing = style.Get(CssProperties.TransitionTimingFunctionId);
            string behavior = style.Get(CssProperties.TransitionBehaviorId);
            if (!elementSpecsCache.TryGetValue(element, out var cache)) {
                cache = new SpecsCache();
                elementSpecsCache[element] = cache;
            } else if (cache.Transitions != null
                && ReferenceEquals(cache.TShorthand, shorthand)
                && ReferenceEquals(cache.TProperty, property)
                && ReferenceEquals(cache.TDuration, duration)
                && ReferenceEquals(cache.TDelay, delay)
                && ReferenceEquals(cache.TEasing, easing)
                && ReferenceEquals(cache.TBehavior, behavior)) {
                return cache.Transitions;
            }
            var built = ParseTransitionSpecsFor(style);
            cache.TShorthand = shorthand;
            cache.TProperty = property;
            cache.TDuration = duration;
            cache.TDelay = delay;
            cache.TEasing = easing;
            cache.TBehavior = behavior;
            cache.Transitions = built;
            return built;
        }

        IReadOnlyList<TransitionSpec> ParseTransitionSpecsFor(ComputedStyle style) {
            // Id-keyed reads of the transition longhands. Each Get(int) is an
            // O(1) array indexed read; the equivalent Get(string) does a
            // string→id Dictionary probe + the same array read.
            string shorthand = style.Get(CssProperties.TransitionId);
            // Only use shorthand if it has been authored beyond initial. The initial value is
            // "all 0s ease 0s" which produces zero-duration specs that no-op anyway.
            string property = style.Get(CssProperties.TransitionPropertyId);
            string duration = style.Get(CssProperties.TransitionDurationId);
            string delay = style.Get(CssProperties.TransitionDelayId);
            string easing = style.Get(CssProperties.TransitionTimingFunctionId);
            // CSS Transitions L2 §3.1: transition-behavior is always a longhand
            // (not part of the `transition` shorthand). Read it regardless of
            // whether we take the longhand or shorthand path below.
            string behavior = style.Get(CssProperties.TransitionBehaviorId);

            bool hasLonghand =
                (property != null && property != "all") ||
                (duration != null && duration != "0s") ||
                (delay != null && delay != "0s") ||
                (easing != null && easing != "ease");

            if (hasLonghand) {
                return BuildFromLonghands(property ?? "all", duration ?? "0s", delay ?? "0s", easing ?? "ease", behavior ?? "normal");
            }
            if (!string.IsNullOrEmpty(shorthand) && shorthand != "all 0s ease 0s") {
                // Shorthand path: apply transition-behavior from the longhand
                // (transition-behavior is never encoded in the shorthand value).
                return ApplyBehaviorToSpecs(TransitionShorthandParser.Parse(shorthand), behavior ?? "normal");
            }
            return null;
        }

        // Applies the transition-behavior longhand list to a pre-parsed spec list
        // from the shorthand path. The behavior list cycles per the CSS spec
        // (same as all other transition-* longhands). Returns the same list
        // when behavior is "normal" for all items (avoids allocation).
        static IReadOnlyList<TransitionSpec> ApplyBehaviorToSpecs(IReadOnlyList<TransitionSpec> specs, string behavior) {
            if (specs == null || specs.Count == 0) return specs;
            var behaviors = SplitCommaList(behavior);
            // Fast path: all items are "normal" (the default — no allocation needed).
            bool anyAllowDiscrete = false;
            for (int i = 0; i < specs.Count; i++) {
                string bv = Cycle(behaviors, i, "normal");
                if (string.Equals(bv, "allow-discrete", StringComparison.OrdinalIgnoreCase)) {
                    anyAllowDiscrete = true;
                    break;
                }
            }
            if (!anyAllowDiscrete) return specs;
            var result = new List<TransitionSpec>(specs.Count);
            for (int i = 0; i < specs.Count; i++) {
                var s = specs[i];
                string bv = Cycle(behaviors, i, "normal");
                bool ad = string.Equals(bv, "allow-discrete", StringComparison.OrdinalIgnoreCase);
                result.Add(new TransitionSpec(s.Property, s.DurationSeconds, s.DelaySeconds, s.Easing, ad));
            }
            return result;
        }

        static IReadOnlyList<TransitionSpec> BuildFromLonghands(string property, string duration, string delay, string easing, string behavior = "normal") {
            var props = SplitCommaList(property);
            var durs = SplitCommaList(duration);
            var dels = SplitCommaList(delay);
            var eass = SplitCommaList(easing);
            // CSS Transitions L2 §3.1: transition-behavior cycles the same way
            // as the other longhands. Each list entry is "normal" or "allow-discrete".
            var behs = SplitCommaList(behavior ?? "normal");

            var result = new List<TransitionSpec>();
            int n = Math.Max(props.Count, Math.Max(durs.Count, Math.Max(dels.Count, Math.Max(eass.Count, behs.Count))));
            for (int i = 0; i < n; i++) {
                string p = Cycle(props, i, "all");
                string d = Cycle(durs, i, "0s");
                string dl = Cycle(dels, i, "0s");
                string e = Cycle(eass, i, "ease");
                string bv = Cycle(behs, i, "normal");
                TransitionShorthandParser.TryParseTime(d, out double dur);
                TransitionShorthandParser.TryParseTime(dl, out double del);
                // CSS Easing L1 §2.1: when transition-timing-function is invalid
                // it falls back to the property's initial value, which is `ease`.
                EasingFunction ef = EaseEasing.Instance;
                try { ef = EasingParser.Parse(e); }
                catch (Exception ex) {
                    // EC4: by-design fallback to `ease` preserved (spec-mandated).
                    // Kept broad because EasingParser throws plain
                    // FormatException / ArgumentNullException from BCL, not a
                    // typed parse-exception. Warn once per offending string so
                    // a typo in transition-timing-function is visible.
                    WarnEasingParseFailure(e, ex);
                }
                bool allowDiscrete = string.Equals(bv, "allow-discrete", StringComparison.OrdinalIgnoreCase);
                result.Add(new TransitionSpec(p, dur, del, ef, allowDiscrete));
            }
            return result;
        }

        static List<string> SplitCommaList(string s) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0) {
                    list.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            list.Add(s.Substring(start).Trim());
            return list;
        }

        static string Cycle(List<string> list, int i, string fallback) {
            if (list.Count == 0) return fallback;
            return list[i % list.Count];
        }

        void StartTransitionFor(Element element, string prop, string from, string to, TransitionSpec spec) {
            var key = (element, prop);
            string effectiveFrom = from;
            string originalFrom = from;
            TransitionSpec effectiveSpec = spec;
            if (transitions.TryGetValue(key, out var existing) && !existing.Finished) {
                // Per CSS spec: re-target uses current interpolated value as new from.
                effectiveFrom = existing.CurrentText ?? from;
                originalFrom = existing.OriginalFromText ?? existing.FromText ?? from;
                // CSS Transitions L1 §3 "Faster reversing of interrupted
                // transitions": when the new target equals the original
                // `from` of the prior transition, shorten the new duration
                // by the reverse-progress of the interrupted transition so
                // the value arrives back at the start at the same wall-clock
                // time the original would have reached this interpolated
                // point.
                if (string.Equals(to, originalFrom, StringComparison.Ordinal)
                    && existing.Spec.DurationSeconds > 0) {
                    double reverseProgress = ComputeReverseProgress(
                        existing, effectiveFrom, originalFrom);
                    if (reverseProgress > 0 && reverseProgress < 1) {
                        double shortened = existing.Spec.DurationSeconds * (1 - reverseProgress);
                        effectiveSpec = new TransitionSpec(
                            spec.Property,
                            shortened,
                            spec.DelaySeconds,
                            spec.Easing);
                    }
                }
            }
            // Pre-parse both endpoints exactly once at the start of the
            // transition. The per-frame Tick path (TickInternal below)
            // re-runs the parser on every frame pre-fix — for a 1s color
            // transition at 60Hz that's 120 redundant CssValue.TryParse
            // calls. Now: 2 calls total, cached on the record.
            CssValue.TryParse(effectiveFrom, prop, out var fromParsed);
            CssValue.TryParse(to, prop, out var toParsed);
            var rec = new RunningTransitionRecord {
                Property = prop,
                FromText = effectiveFrom,
                ToText = to,
                FromParsed = fromParsed,
                ToParsed = toParsed,
                StartTimeSeconds = clock.NowSeconds,
                Spec = effectiveSpec,
                Kind = PropertyKindRegistry.Of(prop),
                CurrentText = effectiveFrom,
                Finished = false,
                OriginalFromText = originalFrom
            };
            // Replace any existing record for this (element, property) in the
            // element-indexed view so iteration order matches the primary dict.
            if (transitions.TryGetValue(key, out var prior)
                && transitionsByElement.TryGetValue(element, out var priorList)) {
                priorList.Remove(prior);
            }
            transitions[key] = rec;
            transitioningElements.Add(element);
            if (!transitionsByElement.TryGetValue(element, out var elList)) {
                elList = new List<RunningTransitionRecord>(2);
                transitionsByElement[element] = elList;
            }
            elList.Add(rec);
        }

        // Reverse-progress along the interrupted transition: prefers the
        // value-space ratio (current-from)/(to-from) for numeric properties
        // and falls back to the prior transition's elapsed-time progress
        // when the endpoints can't be parsed as scalars (colors, transforms).
        double ComputeReverseProgress(RunningTransitionRecord prior, string currentText, string priorFrom) {
            if (double.TryParse(currentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double cur)
                && double.TryParse(priorFrom, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f)
                && double.TryParse(prior.ToText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t)
                && Math.Abs(t - f) > 1e-9) {
                double p = (cur - f) / (t - f);
                if (p < 0) p = 0;
                if (p > 1) p = 1;
                return p;
            }
            double elapsed = clock.NowSeconds - prior.StartTimeSeconds - prior.Spec.DelaySeconds;
            if (elapsed <= 0 || prior.Spec.DurationSeconds <= 0) return 0;
            double tp = elapsed / prior.Spec.DurationSeconds;
            if (tp < 0) tp = 0;
            if (tp > 1) tp = 1;
            return tp;
        }

        void UpdateAnimationsFor(Element element, ComputedStyle current) {
            // Id-keyed reads for the animation-* longhands. See ParseTransitionSpecsFor.
            string name = current.Get(CssProperties.AnimationNameId);
            string anim = current.Get(CssProperties.AnimationId);
            bool hasLonghandName = !string.IsNullOrEmpty(name) && name != "none";
            bool hasShorthand = !string.IsNullOrEmpty(anim)
                && anim != "none 0s ease 0s 1 normal none running"
                && anim != "none";
            if (!hasLonghandName && !hasShorthand) {
                RemoveByElement(animations, element, animatedElements);
                // Drop the per-element view so Compose's lookup misses
                // cleanly for this element (mirror of the primary-dict
                // wipe above). See task #281 for the leak the by-element
                // views otherwise create at this and other Remove sites.
                animationsByElement.Remove(element);
                return;
            }

            // Cache miss path: re-parse via the long path. Hit path: reuse the
            // cached spec list — the parsed shape is invariant under the
            // animation overlay's transform/opacity updates.
            string duration = current.Get(CssProperties.AnimationDurationId);
            string delay = current.Get(CssProperties.AnimationDelayId);
            string easing = current.Get(CssProperties.AnimationTimingFunctionId);
            string iters = current.Get(CssProperties.AnimationIterationCountId);
            string dirs = current.Get(CssProperties.AnimationDirectionId);
            string fills = current.Get(CssProperties.AnimationFillModeId);
            string plays = current.Get(CssProperties.AnimationPlayStateId);
            string compositions = current.Get(CssProperties.AnimationCompositionId);
            if (!elementSpecsCache.TryGetValue(element, out var cache)) {
                cache = new SpecsCache();
                elementSpecsCache[element] = cache;
            }
            IReadOnlyList<AnimationSpec> specs;
            if (cache.Animations != null
                && ReferenceEquals(cache.AName, name)
                && ReferenceEquals(cache.AShorthand, anim)
                && ReferenceEquals(cache.ADuration, duration)
                && ReferenceEquals(cache.ADelay, delay)
                && ReferenceEquals(cache.AEasing, easing)
                && ReferenceEquals(cache.AIters, iters)
                && ReferenceEquals(cache.ADirs, dirs)
                && ReferenceEquals(cache.AFills, fills)
                && ReferenceEquals(cache.APlays, plays)
                && ReferenceEquals(cache.AComps, compositions)) {
                specs = cache.Animations;
            } else {
                if (hasLonghandName) {
                    // The cascade expands `animation` into longhands, then
                    // later/more-specific longhands override individual
                    // components. Use the computed longhand lists as the
                    // canonical source so declarations like
                    // `animation: star-pop ...; animation-delay: 180ms`
                    // affect the running instance instead of being ignored
                    // by reparsing the original shorthand.
                    specs = BuildAnimSpecsFromLonghands(current);
                } else if (hasShorthand) {
                    specs = AnimationShorthandParser.Parse(anim);
                    if (specs.Count == 0 || string.IsNullOrEmpty(specs[0].Name) || specs[0].Name == "none") {
                        specs = BuildAnimSpecsFromLonghands(current);
                    }
                } else {
                    specs = BuildAnimSpecsFromLonghands(current);
                }
                cache.AName = name;
                cache.AShorthand = anim;
                cache.ADuration = duration;
                cache.ADelay = delay;
                cache.AEasing = easing;
                cache.AIters = iters;
                cache.ADirs = dirs;
                cache.AFills = fills;
                cache.APlays = plays;
                cache.AComps = compositions;
                cache.Animations = specs;
            }

            // Remove records whose names no longer appear in the spec list.
            // Reuses the runner's scratch HashSet — Clear is O(Count), which
            // is bounded by the per-element animation-name count (typically 1).
            declaredNamesScratch.Clear();
            foreach (var s in specs) if (!string.IsNullOrEmpty(s.Name)) declaredNamesScratch.Add(s.Name);
            var declaredNames = declaredNamesScratch;
            List<(Element, string)> toRemove = null;
            foreach (var k in animations.Keys) {
                if (k.Item1 == element && !declaredNames.Contains(k.Item2)) {
                    toRemove ??= new List<(Element, string)>();
                    toRemove.Add(k);
                }
            }
            if (toRemove != null) {
                animationsByElement.TryGetValue(element, out var elAnimList);
                foreach (var k in toRemove) {
                    if (animations.TryGetValue(k, out var removedRec)) {
                        elAnimList?.Remove(removedRec);
                    }
                    animations.Remove(k);
                }
                // Drop the per-element list entirely when it empties so the
                // O(per-element) walk in Compose sees no list (TryGetValue
                // returns false → zero iterations).
                if (elAnimList != null && elAnimList.Count == 0) {
                    animationsByElement.Remove(element);
                    animatedElements.Remove(element);
                }
            }

            foreach (var spec in specs) {
                if (string.IsNullOrEmpty(spec.Name) || spec.Name == "none") continue;
                var key = (element, spec.Name);
                if (animations.TryGetValue(key, out var existing)) {
                    // Update spec (e.g. play-state change) without resetting start time.
                    if (existing.Paused && !spec.Paused) {
                        // Resume — shift StartTimeSeconds forward by the paused duration.
                        double pausedFor = clock.NowSeconds - existing.PausedAtSeconds;
                        existing.StartTimeSeconds += pausedFor;
                        existing.Paused = false;
                        // Rebuild instance with the new start time.
                        existing.Instance = BuildInstance(existing.Anim, spec, existing.StartTimeSeconds);
                    } else if (!existing.Paused && spec.Paused) {
                        existing.Paused = true;
                        existing.PausedAtSeconds = clock.NowSeconds;
                    }
                    existing.Spec = spec;
                    continue;
                }

                var kf = resolver.ResolveByName(spec.Name);
                if (kf == null) continue;
                // Synthesize implicit from/to from the element's CURRENT
                // computed style for every property the keyframes touch.
                // CSS Animations §3 says a missing 0%/100% keyframe takes
                // the value the property would have without the animation;
                // without this step a `@keyframes spin { to { rotate(360) } }`
                // sees an empty synthetic 0% keyframe and the sampler short-
                // circuits to the only defined endpoint, leaving the
                // animation visually stuck.
                kf = MaterializeImplicitKeyframes(kf, current);
                double startTime = clock.NowSeconds;
                var instance = BuildInstance(kf, spec, startTime);
                var rec = new RunningAnimationRecord {
                    Name = spec.Name,
                    Anim = kf,
                    Spec = spec,
                    Instance = instance,
                    StartTimeSeconds = startTime,
                    CurrentSample = null,
                    Paused = spec.Paused,
                    PausedAtSeconds = spec.Paused ? startTime : 0
                };
                animations[key] = rec;
                animatedElements.Add(element);
                if (!animationsByElement.TryGetValue(element, out var elAnimList)) {
                    elAnimList = new List<RunningAnimationRecord>(2);
                    animationsByElement[element] = elAnimList;
                }
                elAnimList.Add(rec);
            }
        }

        // Returns a copy of `source` whose 0% and 100% keyframes have an
        // entry for every property mentioned by any other keyframe — the
        // missing entries are filled from `baseStyle.Get(property)`. The
        // KeyframeAnimation type already inserts empty 0%/100% keyframes
        // when only `from`/`to` (or only a percentage) is authored; this
        // step turns those empty endpoints into proper "current style"
        // anchors so transform/length/color interpolation has both ends.
        //
        // We always clone (rather than mutating `source`) because
        // KeyframeAnimation is cached per @keyframes name and reused
        // across every element that references it — each element can
        // have its own base style so each gets its own materialized copy.
        static KeyframeAnimation MaterializeImplicitKeyframes(KeyframeAnimation source, ComputedStyle baseStyle) {
            if (source == null || source.Keyframes.Count == 0) return source;
            // Collect every property authored in any keyframe.
            var animatedProps = new HashSet<string>();
            foreach (var kf in source.Keyframes) {
                foreach (var p in kf.Properties.Keys) animatedProps.Add(p);
            }
            if (animatedProps.Count == 0) return source;
            // Clone keyframes (shallow on Position, deep on Properties so
            // we don't mutate the cached source). While cloning, resolve any
            // var() custom-property references in the authored keyframe values
            // against the element's computed style. Without this, a value like
            // `background-color: var(--red)` is carried through to the sampler
            // and the composed style as the literal string "var(--red)", which
            // the color parser rejects → the box paints transparent (showing
            // the page background as "black"). The cascade resolves var() for
            // ordinary declarations, but @keyframes values bypass that path, so
            // we substitute here — once per animation start, per element (each
            // element may resolve --custom differently). baseStyle carries the
            // inherited custom properties (same source the cascade reads).
            var cloned = new List<Keyframe>(source.Keyframes.Count);
            foreach (var kf in source.Keyframes) {
                var resolvedProps = new Dictionary<string, string>(kf.Properties.Count);
                foreach (var pv in kf.Properties) {
                    // Don't resolve a custom-property *declaration* inside a
                    // keyframe (`--foo: ...`); only substitute var() *uses* in
                    // ordinary property values.
                    resolvedProps[pv.Key] = pv.Key.StartsWith("--")
                        ? pv.Value
                        : VariableResolver.Resolve(pv.Value, baseStyle);
                }
                cloned.Add(new Keyframe(kf.Position, resolvedProps));
            }
            // Fill the 0% and 100% endpoints with base-style values for
            // any animated property that the authored keyframes left blank.
            Keyframe zero = cloned[0];
            Keyframe one = cloned[cloned.Count - 1];
            // The KeyframeAnimation ctor guarantees zero.Position <= 0 and
            // one.Position >= 1, so we always have an endpoint to fill.
            foreach (var p in animatedProps) {
                if (!zero.Properties.ContainsKey(p)) {
                    string v = baseStyle?.Get(p);
                    if (!string.IsNullOrEmpty(v)) zero.Properties[p] = v;
                }
                if (!one.Properties.ContainsKey(p)) {
                    string v = baseStyle?.Get(p);
                    if (!string.IsNullOrEmpty(v)) one.Properties[p] = v;
                }
            }
            return new KeyframeAnimation(source.Name, cloned);
        }

        static AnimationInstance BuildInstance(KeyframeAnimation kf, AnimationSpec spec, double startTime) {
            return new AnimationInstance(
                kf,
                spec.DurationSeconds,
                spec.DelaySeconds,
                spec.Easing,
                spec.IterationCount,
                spec.FillMode,
                spec.Direction,
                startTime);
        }

        IReadOnlyList<AnimationSpec> BuildAnimSpecsFromLonghands(ComputedStyle s) {
            // Id-keyed reads of the animation-* longhands. Avoids 8 string-keyed
            // Dictionary probes per element when an animation declaration is
            // rebuilt (every cascade-recompute that touches an animated element).
            var names = SplitCommaList(s.Get(CssProperties.AnimationNameId) ?? "none");
            var durs = SplitCommaList(s.Get(CssProperties.AnimationDurationId) ?? "0s");
            var dels = SplitCommaList(s.Get(CssProperties.AnimationDelayId) ?? "0s");
            var eass = SplitCommaList(s.Get(CssProperties.AnimationTimingFunctionId) ?? "ease");
            var iters = SplitCommaList(s.Get(CssProperties.AnimationIterationCountId) ?? "1");
            var dirs = SplitCommaList(s.Get(CssProperties.AnimationDirectionId) ?? "normal");
            var fills = SplitCommaList(s.Get(CssProperties.AnimationFillModeId) ?? "none");
            var plays = SplitCommaList(s.Get(CssProperties.AnimationPlayStateId) ?? "running");
            // CSS Animations L2 §10: `animation-composition` longhand.
            // Default `replace` preserves L1 semantics.
            var comps = SplitCommaList(s.Get(CssProperties.AnimationCompositionId) ?? "replace");
            var result = new List<AnimationSpec>();
            for (int i = 0; i < names.Count; i++) {
                string name = names[i];
                if (string.IsNullOrEmpty(name) || name == "none") continue;
                TransitionShorthandParser.TryParseTime(Cycle(durs, i, "0s"), out double dur);
                TransitionShorthandParser.TryParseTime(Cycle(dels, i, "0s"), out double del);
                EasingFunction ef = EaseEasing.Instance;
                string easRaw = Cycle(eass, i, "ease");
                try { ef = EasingParser.Parse(easRaw); }
                catch (Exception ex) {
                    // EC4: see TransitionSpec parse site above; same by-design
                    // fallback applies to animation-timing-function.
                    WarnEasingParseFailure(easRaw, ex);
                }
                string iterText = Cycle(iters, i, "1");
                double iterCount = 1;
                if (string.Equals(iterText, "infinite", StringComparison.OrdinalIgnoreCase)) {
                    iterCount = double.PositiveInfinity;
                } else if (double.TryParse(iterText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) && n >= 0) {
                    // CSS Animations L1 §3.5 (animation-iteration-count):
                    // "Negative numbers are invalid." Without the n >= 0 guard
                    // a negative count flowed through to endTime computation
                    // (start + delay + duration * n), placing endTime before
                    // start, so the sweep terminated the animation on its
                    // first tick — author sees no playback at all instead of
                    // the spec-required initial value (1) being used.
                    iterCount = n;
                }
                PlaybackDirection dir = PlaybackDirection.Normal;
                switch (Cycle(dirs, i, "normal")) {
                    case "reverse": dir = PlaybackDirection.Reverse; break;
                    case "alternate": dir = PlaybackDirection.Alternate; break;
                    case "alternate-reverse": dir = PlaybackDirection.AlternateReverse; break;
                }
                FillMode fm = FillMode.None;
                switch (Cycle(fills, i, "none")) {
                    case "forwards": fm = FillMode.Forwards; break;
                    case "backwards": fm = FillMode.Backwards; break;
                    case "both": fm = FillMode.Both; break;
                }
                bool paused = Cycle(plays, i, "running") == "paused";
                AnimationCompositionMode comp = AnimationCompositionMode.Replace;
                switch (Cycle(comps, i, "replace")) {
                    case "add": comp = AnimationCompositionMode.Add; break;
                    case "accumulate": comp = AnimationCompositionMode.Accumulate; break;
                }
                result.Add(new AnimationSpec(name, dur, del, ef, iterCount, dir, fm, paused, comp));
            }
            return result;
        }

        public void Tick(double currentTimeSeconds) {
            TickInternal(currentTimeSeconds, tracker);
        }

        // Lifecycle-driven overload: the per-frame orchestrator passes its
        // tracker explicitly. The invalidation kind is now computed
        // per-animation by inspecting which properties the sample
        // interpolates — transform/opacity/color-only samples mark Paint,
        // length/font-size samples mark Paint | Layout. Pre-fix this method
        // unconditionally marked Layout for every animation tick, which
        // forced a full IFC + grid + flex re-layout every frame on
        // animated UIs even when the animation only touched transform —
        // the dominant ~30ms CPU cost on match3 with 5 paint-only
        // animations live.
        public void Tick(double currentTimeSeconds, InvalidationTracker frameTracker) {
            TickInternal(currentTimeSeconds, frameTracker);
        }

        // Returns the InvalidationKind needed for the set of properties in
        // `props`. Lengths (width/height/padding/etc), the layout-affecting
        // Numbers (flex-grow/shrink, order), and the layout-affecting
        // Discretes (display, position, flex-direction, etc.) force a full
        // re-layout. Transform / Color / Number opacity / paint-only
        // Discretes (text-align, etc.) need only paint.
        //
        // Concrete Dictionary type — taking `IEnumerable<string>` forced the
        // foreach to boxx the KeyCollection's struct enumerator into an
        // IEnumerator<string> on every call (~40 B / call × 60 anims / frame
        // = ~2.7 KB / frame on the gem-grid scene). The concrete type lets
        // the foreach bind directly to KeyCollection.Enumerator (struct).
        static InvalidationKind ClassifyInvalidation(Dictionary<string, string> sample,
                                                     Dictionary<string, CssValue> typedSample) {
            bool layout = false;
            bool sawAny = false;
            bool allWrapper = true;
            if (sample != null) {
                foreach (var p in sample.Keys) {
                    sawAny = true;
                    if (!IsWrapperOnlyProperty(p)) allWrapper = false;
                    var kind = PropertyKindRegistry.Of(p);
                    if (kind == PropertyKind.Length) { layout = true; break; }
                    if (IsLayoutAffectingDiscreteOrNumber(p)) { layout = true; break; }
                }
            }
            // The typed overlay path (transform / opacity fast paths in
            // AnimationInstance) leaves sampleResult EMPTY for its keys and
            // records them in TypedSample instead — classification must see
            // both or a transform-only animation reads as "no properties".
            if (!layout && typedSample != null) {
                foreach (var p in typedSample.Keys) {
                    sawAny = true;
                    if (!IsWrapperOnlyProperty(p)) allWrapper = false;
                    var kind = PropertyKindRegistry.Of(p);
                    if (kind == PropertyKind.Length) { layout = true; break; }
                    if (IsLayoutAffectingDiscreteOrNumber(p)) { layout = true; break; }
                }
            }
            if (layout) return InvalidationKind.Paint | InvalidationKind.Layout;
            // Wrapper-only ticks (transform / translate / rotate / scale /
            // opacity) don't touch the element's DECORATION output — the
            // painter re-resolves wrappers fresh every frame and replays the
            // cached decoration commands. Mark Composite (subtree composition
            // changed: snapshots covering this element must not replay) but
            // NOT Paint, so BoxToPaintConverter.Apply keeps the PaintBoxCache.
            // particles.html: 420 wrapper-only animations rebuilt their
            // radial-gradient decorations EVERY frame through the Paint mark.
            if (sawAny && allWrapper) return InvalidationKind.Composite;
            return InvalidationKind.Paint;
        }

        // Properties consumed exclusively by the painter's WRAPPER emission
        // (EmitWrappersFresh: PushTransform / PushOpacity), never by
        // EmitDecorations. `filter` is deliberately NOT here: a lone
        // `drop-shadow()` renders via the synthetic-shadow path inside
        // EmitDecorations, and a `brightness()` can fold into paint colors —
        // both make cached decoration commands stale.
        static bool IsWrapperOnlyProperty(string prop) {
            switch (prop) {
                case "transform":
                case "translate":
                case "rotate":
                case "scale":
                case "opacity":
                    return true;
                default:
                    return false;
            }
        }

        static bool IsLayoutAffectingDiscreteOrNumber(string prop) {
            // Numbers + discretes that re-flow the box tree when they change.
            // The rest (opacity, z-index, color text-align, etc.) only need
            // a fresh paint walk. Keep this conservative — false positives
            // are cheaper than missing a true layout dependency, but
            // animated UIs see this list every frame so we want it tight.
            switch (prop) {
                case "flex-grow":
                case "flex-shrink":
                case "flex-basis":
                case "order":
                case "display":
                case "visibility":
                case "position":
                case "overflow":
                case "overflow-x":
                case "overflow-y":
                case "box-sizing":
                case "flex-direction":
                case "flex-wrap":
                case "justify-content":
                case "align-items":
                case "align-self":
                case "align-content":
                case "font-weight":
                case "font-style":
                case "font-variant":
                case "white-space":
                    return true;
                default:
                    return false;
            }
        }

        void TickInternal(double currentTimeSeconds, InvalidationTracker target) {
            if (transitions.Count > 0) {
                // P4: reuse instance scratch instead of `List<(Element,string)> done = null`
                // + lazy `done ??= new List<>()` per frame.
                var done = scratchDoneTransitions;
                done.Clear();
                foreach (var kv in transitions) {
                    var rt = kv.Value;
                    // Transitions interpolate a single property — classify
                    // by that property's kind: layout-affecting → Paint|Layout,
                    // wrapper-only (transform/opacity family) → Composite (the
                    // decoration cache stays valid; see ClassifyInvalidation),
                    // everything else → Paint.
                    var kind = (rt.Kind == PropertyKind.Length
                                || IsLayoutAffectingDiscreteOrNumber(rt.Property))
                        ? (InvalidationKind.Paint | InvalidationKind.Layout)
                        : IsWrapperOnlyProperty(rt.Property)
                            ? InvalidationKind.Composite
                            : InvalidationKind.Paint;
                    double elapsed = currentTimeSeconds - rt.StartTimeSeconds - rt.Spec.DelaySeconds;
                    if (elapsed <= 0) {
                        rt.CurrentText = rt.FromText;
                        MarkDirty(target, kv.Key.Item1, kind);
                        continue;
                    }
                    double duration = rt.Spec.DurationSeconds;
                    if (duration <= 0) {
                        rt.CurrentText = rt.ToText;
                        rt.Finished = true;
                        done.Add(kv.Key);
                        MarkDirty(target, kv.Key.Item1, kind);
                        continue;
                    }
                    double progress = elapsed / duration;
                    if (progress >= 1) {
                        rt.CurrentText = rt.ToText;
                        rt.Finished = true;
                        done.Add(kv.Key);
                        MarkDirty(target, kv.Key.Item1, kind);
                        continue;
                    }
                    double eased = rt.Spec.Easing.Evaluate(progress);
                    // Use the pre-parsed CssValue endpoints captured at
                    // StartTransitionFor — the interpolator's typed path
                    // skips two CssValue.TryParse calls per frame.
                    rt.CurrentText = ValueInterpolator.InterpolateParsed(
                        rt.FromParsed, rt.ToParsed,
                        rt.FromText, rt.ToText,
                        eased, rt.Kind, LengthContext);
                    MarkDirty(target, kv.Key.Item1, kind);
                }
                if (done.Count > 0) {
                    for (int i = 0; i < done.Count; i++) {
                        var k = done[i];
                        if (transitions.TryGetValue(k, out var doneRec)
                            && transitionsByElement.TryGetValue(k.Item1, out var doneList)) {
                            doneList.Remove(doneRec);
                            if (doneList.Count == 0) {
                                transitionsByElement.Remove(k.Item1);
                                transitioningElements.Remove(k.Item1);
                            }
                        }
                        transitions.Remove(k);
                    }
                    done.Clear();
                }
            }

            if (animations.Count > 0) {
                // P4: reuse instance scratch for the animation sweep too.
                var animDone = scratchDoneAnimations;
                animDone.Clear();
                foreach (var kv in animations) {
                    var rec = kv.Value;
                    double sampleTime = rec.Paused ? rec.PausedAtSeconds : currentTimeSeconds;
                    rec.CurrentSample = rec.Instance.Tick(sampleTime);
                    // Pure transform/opacity/color animations don't disturb
                    // layout — mark Paint only and the layout gate will
                    // skip its O(N) box-tree rebuild for this frame.
                    var kind = rec.CurrentSample != null
                        ? ClassifyInvalidation(rec.CurrentSample, rec.Instance.TypedSample)
                        : InvalidationKind.Paint;
                    MarkDirty(target, kv.Key.Item1, kind);

                    // Sweep finished animations the way transitions are swept
                    // above. Criteria: finite iteration count AND we're past
                    // the active window AND the fill mode doesn't preserve a
                    // sample (None / Backwards). Without this, finished
                    // animations stay in the dictionary forever — Compose()
                    // walks them on every Tick of every painted frame for
                    // every element that ever played an animation, defeating
                    // the per-element fast-path the cache otherwise gives.
                    if (rec.CurrentSample == null && !rec.Paused
                        && !double.IsPositiveInfinity(rec.Spec.IterationCount)
                        && rec.Spec.FillMode != FillMode.Forwards
                        && rec.Spec.FillMode != FillMode.Both) {
                        // iteration-count == 0 (spec-valid but unusual): the
                        // animation runs zero times — finished on the first
                        // tick regardless of duration. Without this, the
                        // record would leak in `animations`/`animationsByElement`
                        // forever because CurrentSample is null but the
                        // > 0 / > 0 guards below skip the sweep.
                        if (rec.Spec.IterationCount <= 0 || rec.Spec.DurationSeconds <= 0) {
                            animDone.Add(kv.Key);
                        } else {
                            double endTime = rec.StartTimeSeconds
                                + rec.Spec.DelaySeconds
                                + rec.Spec.DurationSeconds * rec.Spec.IterationCount;
                            if (currentTimeSeconds >= endTime) {
                                animDone.Add(kv.Key);
                            }
                        }
                    }
                }
                if (animDone.Count > 0) {
                    for (int i = 0; i < animDone.Count; i++) {
                        var k = animDone[i];
                        if (animations.TryGetValue(k, out var doneRec)
                            && animationsByElement.TryGetValue(k.Item1, out var doneList)) {
                            doneList.Remove(doneRec);
                            if (doneList.Count == 0) {
                                animationsByElement.Remove(k.Item1);
                                animatedElements.Remove(k.Item1);
                            }
                        }
                        animations.Remove(k);
                    }
                    animDone.Clear();
                }
            }
        }

        void MarkDirty(InvalidationTracker target, Element e, InvalidationKind kind) {
            if (target == null || e == null) return;
            if ((kind & InvalidationKind.Layout) != 0) {
                target.MarkLayoutForElement(e, cachedStyleForInvalidation);
                var remaining = kind & ~InvalidationKind.Layout;
                if (remaining != InvalidationKind.None) {
                    target.MarkDirty(e, remaining);
                }
                return;
            }
            target.MarkDirty(e, kind);
        }

        // Per-element composed-style cache. Compose() is hot — called every
        // frame for every animated element via paint's
        // RefreshPaintOnlyStyles. Without caching, each call allocated a
        // fresh ComputedStyle (192-property string[] + bool[]) and copied
        // ALL 192 properties from baseStyle, then overlaid the live
        // animation/transition sample. With 4 animated elements at 60Hz
        // that's ~6 KB/frame of GC + ~800 Set() calls — visible as
        // ~3.5 ms/frame in the painter's RefreshPaintOnlyStyles trace.
        //
        // The cache keys each entry on (element, baseStyle.Version): a
        // hit means the base style hasn't changed since last frame and
        // the cached composed style only needs its OVERLAY refreshed
        // (the handful of animation/transition properties for this
        // element this frame). A miss falls through to the full rebuild
        // path. The cache survives a base-style change because the next
        // baseStyle.Version will mismatch and force a rebuild.
        //
        // The cache stores ALSO the previous frame's overlaid property
        // names so we can re-set them back to their base value before
        // applying the current frame's overlay — otherwise yesterday's
        // animation sample would leak through to today's read.
        readonly Dictionary<Element, ComposedEntry> composedCache = new Dictionary<Element, ComposedEntry>();
        sealed class ComposedEntry {
            public ComputedStyle Style;
            public long BaseVersion;
            // Property names that were overlaid LAST call. Reset back to
            // base values at the start of the next call so removed
            // animation overlays don't leave residue.
            public readonly List<string> LastOverlayProps = new List<string>(8);
            // Parallel list of pre-resolved CssProperties ids for each
            // entry in LastOverlayProps. -1 marks a custom / unregistered
            // property (the reset path falls back to the string-keyed
            // Set/Get for that index). Both lists are appended in lock-
            // step everywhere an overlay is recorded — see Compose. This
            // pairing is what lets the reset loop above use the int-keyed
            // ComputedStyle paths and avoid a per-frame CssProperties.GetId
            // hashmap probe per overlay-property (P14/P15).
            public readonly List<int> LastOverlayPropIds = new List<int>(8);
        }

        public ComputedStyle Compose(Element element, ComputedStyle baseStyle) {
            if (element == null || baseStyle == null) return baseStyle;
            // Hot-path early return for the common case: no animation or
            // transition is attached to this element. Pre-fix this check
            // walked both dicts O(N_animations + N_transitions); now it's
            // two HashSet.Contains calls. For 1000 elements / 50 animating
            // that's ~50K dict iterations dropped to ~2K hash lookups per
            // full layout pass.
            bool hasAnim = animatedElements.Contains(element);
            bool hasTrans = transitioningElements.Contains(element);
            if (!hasAnim && !hasTrans) return baseStyle;

            ComposedEntry entry;
            bool reuse = composedCache.TryGetValue(element, out entry)
                && entry.BaseVersion == baseStyle.Version
                && entry.Style != null;

            ComputedStyle composed;
            if (reuse) {
                composed = entry.Style;
                // Reset the previous overlay back to base values. We
                // don't blow away the whole style — keep the 192-prop
                // backing array warm and only touch the names that
                // were overlaid last frame. Id-keyed Set/Get when the
                // overlay was registered on a known property id (every
                // path through the Compose body below pushes a paired
                // id alongside the name). Falls back to the string path
                // for custom-property / unregistered overlays — see P15
                // in CODE_AUDIT_FINDINGS.md.
                var lastProps = entry.LastOverlayProps;
                var lastIds = entry.LastOverlayPropIds;
                for (int i = 0; i < lastProps.Count; i++) {
                    int pid = lastIds[i];
                    if (pid >= 0) {
                        composed.Set(pid, baseStyle.Get(pid));
                    } else {
                        var name = lastProps[i];
                        composed.Set(name, baseStyle.Get(name));
                    }
                }
                lastProps.Clear();
                lastIds.Clear();
            } else {
                composed = new ComputedStyle(element);
                // P11: CopyFrom is three Array.Copy of the property arrays vs
                // ~190 string-keyed Set calls walking Enumerate(). It is also
                // more complete — it propagates importantSet, DecorationVersion
                // (so transform-only animations don't bust the decoration
                // cache) and the wrapper/decoration flags the Enumerate path
                // silently dropped. Fires on the first frame of every
                // transition/animation and after each base re-cascade of an
                // animated element.
                composed.CopyFrom(baseStyle);
                if (entry == null) {
                    entry = new ComposedEntry { Style = composed };
                    composedCache[element] = entry;
                } else {
                    entry.Style = composed;
                    entry.LastOverlayProps.Clear();
                    entry.LastOverlayPropIds.Clear();
                }
                entry.BaseVersion = baseStyle.Version;
            }

            // Apply animations first; later transitions override (transitions take precedence
            // over animations when both target the same property in our v1 model — closer to
            // the user's interactive intent). Element-indexed lookup keeps this O(per-element
            // active anims) instead of O(total active anims) — the dict scan was the dominant
            // cost when every tile in a grid has its own keyframe animation.
            if (hasAnim && animationsByElement.TryGetValue(element, out var elAnims)) {
                for (int ai = 0; ai < elAnims.Count; ai++) {
                    var rec = elAnims[ai];
                    if (rec.CurrentSample == null) continue;
                // Typed-fast path: when the animator emitted a CssValue
                // directly (e.g. rotate(<angle>) overlay updated in place),
                // route through SetParsed to skip the per-Tick string
                // materialisation that Set(string) would force.
                var typed = rec.Instance?.TypedSample;
                // CSS Animations L2 §5 / §10: composition mode controls how
                // the animation's effective value combines with the
                // underlying (un-animated) value. `Replace` overwrites
                // (L1 behaviour, no extra work). `Add` sums with the
                // base value for numeric/length samples in matching
                // units; non-numeric values fall back to `Replace` for
                // v1. `Accumulate` is treated as `Add` for v1 — strict
                // spec accumulation across iteration counts (transform
                // accumulation, color accumulation per §5.4) is
                // deferred. Tracked at H2b in CSS_COMPLIANCE_ISSUES.md.
                var composition = rec.Spec.Composition;
                foreach (var p in rec.CurrentSample) {
                    // CSS Cascading L4 §6.4.1: !important author declarations
                    // outrank animation declarations. Skip the overlay when
                    // the base style won this property via !important so the
                    // animation never replaces the author's declared value.
                    //
                    // Property id resolved via the AnimationInstance's per-
                    // sample-key cache: keyframe property names are stable
                    // across Ticks (they come from the @keyframes block,
                    // parsed once), so the first GetSampleKeyId call resolves
                    // the id and every subsequent frame is a 1-entry dict hit
                    // instead of the CssProperties.idByName probe Compose
                    // did per frame per element per property pre-fix
                    // (P14 in CODE_AUDIT_FINDINGS.md). pid < 0 marks a
                    // custom property (`--*`); we fall back to the string
                    // path so unregistered names still flow through.
                    int pid = rec.Instance != null ? rec.Instance.GetSampleKeyId(p.Key) : -1;
                    if (pid >= 0 && baseStyle.IsImportant(pid)) continue;
                    string effectiveValue = p.Value;
                    if (composition != AnimationCompositionMode.Replace) {
                        string underlying = pid >= 0 ? baseStyle.Get(pid) : baseStyle.Get(p.Key);
                        if (TryComposeAdd(underlying, p.Value, out var summed)) {
                            effectiveValue = summed;
                        }
                        // else: fall back to Replace semantics (typed
                        // values like colors / transforms aren't summed
                        // in v1; comment above).
                    }
                    if (typed != null && typed.TryGetValue(p.Key, out var typedVal) && typedVal != null
                            && composition == AnimationCompositionMode.Replace) {
                        // Typed fast-path only valid for Replace; Add/Accumulate
                        // need the underlying-base-aware path above.
                        if (pid >= 0) {
                            composed.SetParsed(pid, typedVal);
                            entry.LastOverlayProps.Add(p.Key);
                            entry.LastOverlayPropIds.Add(pid);
                            continue;
                        }
                    }
                    if (pid >= 0) {
                        composed.Set(pid, effectiveValue);
                    } else {
                        composed.Set(p.Key, effectiveValue);
                    }
                    entry.LastOverlayProps.Add(p.Key);
                    entry.LastOverlayPropIds.Add(pid);
                }
                // typed entries whose key didn't appear in CurrentSample
                // (e.g. transform fast-path that skipped sampleResult, or
                // the opacity typed-only fast path).
                if (typed != null) {
                    foreach (var p in typed) {
                        if (rec.CurrentSample.ContainsKey(p.Key)) continue;
                        if (p.Value == null) continue;
                        int pid = rec.Instance != null ? rec.Instance.GetSampleKeyId(p.Key) : -1;
                        if (pid < 0) continue;
                        if (baseStyle.IsImportant(pid)) continue;
                        if (composition != AnimationCompositionMode.Replace) {
                            // CSS Animations L2 §5: when composition isn't
                            // Replace, route the typed sample through the
                            // string-add path so the underlying base value
                            // is honoured. The typed CssValue's Raw is the
                            // textual form of the sample — TryComposeAdd
                            // handles numbers/lengths/single-fn shapes.
                            string underlying = baseStyle.Get(pid);
                            string sampleText = p.Value.Raw ?? p.Value.ToString();
                            if (TryComposeAdd(underlying, sampleText, out string summed)) {
                                composed.Set(pid, summed);
                                entry.LastOverlayProps.Add(p.Key);
                                entry.LastOverlayPropIds.Add(pid);
                                continue;
                            }
                            // Couldn't sum (color, multi-fn transform, etc.)
                            // — fall through to Replace semantics.
                        }
                        composed.SetParsed(pid, p.Value);
                        entry.LastOverlayProps.Add(p.Key);
                        entry.LastOverlayPropIds.Add(pid);
                    }
                }
                } // for each element-animation record
            }

            if (hasTrans && transitionsByElement.TryGetValue(element, out var elTrans)) {
                for (int ti = 0; ti < elTrans.Count; ti++) {
                    var rt = elTrans[ti];
                    if (rt.CurrentText != null) {
                        // Transitions sit above !important in the cascade
                        // (§6.4.1 top-of-stack), so an in-progress
                        // transition still wins. But !important wins over
                        // animations — and the same axis bit drives both
                        // paths in v1; we accept that nuance: the more
                        // common author intent is to pin a value with
                        // !important and stop ANY override. If we want to
                        // split the two later, we can gate this branch on
                        // an animation-only bit.
                        //
                        // Pre-resolved PropertyId comes from TransitionSpec —
                        // the spec is built once when the cascade fires
                        // transition-* declarations (not per frame), so the
                        // id is free at Compose time. Falls back to the
                        // string Set when PropertyId is -1 (custom / "all" /
                        // unregistered names). See P14 in CODE_AUDIT_FINDINGS.md.
                        int pid = rt.Spec.PropertyId;
                        if (pid >= 0 && baseStyle.IsImportant(pid)) continue;
                        if (pid >= 0) {
                            composed.Set(pid, rt.CurrentText);
                        } else {
                            composed.Set(rt.Property, rt.CurrentText);
                        }
                        entry.LastOverlayProps.Add(rt.Property);
                        entry.LastOverlayPropIds.Add(pid);
                    }
                }
            }

            return composed;
        }

        // CSS Animations L2 §5: compose the animation's effective value with
        // the underlying value via addition. v1 implementation handles the
        // simple cases:
        //   • bare numbers (opacity, scale) — sum the doubles
        //   • lengths in matching units (px+px, em+em, etc.) — sum the values,
        //     emit "<sum><unit>"
        //   • single-function transforms whose function name matches and
        //     whose argument list is a parallel sequence of numbers / lengths
        //     (translate(<len>,<len>), scale(<num>,<num>), rotate(<angle>)) —
        //     sum component-wise
        // Other shapes (colors, multi-function transform shorthands, percent
        // mixed with px, calc()) fall back to Replace semantics for v1 — the
        // caller treats `false` as "use the animation value as-is". This is a
        // conservative subset; widening it (color channel-add, transform
        // function-pair add, percent-as-px) is tracked separately.
        internal static bool TryComposeAdd(string underlying, string animationValue, out string composed) {
            composed = null;
            if (string.IsNullOrEmpty(underlying) || string.IsNullOrEmpty(animationValue)) return false;
            string u = underlying.Trim();
            string a = animationValue.Trim();
            if (u.Length == 0 || a.Length == 0) return false;

            // Bare numbers (e.g. opacity, scale single-arg).
            if (double.TryParse(u, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double un)
                && double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double an)) {
                double sum = un + an;
                composed = sum.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            // Lengths in matching units.
            if (TryParseSimpleLength(u, out double uVal, out string uUnit)
                && TryParseSimpleLength(a, out double aVal, out string aUnit)
                && uUnit == aUnit) {
                double sum = uVal + aVal;
                composed = sum.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + uUnit;
                return true;
            }

            // Single transform function: name(args...) where args is a
            // comma-separated list of bare-number / simple-length / angle
            // tokens. The two sides must use the same function name AND have
            // matching arg-shapes (count + per-arg kind/unit).
            if (TryParseSingleFunction(u, out string uName, out var uArgs)
                && TryParseSingleFunction(a, out string aName, out var aArgs)
                && string.Equals(uName, aName, StringComparison.OrdinalIgnoreCase)
                && uArgs.Count == aArgs.Count) {
                var sumArgs = new List<string>(uArgs.Count);
                for (int i = 0; i < uArgs.Count; i++) {
                    if (!TryComposeAdd(uArgs[i], aArgs[i], out string s)) {
                        // One arg doesn't compose under the simple rules —
                        // bail out and let the caller fall back to Replace.
                        return false;
                    }
                    sumArgs.Add(s);
                }
                composed = uName + "(" + string.Join(", ", sumArgs) + ")";
                return true;
            }

            return false;
        }

        // Parses a token like "10px" or "1.5em" or "-3deg" into (value, unit).
        // Returns false for bare numbers (no unit) — callers route those
        // through the bare-double path above so "0" and "0px" don't
        // accidentally fuse. Returns false for percent (we don't sum percents
        // here in v1).
        static bool TryParseSimpleLength(string text, out double value, out string unit) {
            value = 0;
            unit = null;
            if (string.IsNullOrEmpty(text)) return false;
            int i = text.Length;
            // Walk back from the end while the trailing character is a unit
            // letter (a-z A-Z) or '%'. The numeric prefix is everything else.
            while (i > 0) {
                char c = text[i - 1];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '%') {
                    i--;
                    continue;
                }
                break;
            }
            if (i == 0 || i == text.Length) return false; // no number or no unit
            string numText = text.Substring(0, i);
            string unitText = text.Substring(i);
            if (!double.TryParse(numText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return false;
            // We deliberately exclude '%' here — composing percent + px without
            // a layout resolution context can't be done safely at this layer.
            if (unitText == "%") return false;
            unit = unitText;
            return true;
        }

        // Parses "name(arg, arg, ...)" into (name, [arg, arg]). Returns false
        // for any input that isn't a single function call (e.g. multi-fn
        // transform lists like "translate(10px) rotate(45deg)").
        static bool TryParseSingleFunction(string text, out string name, out List<string> args) {
            name = null;
            args = null;
            if (string.IsNullOrEmpty(text)) return false;
            int openParen = text.IndexOf('(');
            if (openParen <= 0) return false;
            if (text[text.Length - 1] != ')') return false;
            string raw = text.Substring(openParen + 1, text.Length - openParen - 2);
            // Reject multi-fn lists: a second '(' at top level means we are
            // looking at "f(a) g(b)" or similar.
            int depth = 0;
            for (int i = openParen + 1; i < text.Length - 1; i++) {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                if (depth < 0) return false;
            }
            name = text.Substring(0, openParen).Trim();
            // Disallow names containing whitespace or operators.
            for (int i = 0; i < name.Length; i++) {
                char c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || (c >= '0' && c <= '9'))) {
                    return false;
                }
            }
            args = new List<string>();
            int start = 0;
            int d = 0;
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (c == '(') d++;
                else if (c == ')') d--;
                else if (c == ',' && d == 0) {
                    args.Add(raw.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            args.Add(raw.Substring(start).Trim());
            return true;
        }

        // EC4 — observability for the by-design fallback to `ease` when an
        // animation-/transition-timing-function string fails to parse. EasingParser
        // throws plain FormatException / ArgumentNullException from BCL paths
        // (cubic-bezier arg count, linear() control points, etc.), so the catch
        // up-stack stays broad; this helper dedupes per offending input so a
        // typo doesn't spam the console on every keyframe / property-list cycle.
        // Process-static set mirrors the ColorResolver DD2/DD3 pattern — single
        // session, never cleared except via ResetWarnings_TestOnly.
        static readonly HashSet<string> s_WarnedEasingKeys = new HashSet<string>();

        static void WarnEasingParseFailure(string raw, Exception ex) {
            string key = "EC4:" + (raw ?? "");
            lock (s_WarnedEasingKeys) {
                if (!s_WarnedEasingKeys.Add(key)) return;
            }
            UICssDiagnostics.Warn(
                "CssAnimationRunner",
                "EC4: easing string '" + (raw ?? "") + "' failed to parse (" +
                (ex?.GetType().Name ?? "Exception") +
                "); falling back to the initial value `ease` per CSS Easing L1 §2.1.");
        }

        // Test hook — wipes the dedupe set so a re-running test can observe a
        // warning that was already emitted by an earlier test in the same
        // session. Not part of the production contract.
        internal static void ResetWarnings_TestOnly() {
            lock (s_WarnedEasingKeys) s_WarnedEasingKeys.Clear();
            UICssDiagnostics.ResetForTests();
        }
    }
}
