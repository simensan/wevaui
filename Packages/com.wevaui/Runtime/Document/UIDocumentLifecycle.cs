using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.Documents {
    // Per-frame helper. Pure C# — WevaDocument calls Update() each tick and
    // Update() drives the cascade -> layout -> paint -> bindings pipeline,
    // consuming the InvalidationTracker the builder attached to the document.
    //
    // Invariants the orchestrator MUST hold:
    //   - state.Invalidation is non-null and attached to state.Doc.
    //   - state.LayoutContext.ViewportWidthPx/Height match state.MediaContext
    //     (caller updates both before calling Update if the surface resizes).
    //   - controller is the same instance Build() saw (or its replacement set
    //     via WevaDocument.SetController, which rebuilds bindings).
    //
    // Reactivity contract: Update() returns without doing layout work when
    // nothing is dirty. Cascade/layout/paint cache stats should remain steady
    // across consecutive idle Update calls — the loop test asserts this.
    public static class UIDocumentLifecycle {
        // Per-state last-tick time so we can derive a delta for animators that
        // operate on relative seconds (smooth scroll, view transitions). Stored
        // on UIDocumentState rather than statically so multiple documents tick
        // independently in tests.
        public static UpdateResult Update(UIDocumentState state, object controller, double nowSeconds) {
            var result = new UpdateResult();
            if (state == null || state.Doc == null) return result;

            var tracker = state.Invalidation;

            // Compute delta from previous Update call.
            double dt = 0;
            if (!double.IsNaN(state.LastTickSeconds)) {
                dt = nowSeconds - state.LastTickSeconds;
                if (dt < 0) dt = 0;
            }
            state.LastTickSeconds = nowSeconds;

            // 1. Tick the animation runner. Active transitions/animations mark
            //    their elements with Paint | Layout on the tracker so the
            //    drain step below drops the stale layout/paint cache entries
            //    and the conditional RunLayout pass below picks up the
            //    composed (interpolated) style for this frame.
            if (state.Animator != null) {
                UnityEngine.Profiling.Profiler.BeginSample("Weva.Update.Animate");
                state.Animator.Tick(nowSeconds, tracker);
                UnityEngine.Profiling.Profiler.EndSample();
            }

            // 1b. Smooth scroll & snap. Snap settle uses `nowSeconds` to
            //     decide whether enough time has passed since the last wheel
            //     burst; the smooth animator integrates `dt` regardless.
            if (state.SmoothScroll != null) {
                state.SmoothScroll.Tick(dt, tracker);
            }
            state.ScrollEvents?.TickSnap(nowSeconds);
            // 1b'. Inertial flick glides (touch drag release). Integrated
            //      after SmoothScroll so a glide that retargets onto a snap
            //      point this frame is picked up by the animator next frame.
            state.ScrollEvents?.TickMomentum(dt, nowSeconds, tracker);

            // 1c. View transitions: tick the in-flight transition (if any).
            if (state.ViewTransitions != null) {
                state.ViewTransitions.Tick(dt, tracker);
            }

            // 1d. Caret blink. While a text-editable is focused, repaint it when
            //     the blink phase flips so the live caret (BoxToPaintConverter's
            //     InputCaretOf, which reads state.CaretBlinkOn this same frame)
            //     visibly blinks — a static paint can't animate it. Only a phase
            //     flip marks dirty (≈2 repaints/sec while focused, none idle).
            if (state.CaretActivityPending) {
                state.CaretActivityPending = false;
                state.CaretActivitySeconds = nowSeconds;
            }
            bool blinkOn = ComputeCaretBlinkOn(nowSeconds, state.CaretActivitySeconds);
            state.CaretBlinkOn = blinkOn;
            // PF2: test the phase flip FIRST. IsCaretEditable allocates (the
            // type-attribute ToLowerInvariant), and with the old ordering it
            // ran on EVERY frame while any input was focused — per-frame
            // garbage on otherwise idle frames (invariant violation). The
            // flip gate bounds it to ~2 calls/sec, and tracking the phase
            // unconditionally keeps the semantics identical (mark exactly on
            // a flip while a text-editable is focused).
            if (blinkOn != state.LastCaretBlinkOn) {
                state.LastCaretBlinkOn = blinkOn;
                var focusedEditable = state.Events?.FocusedElement;
                if (focusedEditable != null && IsCaretEditable(focusedEditable)) {
                    tracker?.MarkDirty(focusedEditable, Weva.Reactive.InvalidationKind.Paint);
                }
            }

            // 2. Bindings update first so Data on text nodes reflects current
            //    controller state BEFORE layout reads the text. Mutations from
            //    Bindings.Update flow into the tracker via the DOM Mutated
            //    event, then the next steps consume that dirty set.
            if (state.Bindings != null) {
                state.Bindings.Update(controller, tracker, state.StyleOf);
            }

            // 3. Drain dirty sets into each layer's cache. We deliberately do
            //    NOT call state.Cascade.Apply(tracker) here: dropping cascade
            //    entries eagerly destroys the previous-style snapshot that
            //    OnStyleChange needs to detect a transition trigger. The
            //    incremental cascade key (element.Version + parentStyle.Version
            //    + mediaContextVersion + stateProvider.Version) already forces
            //    a miss on any real change, and on miss the runner reads the
            //    stale entry as `previous` before overwriting it.
            state.LayoutEngine?.Apply(tracker);
            state.Painter?.Apply(tracker, state.BoxLookup);

            // 4. Decide whether layout must rerun. Initial pass (RootBox null)
            //    counts as dirty. Otherwise we delegate to IncrementalLayoutGate
            //    so the bench (which drives LayoutEngine directly) and the
            //    lifecycle share a single skip predicate.
            bool needsLayout = state.RootBox == null
                || !IncrementalLayoutGate.ShouldSkipLayout(tracker);

            // PseudoClassState dirty (e.g. :hover transition) needs the same
            // refresh path as Paint dirty: the element's pseudo-class
            // membership changed, so the cascade winner may have flipped to
            // a different rule (e.g. `.filter:hover` over `.filter-on`), and
            // Box.Style must be repointed at the new ComputedStyle for the
            // painter to pick up new background / color / border / etc.
            // Without this, hovering a button updates state.Version + marks
            // the tracker dirty but neither RunLayout nor RefreshPaintOnly-
            // Styles runs — the button visually stays in its un-hovered
            // state until something else triggers a layout (resize, scroll).
            bool styleRefreshNeeded = tracker != null
                && tracker.HasAny(InvalidationKind.Paint
                                  | InvalidationKind.PseudoClassState
                                  | InvalidationKind.Composite);
            if (needsLayout) {
                RunLayout(state, tracker);
                result.LayoutRan = true;
                // Even when layout ran, the subtree-only fast path leaves
                // every box OUTSIDE the dirty subtree pointing at its
                // previous-frame ComputedStyle. Paint-only animated
                // elements (e.g. aurora translating while combo-banner
                // padding changes) live outside that subtree, so we
                // still need to walk Paint-tagged dirty elements and
                // refresh their Box.Style with the current composed
                // style. Without this step paint-only animations
                // appear frozen whenever any other element triggers a
                // subtree relayout in the same frame.
                if (styleRefreshNeeded) {
                    RefreshPaintOnlyStyles(state, tracker);
                }
            } else if (styleRefreshNeeded) {
                // Layout-skip path with live paint-only invalidation
                // (transform/opacity/color animations and transitions),
                // or with pseudo-class state changes (hover / focus /
                // active). RunLayout is what normally pushes the latest
                // composed ComputedStyle onto each Box.Style — when we
                // skip it the boxes still hold the previous frame's
                // snapshot, so the painter would render with stale
                // styling (the hover effect appeared to not fire even
                // though state.Version had bumped).
                //
                // Walk the dirty Paint- or PseudoClassState-tagged
                // elements, look up the matching Box via the
                // ElementToBox index, and refresh Box.Style from the
                // cascade's composed-style accessor. Cascade.Get-
                // ComposedStyle layers the active animation / transition
                // samples on top of the cached base style and re-evaluates
                // selectors against the current state, so the refreshed
                // Box.Style carries this frame's interpolated values AND
                // the latest pseudo-class-matched rules.
                RefreshPaintOnlyStyles(state, tracker);
                // CSS Position L3 §6.3: sticky offsets depend on the
                // absolute scroll position. A wheel event updates
                // ScrollState but marks only Paint-dirty (no Layout flag),
                // so StickyResolver never ran in this else-branch and the
                // sticky offsets stayed stale from the prior layout pass.
                // Refresh sticky here so any scroll change (including a
                // single large delta that jumps across both pin boundaries)
                // always produces the correct clamp on the same frame.
                state.LayoutEngine?.RefreshStickyOffsets();
            }

            // 4b. Snapshot whether anything that could have changed the paint
            //     output happened this Update before the tracker is cleared.
            //     The render pass reads this on the next EmitPaint to decide
            //     whether to rebuild the paint list or reuse the prior frame's
            //     batches — saves the per-frame box-tree walk + paint-cmd
            //     allocation when the document is fully idle.
            bool paintMayHaveChanged = result.LayoutRan || (tracker != null && tracker.DirtyCount > 0);
            if (paintMayHaveChanged) state.PaintInvalidated = true;

            // 5. Per-frame tracker is single-frame: clear it now so subsequent
            //    mutations land cleanly. The cascade/layout/paint caches keep
            //    their entries — only the dirty set is reset.
            tracker?.Clear();

            return result;
        }

        // Walks the tracker's Paint-tagged dirty elements and re-points
        // each backing Box.Style at the cascade's freshly composed style.
        // Used in the layout-skip path (transform/opacity/color animations)
        // so the painter — which reads Box.Style verbatim — picks up the
        // latest interpolated values without paying the full O(N) box-tree
        // rebuild RunLayout does. The painter's per-box cache invalidates
        // automatically: PaintBoxCache.IsValid compares the cached
        // StyleVersion to box.Style.Version, and the cascade returns a new
        // ComputedStyle instance (and thus a new Version) on every call
        // where an animation overlay is live.
        static void RefreshPaintOnlyStyles(UIDocumentState state, InvalidationTracker tracker) {
            if (state == null || tracker == null || state.Cascade == null
                || state.ElementToBox == null) return;
            var styleProvider = state.State;
            // Iterate the underlying dictionary directly — DirtyEntries
            // returns the concrete Dictionary so foreach uses its struct
            // enumerator (no yield-return allocator). Inline the kind mask.
            // Process Paint-tagged (decoration animations/transitions),
            // Composite-tagged (wrapper-only animation ticks — transform /
            // opacity, which deliberately skip the Paint mark so the
            // decoration cache survives) AND PseudoClassState-tagged
            // (hover/focus/active) elements. All need their Box.Style
            // repointed at the freshly-composed ComputedStyle so the painter
            // reads the latest interpolated values.
            const InvalidationKind refresh = InvalidationKind.Paint
                | InvalidationKind.PseudoClassState
                | InvalidationKind.Composite;
            foreach (var kv in tracker.DirtyEntries) {
                if ((kv.Value & refresh) == 0) continue;
                if (kv.Key is Element e) {
                    var box = state.ElementToBox.Lookup(e);
                    if (box == null) continue;
                    var newStyle = state.Cascade.GetComposedStyle(e, styleProvider);
                    // Repoint every box that maps to this Element — see the
                    // matching comment in BoxToPaintConverter.Apply. The
                    // outer container box (background / border owner) and
                    // the inner content box (text owner) share the same
                    // Element pointer but are separate Box instances; both
                    // need their Style updated for the painter to see the
                    // hover-derived background. Walk transparently through
                    // anonymous wrapper boxes (Element=null) so the outer
                    // container — which can be 1–3 anonymous-wrapper levels
                    // up from the inner content box — also gets refreshed.
                    Box scope = box;
                    while (scope != null) {
                        if (scope.Element != null && !ReferenceEquals(scope.Element, e)) break;
                        if (ReferenceEquals(scope.Element, e)) scope.Style = newStyle;
                        scope = scope.Parent;
                    }
                }
            }
        }

        // Caret blink cadence. ~530ms on / ~530ms off (1.06s period) matches the
        // typical browser text-cursor blink. Driven off the lifecycle tick time so
        // it stays in lockstep with the rest of the frame clock; no per-input timer.
        const double CaretBlinkPeriod = 1.06;
        const double CaretBlinkOnHalf = 0.53;

        // True during the visible half of the blink cycle. A non-finite or negative
        // tick (e.g. the seeded LastTickSeconds=NaN first frame) shows the caret so
        // it never starts hidden.
        static bool ComputeCaretBlinkOn(double seconds) {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return true;
            return (seconds % CaretBlinkPeriod) < CaretBlinkOnHalf;
        }

        // Activity-anchored variant (input/selection audit #3): the phase runs
        // from the LAST caret activity, so the caret is solid for the first
        // 530ms after every keystroke / caret move — Chrome's blink-reset
        // behavior. Without an activity stamp (NaN) the phase free-runs from
        // the raw tick time (the pre-fix behavior: type fast during the off
        // half and the caret stayed invisible).
        internal static bool ComputeCaretBlinkOn(double nowSeconds, double activitySeconds) {
            if (double.IsNaN(activitySeconds)) return ComputeCaretBlinkOn(nowSeconds);
            return ComputeCaretBlinkOn(nowSeconds - activitySeconds);
        }

        // Whether the focused element hosts a blinking text caret. Mirrors
        // EmitInputOverlays' scope: text-type <input> and — since the
        // multiline caret map landed (audit #6) — <textarea>, whose overlay
        // paints a real caret now, so the blink flip must repaint it.
        static bool IsCaretEditable(Element e) {
            if (e == null) return false;
            if (e.TagName == "textarea") return true;
            if (e.TagName != "input") return false;
            string t = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
            return !(t == "checkbox" || t == "radio" || t == "range" || t == "hidden"
                     || t == "file" || t == "submit" || t == "reset" || t == "button" || t == "image");
        }

        public static void RunLayout(UIDocumentState state, InvalidationTracker tracker = null) {
            if (state == null || state.Doc == null) return;
            if (state.LayoutEngine == null || state.Cascade == null) return;
            // Cached delegate — see UIDocumentState.StyleOf. Avoids the
            // per-RunLayout closure + delegate allocation that fired every
            // frame on animated documents.
            System.Func<Element, ComputedStyle> styleOf = state.StyleOf;
            // Build cascade snapshots only when style inputs changed. Text-only
            // updates still need layout to see live TextNode.Data, but they can
            // reuse the previous ComputedStyle cache instead of re-walking
            // selectors and refilling the cascade snapshot for the whole DOM.
            bool needsCascade = tracker == null
                || state.RootBox == null
                || state.Cascade.LastSnapshot == null
                || tracker.HasAny(InvalidationKind.Style | InvalidationKind.Structure | InvalidationKind.PseudoClassState);
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Update.Cascade");
            if (needsCascade) {
                // Prefer the incremental cascade path: re-evaluate only the
                // ancestor closure of elements the tracker flagged dirty,
                // rather than walking the entire DOM. The cascade engine
                // falls back to the full ComputeAll automatically when the
                // dirty hints can't drive a correctness-preserving partial
                // walk (initial pass, snapshotDirty, :has()/:scope in sheet,
                // sibling-combinator-state-pseudo selectors, media context
                // change). For large HUDs this is the difference
                // between a click cascade visiting ~1500 elements vs the
                // ~10-90 elements that actually need re-cascade.
                if (tracker != null) {
                    state.Cascade.ComputeAllIncremental(state.Doc, state.State, EnumerateCascadeDirtyElements(tracker));
                } else {
                    state.Cascade.ComputeAll(state.Doc, state.State);
                }
                state.LayoutContext.Snapshot = state.Cascade.LastSnapshot;
                state.LayoutContext.SnapshotStyles =
                    state.Animator != null && state.Animator.HasActiveCompositions
                        ? null
                        : state.Cascade.Styles;
                // Drain the cascade's per-pass dirty sets onto the tracker.
                // Without this, post-cascade Layout/Structure marks (e.g.
                // `display` crossing the `none ↔ shown` boundary, surfaced
                // by ComputeOrHit's display-cross check) are noted in the
                // cascade's HashSets but never reach the layout engine —
                // so a tooltip that flips display=flex → display=none
                // wouldn't rebuild its box subtree, leaving the stale box
                // visible at its last-laid-out dimensions. The cascade is
                // the only stage that can observe ComputedStyle deltas;
                // this hop hands those deltas to the layout engine.
                if (tracker != null) {
                    state.Cascade.ApplyLayoutInvalidation(tracker);
                }
            } else if (IsViewportOnlyLayoutDirty(state, tracker)) {
                state.LayoutContext.Snapshot = state.Cascade.LastSnapshot;
                state.LayoutContext.SnapshotStyles =
                    state.Animator != null && state.Animator.HasActiveCompositions
                        ? null
                        : state.Cascade.Styles;
            } else {
                state.LayoutContext.Snapshot = null;
                state.LayoutContext.SnapshotStyles = null;
            }
            UnityEngine.Profiling.Profiler.EndSample(); // Weva.Update.Cascade
            // Pass the tracker through to the layout engine so it can take
            // the subtree-only relayout fast path when only a handful of
            // out-of-flow elements (or otherwise structurally-isolated
            // subtrees) are dirty. Without the tracker the engine treats
            // every call as a full rebuild — what made
            // `padding`-animating absolute elements push frametime to
            // ~66ms even when only one element actually changed.
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Update.Layout");
            var box = state.LayoutEngine.Layout(state.Doc, styleOf, state.LayoutContext, tracker);
            UnityEngine.Profiling.Profiler.EndSample(); // Weva.Update.Layout
            state.RootBox = box;
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Update.ElementToBoxRebuild");
            // P4: a Skip-path layout returns the box tree unchanged (same
            // instances), so the existing Element->Box map is still valid —
            // don't pay a full-tree walk + dictionary rebuild for nothing.
            // Subtree/Full paths may create or recycle boxes, so they still
            // rebuild. (Scoping Subtree to just the spliced subtree is a
            // further win that needs the engine to surface the spliced root.)
            if (state.ElementToBox != null
                && state.LayoutEngine.LastPath != Weva.Layout.LayoutEngine.LayoutPath.Skip) {
                state.ElementToBox.Rebuild(box);
            }
            UnityEngine.Profiling.Profiler.EndSample();
            // Wire the cascade's container-query box lookup to the now-current box index.
            // Per the v1 chicken-and-egg simplification: container queries see the size
            // produced by *this* layout pass when the *next* cascade pass runs, so a
            // newly-applied container-type rule needs one more layout pass to settle.
            if (state.ElementToBox != null) {
                state.Cascade.ElementToBoxLookup = state.ElementToBox.Lookup;
            }
            if (state.HitTester != null) {
                state.HitTester.Inner = box != null
                    ? new BoxTreeHitTester(box, state.LayoutEngine?.ScrollContainer)
                    : null;
            }
            // Update the ScrollEventHandler's viewport scroll root so wheel
            // events that find no inner scroll-container ancestor can fall
            // through to the viewport scroll state (CSS Overflow §3.3).
            // The root box is the anonymous viewport box (Element == null)
            // that ScrollLayout.RunViewportScroll registered the state on.
            if (state.ScrollEvents != null) {
                var sc = state.LayoutEngine?.ScrollContainer;
                state.ScrollEvents.ViewportRoot =
                    (box != null && box.Element == null && sc != null && sc.Has(box)) ? box : null;
            }
            // Apply scroll positions captured across a pipeline rebuild
            // (WevaDocument.Rebuild) — deferred to here because restoring
            // needs the NEW pipeline's boxes and MaxScroll extents. One-shot.
            if (state.PendingScrollRestores != null) {
                ApplyPendingScrollRestores(state);
                state.PendingScrollRestores = null;
            }
        }

        static void ApplyPendingScrollRestores(UIDocumentState state) {
            var restores = state.PendingScrollRestores;
            var sc = state.LayoutEngine?.ScrollContainer;
            if (restores == null || sc == null || state.Doc == null) return;
            for (int i = 0; i < restores.Count; i++) {
                var r = restores[i];
                // Resolve the DOM path against the re-parsed document.
                Weva.Dom.Node n = state.Doc;
                for (int p = 0; r.Path != null && p < r.Path.Length && n != null; p++) {
                    int idx = r.Path[p];
                    n = idx >= 0 && idx < n.Children.Count ? n.Children[idx] : null;
                }
                if (!(n is Weva.Dom.Element el)) continue;
                var box = state.ElementToBox?.Lookup(el);
                if (box == null) continue;
                var st = sc.GetOrCreate(box);
                if (st == null) continue;
                st.ScrollX = System.Math.Min(System.Math.Max(0, r.ScrollX), System.Math.Max(0, st.MaxScrollX));
                st.ScrollY = System.Math.Min(System.Math.Max(0, r.ScrollY), System.Math.Max(0, st.MaxScrollY));
                box.ScrollX = st.ScrollX;
                box.ScrollY = st.ScrollY;
                st.BumpVersion();
            }
        }

        static bool IsViewportOnlyLayoutDirty(UIDocumentState state, InvalidationTracker tracker) {
            if (state == null || tracker == null || state.Doc == null) return false;
            if (tracker.DirtyEntries.Count != 1) return false;
            if (!tracker.DirtyEntries.TryGetValue(state.Doc, out var kind)) return false;
            const InvalidationKind allowed = InvalidationKind.Layout | InvalidationKind.Paint;
            return (kind & ~allowed) == 0 && (kind & InvalidationKind.Layout) != 0;
        }

        // Yields every Element the tracker has flagged as needing a cascade
        // re-evaluation — i.e. anything with a Style, PseudoClassState, or
        // Structure mark. The cascade walks the ancestor closure of this set
        // when ComputeAllIncremental is called, so a narrow set translates to
        // a narrow walk.
        //
        // Iterator pattern avoids allocating a list for the dirty hints. The
        // cascade enumerates this exactly once during its ancestor-closure
        // build. Layout-only / Paint-only / Composite-only dirty entries
        // don't need cascade re-eval and are deliberately filtered out;
        // including them would expand the walk closure for no benefit.
        static System.Collections.Generic.IEnumerable<Weva.Dom.Element> EnumerateCascadeDirtyElements(InvalidationTracker tracker) {
            const InvalidationKind cascadeKinds = InvalidationKind.Style
                                                | InvalidationKind.PseudoClassState
                                                | InvalidationKind.Structure;
            foreach (var entry in tracker.DirtyEntries) {
                if ((entry.Value & cascadeKinds) == 0) continue;
                if (entry.Key is Weva.Dom.Element e) yield return e;
            }
        }

        public struct UpdateResult {
            public bool LayoutRan;
        }

        // Resolves which render backend a given document should use under the current
        // build configuration. Three modes:
        //   - Auto: prefer URP if the active pipeline is URP and we're in play mode;
        //           otherwise fall back to IMGUI for editor previewing.
        //   - IMGUI: explicit override; used for non-URP projects.
        //   - URP: explicit override; assumes UIBatchedRendererFeature is configured.
        // The hook is additive — callers (WevaDocument, the editor preview window) read
        // this to decide whether to attach IMGUIDocumentRenderer. It does NOT drive
        // paint by itself; the URP path runs through the renderer feature.
        public static ResolvedBackend ResolveBackend(int rendererBackend, bool isPlaying, bool isUrpActive) {
            // 0 = Auto, 1 = IMGUI, 2 = URP — mirrors WevaDocument.RendererBackendKind.
            switch (rendererBackend) {
                case 1:
                    return ResolvedBackend.IMGUI;
                case 2:
                    return ResolvedBackend.URP;
                default:
                    return (isPlaying && isUrpActive) ? ResolvedBackend.URP : ResolvedBackend.IMGUI;
            }
        }

        public enum ResolvedBackend {
            IMGUI,
            URP
        }
    }
}
