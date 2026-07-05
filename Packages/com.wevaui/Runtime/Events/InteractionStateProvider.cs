using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Events {
    // MS5 — DOM-removal cleanup. The `states` map is keyed on Element; entries
    // are added by SetFlag whenever an element gains a pseudo-class bit
    // (Hover, Focus, FocusVisible, Active, Target, FocusWithin). Most of
    // those bits self-clean:
    //   - Hover and FocusWithin via DiffApplyFlagChain (the next hover /
    //     focus update strips the flag from any element no longer in the
    //     chain, regardless of whether it's still in the DOM).
    //   - Active via the dispatcher's PointerUp handler, which calls
    //     SetFlag(downTarget, Active, false) on every press-release cycle.
    // The bits that do NOT self-clean on removal are Focus / FocusVisible
    // (the dispatcher's ForgetIfInSubtree nulls `focused` when the focused
    // element is detached, but the cleanup code in FocusInternal reads
    // `previous = focused` which is now null, so the removed element's
    // Focus / FocusVisible bits stay in `states` forever) and Target (no
    // hook clears the previous target when the fragment'd element is
    // detached). Without the mutation subscription below, every focused-
    // then-removed element leaks one Dictionary<Element, ElementState>
    // entry — and that entry pins the Element strongly through the same
    // dispatcher that already plugged the analogous EventListeners /
    // CssAnimationRunner leaks (MS1 / MS2). Mirrors the EventDispatcher.
    // OnDomMutation pattern: subscribe in AttachToDocument, walk the
    // removed subtree top-down on ChildRemoved, unsubscribe in Dispose.
    public sealed class InteractionStateProvider : IElementStateProvider, IDisposable {
        readonly Dictionary<Element, ElementState> states = new();
        InvalidationTracker tracker;
        long version;
        Element targetElement;

        Document attachedDoc;
        Action<DomMutation> mutationListener;
        bool disposed;

        // Optional hook: if set, every state-bit flip on an element marks that
        // element with InvalidationKind.PseudoClassState on the tracker. The
        // cascade's per-element-state-digest cache key already filters which
        // elements miss when state.Version bumps, but downstream consumers
        // (layout, paint) still need the dirty bit to know which elements'
        // cached layout/paint output may have changed.
        public InvalidationTracker Tracker {
            get => tracker;
            set => tracker = value;
        }

        // Optional invalidation gate. When set, SetFlag consults
        // `cascade.SubjectMatchAffectedByStateBit(e, flag)` before stamping
        // PseudoClassState onto the tracker. The cascade's RuleFeatureSet
        // says "does any selector with this state pseudo in its subject
        // compound actually match this element?" — if no, the re-cascade
        // would produce identical content (Reset still bumps Version, which
        // fans out the WalkIncremental fallback over the whole subtree
        // unnecessarily). A real `:active` chain on a card click marks
        // section / body / html dirty even though no rule says `body:active`,
        // and the cascade then fans the Walk fallback over every other card
        // because the section's Version bumped.
        //
        // Default null = preserve the legacy "always mark" behavior so
        // unattached tests / use cases keep working. UIDocumentBuilder wires
        // this immediately after constructing the engine.
        CascadeEngine cascade;
        public void AttachCascade(CascadeEngine engine) {
            cascade = engine;
        }

        // Per-spec reactive doctrine: bumps every time SetFlag mutates the map.
        // CascadeEngine reads this via IElementStateProvider.Version. With the
        // per-element digest fast path the bump no longer invalidates the cache
        // wholesale, but it does serve as the global tie-breaker for selectors
        // using sibling combinators with stateful pseudos (the v1 simplification
        // documented in CascadeEngine.IncrementalState.cs).
        public long Version => version;

        public ElementState GetState(Element e) {
            if (e == null) return ElementState.None;
            var s = states.TryGetValue(e, out var v) ? v : ElementState.None;
            if (e.OwnerDocument != null) {
                // Index-based walk avoids the per-call enumerator alloc that
                // foreach (var c in IReadOnlyList<Node>) emits — that boxed
                // ~40 B per GetState call, ×N ancestor chain steps ×64
                // animated elements per frame = ~13 KB / frame on the
                // spinning-gem scene per the deep profile.
                var children = e.OwnerDocument.Children;
                Element root = null;
                for (int i = 0; i < children.Count; i++) {
                    if (children[i] is Element re) { root = re; break; }
                }
                if (e == root) s |= ElementState.Root;
            }
            if (FocusManager.IsDisabled(e)) s |= ElementState.Disabled;
            if (HasCheckedAttribute(e)) s |= ElementState.Checked;
            if (HasPlaceholderShown(e)) s |= ElementState.PlaceholderShown;
            return s;
        }

        internal void SetFlag(Element e, ElementState flag, bool on) {
            if (e == null) return;
            var cur = states.TryGetValue(e, out var v) ? v : ElementState.None;
            var next = on ? (cur | flag) : (cur & ~flag);
            if (next == cur) return;
            if (next == ElementState.None) states.Remove(e);
            else states[e] = next;
            version++;
            // Feature-set gate: skip the tracker mark when no selector's
            // state-carrying compound could match this element (subject,
            // descendant-left, or sibling-left position — audit CX1: the
            // old subject-only gate silently dropped `.parent:hover .child`
            // and `.a:hover + .b` flips entirely). Without the gate, an
            // `:active` chain on a card click would mark section / body /
            // html dirty even when no rule targets them on `:active`, and
            // the WalkIncremental ancestor-Version-bump fallback would fan
            // a full Walk over every other card. See AttachCascade docs.
            if (cascade != null && !cascade.StateBitAffectsElement(e, flag)) return;
            tracker?.MarkDirty(e, InvalidationKind.PseudoClassState);
        }

        internal void SetTargetElement(Element target) {
            if (targetElement == target) return;
            var previous = targetElement;
            targetElement = target;
            if (previous != null) SetFlag(previous, ElementState.Target, false);
            if (targetElement != null) SetFlag(targetElement, ElementState.Target, true);
        }

        internal bool HasFlag(Element e, ElementState flag) {
            if (e == null) return false;
            return states.TryGetValue(e, out var v) && (v & flag) != 0;
        }

        internal void ClearFlagEverywhere(ElementState flag) {
            var keys = new List<Element>(states.Keys);
            bool any = false;
            foreach (var k in keys) {
                var v = states[k];
                if ((v & flag) == 0) continue;
                var next = v & ~flag;
                if (next == ElementState.None) states.Remove(k);
                else states[k] = next;
                if (cascade != null && !cascade.StateBitAffectsElement(k, flag)) {
                    any = true;
                    continue;
                }
                tracker?.MarkDirty(k, InvalidationKind.PseudoClassState);
                any = true;
            }
            if (any) version++;
        }

        internal void SetHoverChain(IList<Element> chain) {
            DiffApplyFlagChain(ElementState.Hover, chain);
        }

        // CSS Selectors L4 §11.4.1: `:active` matches an element WHILE it is
        // being activated AND every one of its ancestors. The dispatcher's
        // earlier leaf-only SetFlag(hit, Active, true) violated that — clicking
        // a child element left the parent's `:active` rule dormant (visible in
        // a real `.challenge-card:active { transform: scale(0.98) }`,
        // which only flickered when the user happened to press the bare
        // padding edges of the LI rather than its inner content). Mirror the
        // hover machinery: stamp Active on the entire press chain, strip it
        // on release. Idempotent on no-op chains so the cascade isn't dirtied
        // on stationary mousedown frames.
        internal void SetActiveChain(IList<Element> chain) {
            DiffApplyFlagChain(ElementState.Active, chain);
        }

        internal void SetFocusWithinChain(IList<Element> chain) {
            DiffApplyFlagChain(ElementState.FocusWithin, chain);
        }

        // Replaces the set of elements carrying `flag` with exactly the elements
        // in `chain`. Only elements whose flag membership actually changes get
        // version-bumped and tracker-dirtied — a no-op chain (same set as before)
        // is fully idempotent. This matters for the dispatcher's hover update
        // path, which re-asserts the chain on every move (even when the hovered
        // element hasn't changed) and which would otherwise re-dirty the cascade
        // each frame.
        //
        // P2 (2026-05-24): replaced the `new List<Element>(states.Keys)`
        // snapshot with an in-place enumeration that collects strip-targets
        // into a reusable scratch buffer. The snapshot was allocating ~states
        // .Count × 8 B per hover update; on a tree with 200 interactive
        // elements that was 1.6 KB / pointer move ×120 Hz = ~200 KB/sec.
        // Enumeration safety: SetFlag mutates `states`, so we MUST NOT call
        // it inside the foreach. We collect first, mutate after.
        readonly List<Element> stripScratch = new();
        void DiffApplyFlagChain(ElementState flag, IList<Element> chain) {
            // Phase 1: collect elements that currently carry `flag` but are
            // not in the new chain. Reuse the scratch list — cleared on entry,
            // cleared on exit (in finally), capacity preserved across calls.
            stripScratch.Clear();
            try {
                foreach (var kv in states) {
                    if ((kv.Value & flag) == 0) continue;
                    if (chain != null && ListContains(chain, kv.Key)) continue;
                    stripScratch.Add(kv.Key);
                }
                // Phase 2: apply removals. Now safe to mutate `states`.
                for (int i = 0; i < stripScratch.Count; i++) {
                    SetFlag(stripScratch[i], flag, false);
                }
            } finally {
                stripScratch.Clear();
            }
            // Phase 3: add flag to elements in the chain that don't already have it.
            if (chain == null) return;
            for (int i = 0; i < chain.Count; i++) {
                SetFlag(chain[i], flag, true);
            }
        }

        static bool ListContains(IList<Element> list, Element e) {
            for (int i = 0; i < list.Count; i++) if (list[i] == e) return true;
            return false;
        }

        static bool HasCheckedAttribute(Element e) {
            if (e == null) return false;
            if (e.TagName != "input") return false;
            var type = e.GetAttribute("type");
            if (type != "checkbox" && type != "radio") return false;
            return e.HasAttribute("checked");
        }

        static bool HasPlaceholderShown(Element e) {
            if (e == null) return false;
            if (e.TagName != "input" && e.TagName != "textarea") return false;
            if (!e.HasAttribute("placeholder")) return false;
            var v = e.GetAttribute("value");
            return string.IsNullOrEmpty(v);
        }

        // Subscribes to the document's mutation events so element-removed
        // events compact the `states` dictionary. Idempotent: calling twice
        // with the same doc is a no-op; calling with a different doc detaches
        // the old subscription first. Pairs with Dispose. Wired from
        // UIDocumentBuilder once per document lifetime — InteractionState
        // Provider survives hot reload (only Cascade / Animator are replaced).
        public void AttachToDocument(Document doc) {
            if (disposed) throw new ObjectDisposedException(nameof(InteractionStateProvider));
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

        // Set of HTML attribute names whose presence or value affects a CSS
        // pseudo-class resolved through IElementStateProvider.GetState. When
        // any of these attributes changes, the state.Version must be bumped so
        // that the cascade's sibling-combinator global-fallback path
        // (CascadeEngine.IncrementalState.cs: ResolveStateDigest) sees the
        // mutation and re-evaluates affected sibling rules.
        //
        // `checked`    → ElementState.Checked  (input[type=checkbox/radio])
        // `disabled`   → ElementState.Disabled (FocusManager.IsDisabled)
        // `placeholder`→ ElementState.PlaceholderShown (empty-value detection)
        // `value`      → ElementState.PlaceholderShown (empty-value detection)
        static bool IsStatefulAttribute(string name) {
            return name == "checked"
                || name == "disabled"
                || name == "placeholder"
                || name == "value";
        }

        // Element-removed: walk the removed subtree top-down and drop every
        // descendant Element from the `states` map.
        //
        // Attribute mutations on stateful attributes (`checked`, `disabled`,
        // `placeholder`, `value`): bump version so the cascade's sibling-
        // combinator global-fallback path re-evaluates. GetState derives those
        // bits directly from the DOM at evaluation time, but the fallback path
        // uses state.Version as the cache-key discriminator — without a version
        // bump the cached entry for an adjacent label is never invalidated after
        // e.g. `input.SetAttribute("checked", "")`.
        //
        // Other mutation kinds (ChildAdded, non-stateful Attribute*, TextChanged)
        // need no action — state is only added via SetFlag, and a newly added
        // element has no entry until/unless a SetFlag fires against it.
        //
        // RaiseMutationBubbling fires BEFORE Node.RemoveChild unlinks the
        // subject from its parent (see Dom/Node.cs:74), so the subtree's
        // children are still intact and a recursive Children walk is
        // sufficient — no need to inspect Parent pointers.
        void OnDomMutation(DomMutation m) {
            if (disposed) return;
            if (m.Kind == DomMutationKind.ChildRemoved) {
                RemoveSubtree(m.Subject);
                return;
            }
            if (m.Kind == DomMutationKind.AttributeAdded
                || m.Kind == DomMutationKind.AttributeRemoved
                || m.Kind == DomMutationKind.AttributeChanged) {
                if (!IsStatefulAttribute(m.AttributeName)) return;
                if (m.Subject is not Element attrEl) return;
                version++;
                tracker?.MarkDirty(attrEl, InvalidationKind.PseudoClassState);
            }
        }

        void RemoveSubtree(Node root) {
            if (root == null) return;
            if (root is Element e) {
                if (states.Remove(e)) {
                    version++;
                    tracker?.MarkDirty(e, InvalidationKind.PseudoClassState);
                }
                // If the detached element happens to be the current :target,
                // forget it — otherwise the next SetTargetElement call would
                // try to SetFlag(previous, Target, false) against an orphan.
                if (targetElement == e) targetElement = null;
            }
            var kids = root.Children;
            for (int i = 0; i < kids.Count; i++) RemoveSubtree(kids[i]);
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            DetachFromDocument();
            states.Clear();
            targetElement = null;
        }

        // Test-only accessors for the MS5 leak-regression suite. Production
        // code goes through SetFlag / GetState which already encapsulate the
        // map.
        internal int StatesCountForTests => states.Count;
        internal bool ContainsForTests(Element e) => e != null && states.ContainsKey(e);
    }
}
