using System;
using System.Collections.Generic;
using Weva.Css.Selectors;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Profiling;
using Weva.Reactive;

namespace Weva.Events {
    public sealed class EventDispatcher : IDisposable {
        readonly Document doc;
        readonly IHitTester hitTester;
        readonly IUIClock clock;
        readonly EventListeners listeners = new();
        readonly FocusManager focusManager = new();
        readonly InteractionStateProvider state;
        // Per-dispatch scratch buffer for the listener snapshot. Mouse-move
        // dispatches were allocating one List<EventListener> per ancestor per
        // phase — ~5 × 2 × ~120Hz = ~1200 list allocs/sec on a quiet scene.
        // Reused via Clear() and grows to the high-water-mark of any single
        // dispatch. Reentrant dispatches (a handler that dispatches a
        // synthetic event) save/restore via a second pooled list.
        readonly List<EventListener> listenerScratch = new();
        readonly Stack<List<EventListener>> listenerScratchPool = new();

        // P1 — Element-path list pool. `BuildPath` previously allocated a
        // fresh `List<Element>` per pointer-move (×3 in the hover-changed
        // branch: oldChain, newChain, and the SetHoverChain re-assertion
        // when newHit == hovered). Pooled rent/return drops that to zero
        // allocations in steady state. The pool is bounded at 8 lists —
        // typical reentrancy depth is 1-2 (Dispatch -> handler -> nothing
        // pointer-related), so an 8-deep cap is a safety bound, not a
        // working-set cap. List capacity grows to ancestor-chain depth
        // high-water-mark and is preserved across rents via Clear().
        readonly Stack<List<Element>> pathListPool = new();

        // P21 — PointerEvent pool. Each pointer-move dispatched 1 event;
        // each enter/leave chain change dispatched 1-N more. Pool guarantees
        // zero allocations in steady state. See PointerEvent class header
        // for the lifetime contract (handlers MUST NOT retain refs past
        // synchronous invocation).
        readonly Stack<PointerEvent> pointerEventPool = new();
        // Test/diag counter — bumped each time we actually `new PointerEvent()`
        // because the pool was empty. Lets the P21 regression test pin
        // zero-construction in steady state.
        internal int PointerEventsConstructedForTests { get; private set; }

        Element focused;
        Element hovered;
        readonly Dictionary<int, Element> pointerDownTargets = new();
        int buttonsMask;
        bool focusVisible;
        // Element that has called SetPointerCapture. While non-null,
        // PointerMove and PointerUp for any button route directly to this
        // element regardless of hit-test result. Mirrors HTMLElement.setPointerCapture
        // — the pattern drag-controllers (RangeController, PanManipulator)
        // use to keep tracking the pointer when it leaves the source element.
        Element pointerCaptureTarget;

        // DOM-mutation subscription. The listener map inside `listeners` is
        // keyed by Element; without this hook every <button on-click="..."/>
        // (or any addEventListener-attached element) that gets removed from
        // the tree would leak its Element + listener closures for the
        // lifetime of the dispatcher. Mirrors the FormControlsRegistry /
        // BindingSet / InvalidationTracker subscription pattern.
        Action<DomMutation> mutationListener;
        bool disposed;

        public EventDispatcher(Document doc, IHitTester hitTester, IUIClock clock = null)
            : this(doc, hitTester, null, clock) { }

        // The cascade reads :hover / :active / :focus state from this provider,
        // so the dispatcher must write to the SAME instance the cascade was
        // built against — otherwise hover styles never apply. UIDocumentBuilder
        // passes its `state.State`; callers that don't care (unit tests) get
        // a private instance via the legacy two-arg overload.
        public EventDispatcher(Document doc, IHitTester hitTester, InteractionStateProvider stateProvider, IUIClock clock = null) {
            this.doc = doc;
            this.hitTester = hitTester;
            this.state = stateProvider ?? new InteractionStateProvider();
            this.clock = clock ?? new SystemUIClock();
            if (doc != null) {
                mutationListener = OnDomMutation;
                doc.Mutated += mutationListener;
            }
        }

        // Subtree-aware listener-map compaction on element removal. Subscribed
        // to Document.Mutated in the constructor; unsubscribed in Dispose.
        // Walks the removed subtree top-down so descendants that owned their
        // own listeners are evicted alongside the subtree root. Other
        // mutation kinds (ChildAdded, Attribute*, TextChanged) are not
        // listener-relevant — the map is keyed only by Element identity, and
        // a newly added element won't appear in the map until/unless a
        // listener is actually registered against it.
        void OnDomMutation(DomMutation m) {
            if (disposed) return;
            if (m.Kind != DomMutationKind.ChildRemoved) return;
            // Subject is the node being removed; clear it and every
            // descendant that happens to be a listener-bearing Element.
            // Also drop any dispatcher-internal element references so the
            // removed subtree is not pinned via hovered / focused /
            // pointer-capture / pointer-down state.
            listeners.RemoveSubtree(m.Subject);
            ForgetIfInSubtree(m.Subject);
        }

        // Clears hovered / focused / pointerCaptureTarget / pointerDownTargets
        // entries that reference any element inside the removed subtree.
        // Without this the dispatcher would itself pin the orphan via these
        // single-element fields even though `listeners` has been compacted.
        void ForgetIfInSubtree(Node removedRoot) {
            if (removedRoot == null) return;
            if (hovered != null && IsInSubtree(hovered, removedRoot)) hovered = null;
            if (focused != null && IsInSubtree(focused, removedRoot)) focused = null;
            if (pointerCaptureTarget != null && IsInSubtree(pointerCaptureTarget, removedRoot)) pointerCaptureTarget = null;
            if (pointerDownTargets.Count > 0) {
                pointerDownScratch.Clear();
                foreach (var kv in pointerDownTargets) {
                    if (IsInSubtree(kv.Value, removedRoot)) pointerDownScratch.Add(kv.Key);
                }
                if (pointerDownScratch.Count > 0 && state != null) {
                    // The pressed target's :active chain extends UP into
                    // elements that survive this removal (the press chain
                    // ends at the document root, but the removed subtree
                    // is some descendant of it). Those surviving ancestors
                    // would otherwise hold `:active` forever — PointerUp
                    // can't reach them because the original target is gone
                    // from pointerDownTargets after this loop. Mirror the
                    // PointerUp clear so :active doesn't leak.
                    state.SetActiveChain(null);
                }
                for (int i = 0; i < pointerDownScratch.Count; i++) {
                    pointerDownTargets.Remove(pointerDownScratch[i]);
                }
                pointerDownScratch.Clear();
            }
        }
        readonly List<int> pointerDownScratch = new();

        static bool IsInSubtree(Element candidate, Node root) {
            if (candidate == null || root == null) return false;
            if (candidate == root) return true;
            // RaiseMutationBubbling fires BEFORE Node.RemoveChild unlinks the
            // subject from its parent (see Dom/Node.cs), so the candidate's
            // parent chain is still intact and a simple ancestor walk is
            // sufficient — no need to traverse `root.Children` from the top.
            for (var n = candidate.Parent; n != null; n = n.Parent) {
                if (n == root) return true;
            }
            return false;
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            if (doc != null && mutationListener != null) {
                doc.Mutated -= mutationListener;
            }
            mutationListener = null;
        }

        public InteractionStateProvider StateProvider => state;
        public Element FocusedElement => focused;
        public Element HoveredElement => hovered;
        public string TargetFragment { get; private set; }

        public Func<Element, bool> IsHidden {
            get => focusManager.IsHidden;
            set => focusManager.IsHidden = value;
        }

        public void AddEventListener(Element target, EventKind kind, EventListener handler, bool useCapture = false) {
            listeners.AddListener(target, kind, handler, useCapture);
        }

        public void RemoveEventListener(Element target, EventKind kind, EventListener handler, bool useCapture = false) {
            listeners.RemoveListener(target, kind, handler, useCapture);
        }

        // Pointer capture (HTML5 setPointerCapture / releasePointerCapture).
        // While captured, PointerMove / PointerUp dispatch directly to `target`
        // even when the pointer has wandered outside the target's bounds.
        // Drag-style controllers (range slider, pan manipulator) call
        // SetPointerCapture on PointerDown and rely on it surviving until the
        // matching PointerUp; ReleasePointerCapture is automatic on PointerUp,
        // but explicit release is also supported.
        //
        // NG1: target must be non-null. Passing null has no useful semantics
        // (there is no element to capture against) and historically clobbered
        // a sibling controller's capture without warning. Callers wanting to
        // release should call ReleasePointerCapture() explicitly.
        public void SetPointerCapture(Element target) {
            if (target == null) throw new ArgumentNullException(nameof(target));
            pointerCaptureTarget = target;
        }
        public void ReleasePointerCapture(Element target = null) {
            if (target == null || pointerCaptureTarget == target) pointerCaptureTarget = null;
        }
        public Element CapturedPointerTarget => pointerCaptureTarget;

        Element RootElement {
            get {
                if (doc == null) return null;
                foreach (var c in doc.Children) {
                    if (c is Element e) return e;
                }
                return null;
            }
        }

        // Rent a path list from the pool (or allocate one if the pool is
        // empty) and fill it with the root-to-target ancestor chain. Caller
        // MUST pass the returned list to `ReturnPathList` (try/finally) so
        // the list goes back into the pool — otherwise the pool drains and
        // every subsequent BuildPath falls back to allocation.
        List<Element> RentPath(Element target) {
            var path = pathListPool.Count > 0 ? pathListPool.Pop() : new List<Element>();
            path.Clear();
            for (var n = target; n != null; n = n.Parent as Element) {
                path.Add(n);
            }
            path.Reverse();
            return path;
        }

        void ReturnPathList(List<Element> path) {
            if (path == null) return;
            path.Clear();
            // Cap the pool depth to bound retained capacity. 8 is generous
            // — reentrant Dispatch -> handler -> Dispatch synthetic event
            // chains rarely exceed depth 2.
            if (pathListPool.Count < 8) pathListPool.Push(path);
        }

        PointerEvent RentPointerEvent() {
            PointerEvent evt;
            if (pointerEventPool.Count > 0) {
                evt = pointerEventPool.Pop();
            } else {
                evt = new PointerEvent();
                PointerEventsConstructedForTests++;
            }
            evt.ResetForReuse();
            return evt;
        }

        void ReturnPointerEvent(PointerEvent evt) {
            if (evt == null) return;
            // Bound the pool: pointer events fan out at most ~chain-depth
            // per move (enter/leave fan-out). 16 covers ~4-5 ancestors deep
            // with a 3x safety margin for reentrancy.
            if (pointerEventPool.Count < 16) pointerEventPool.Push(evt);
        }

        void Dispatch(UIEvent evt, Element target) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.EventDispatch)) {
                if (target == null) return;
                // HTML spec §4.10.18.5: disabled form controls do NOT fire
                // the high-level activation events (click, submit). We gate
                // ONLY those two — pointerdown/pointerup/pointermove still
                // flow (matches browsers; needed for tooltip/state machines)
                // and input/change are dispatched by form-controllers that
                // already short-circuit on `disabled`. The walk covers the
                // "click on a <span> inside a <button disabled>" case — any
                // ancestor in the to-target chain being a disabled form
                // control suppresses dispatch, mirroring how browsers
                // cancel the activation behaviour at the disabled host.
                if ((evt.Kind == EventKind.Click || evt.Kind == EventKind.Submit) &&
                    IsClickSuppressedByDisabledAncestor(target)) {
                    return;
                }
                evt.Target = target;
                evt.TimestampSeconds = clock.NowSeconds;
                // try/finally guarantees the pooled list returns even if a
                // listener throws — P1 regression test pins this contract.
                var path = RentPath(target);
                try {
                    for (int i = 0; i < path.Count - 1; i++) {
                        var node = path[i];
                        evt.Phase = EventPhase.Capture;
                        evt.CurrentTarget = node;
                        InvokeListeners(node, evt, EventPhase.Capture);
                        if (evt.PropagationStopped) return;
                    }

                    evt.Phase = EventPhase.AtTarget;
                    evt.CurrentTarget = target;
                    InvokeListeners(target, evt, EventPhase.AtTarget);
                    if (evt.PropagationStopped) return;

                    if (!evt.Bubbles) return;

                    for (int i = path.Count - 2; i >= 0; i--) {
                        var node = path[i];
                        evt.Phase = EventPhase.Bubble;
                        evt.CurrentTarget = node;
                        InvokeListeners(node, evt, EventPhase.Bubble);
                        if (evt.PropagationStopped) return;
                    }
                } finally {
                    ReturnPathList(path);
                }
            }
        }

        void InvokeListeners(Element node, UIEvent evt, EventPhase phase) {
            // Rent a fresh scratch list when reentering this method (a handler
            // that synchronously fires another event). The primary scratch is
            // a field for the common non-reentrant case so it survives across
            // dispatches with its capacity intact.
            List<EventListener> snapshot;
            bool rented;
            if (listenerScratchInUse) {
                snapshot = listenerScratchPool.Count > 0 ? listenerScratchPool.Pop() : new List<EventListener>();
                rented = true;
            } else {
                snapshot = listenerScratch;
                listenerScratchInUse = true;
                rented = false;
            }
            snapshot.Clear();
            try {
                if (phase == EventPhase.AtTarget) {
                    // DOM Standard §2.9 (dispatch): at AT_TARGET, both capture
                    // and non-capture listener registrations fire — they are
                    // distinct listeners per §2.7 (listener identity is the
                    // (type, callback, capture) triple). The previous dedup
                    // collapsed a callback registered with both useCapture=true
                    // AND useCapture=false into a single invocation, hiding
                    // the second registration entirely.
                    listeners.AppendListeners(node, evt.Kind, EventPhase.Capture, snapshot);
                    listeners.AppendListeners(node, evt.Kind, EventPhase.Bubble, snapshot);
                } else {
                    listeners.AppendListeners(node, evt.Kind, phase, snapshot);
                }
                for (int i = 0; i < snapshot.Count; i++) {
                    try {
                        snapshot[i].Invoke(evt);
                    } catch (Exception ex) {
                        UICssDiagnostics.Warn("event-listener", $"Listener for {evt.Kind} threw: {ex.Message}");
                    }
                    if (evt.ImmediatePropagationStopped) return;
                }
            } finally {
                snapshot.Clear();
                if (rented) {
                    if (listenerScratchPool.Count < 8) listenerScratchPool.Push(snapshot);
                } else {
                    listenerScratchInUse = false;
                }
            }
        }
        bool listenerScratchInUse;

        static int ButtonBit(int button) {
            switch (button) {
                case 0: return 1;
                case 1: return 4;
                case 2: return 2;
                default: return 1 << button;
            }
        }

        static (bool s, bool c, bool a, bool m) Split(KeyModifiers mods) {
            return ((mods & KeyModifiers.Shift) != 0,
                    (mods & KeyModifiers.Ctrl) != 0,
                    (mods & KeyModifiers.Alt) != 0,
                    (mods & KeyModifiers.Meta) != 0);
        }

        // Input/selection audit #4 — click-streak tracking (DOM UIEvent.detail).
        // Chrome model: a button-0 down within the double-click time AND ~4px
        // of the previous down on the same target continues the streak
        // (1 = single, 2 = double, 3 = triple, keeps counting); anything else
        // restarts at 1. Consumers key word/paragraph selection off Detail.
        int clickStreak;
        double clickStreakDownSeconds = double.NegativeInfinity;
        double clickStreakDownX = double.NegativeInfinity;
        double clickStreakDownY = double.NegativeInfinity;
        Element clickStreakTarget;
        public const double ClickStreakMaxGapSeconds = 0.5;
        public const double ClickStreakMaxTravelPx = 4.0;

        public void DispatchPointerDown(double x, double y, int button, KeyModifiers mods) {
            buttonsMask |= ButtonBit(button);
            var hit = hitTester != null ? hitTester.HitTest(x, y) : null;
            UpdateHover(hit, x, y, mods);

            var (sh, ct, al, mt) = Split(mods);

            if (button == 0) {
                double nowT = clock != null ? clock.NowSeconds : 0;
                bool continues = hit != null
                    && ReferenceEquals(hit, clickStreakTarget)
                    && nowT - clickStreakDownSeconds <= ClickStreakMaxGapSeconds
                    && Math.Abs(x - clickStreakDownX) <= ClickStreakMaxTravelPx
                    && Math.Abs(y - clickStreakDownY) <= ClickStreakMaxTravelPx;
                clickStreak = continues ? clickStreak + 1 : 1;
                clickStreakDownSeconds = nowT;
                clickStreakDownX = x;
                clickStreakDownY = y;
                clickStreakTarget = hit;
            }

            if (hit != null) {
                pointerDownTargets[button] = hit;
                // CSS Selectors L4 §11.4.1: `:active` matches the hit element
                // AND every ancestor. Stamp the full press chain so parent
                // rules like `.card:active { transform: scale(0.98) }` fire
                // even when the press lands on a descendant.
                var activePath = RentPath(hit);
                try {
                    state.SetActiveChain(activePath);
                } finally {
                    ReturnPathList(activePath);
                }

                var evt = RentPointerEvent();
                evt.Kind = EventKind.PointerDown;
                evt.X = x; evt.Y = y; evt.Button = button; evt.Buttons = buttonsMask;
                evt.ShiftKey = sh; evt.CtrlKey = ct; evt.AltKey = al; evt.MetaKey = mt;
                evt.Detail = button == 0 ? clickStreak : 1;
                bool downDefaultPrevented;
                try {
                    Dispatch(evt, hit);
                    downDefaultPrevented = evt.DefaultPrevented;
                } finally {
                    ReturnPointerEvent(evt);
                }

                // Input/selection audit #10: focus is the DEFAULT ACTION of a
                // pointer-down — preventDefault() cancels it and the previous
                // focus stays, exactly Chrome's focus-preserving-toolbar
                // idiom. This also stops scrollbar clicks from stealing focus
                // (ScrollEventHandler PreventDefaults the down that starts a
                // thumb drag; pre-fix that click focused/blurred whatever
                // element sat UNDER the scrollbar, firing spurious change
                // events from the blur commit).
                if (!downDefaultPrevented) FocusFromPointer(hit);
            } else {
                FocusFromPointer(null);
            }
        }

        public void DispatchPointerUp(double x, double y, int button, KeyModifiers mods) {
            var hit = hitTester != null ? hitTester.HitTest(x, y) : null;
            UpdateHover(hit, x, y, mods);
            buttonsMask &= ~ButtonBit(button);
            var (sh, ct, al, mt) = Split(mods);

            Element downTarget = pointerDownTargets.TryGetValue(button, out var d) ? d : null;
            if (downTarget != null) {
                // Clear `:active` from the whole press chain — mirror of the
                // PointerDown SetActiveChain. Passing null is documented as
                // "no element should carry Active anymore", which
                // DiffApplyFlagChain implements by stripping every current
                // holder. Cheap when the set is empty.
                state.SetActiveChain(null);
            }
            pointerDownTargets.Remove(button);

            // Captured pointer overrides hit-test for the up event so drag
            // controllers receive PointerUp even when the pointer is released
            // outside the original target.
            Element upTarget = pointerCaptureTarget ?? hit;

            bool upDefaultPrevented = false;
            if (upTarget != null) {
                var evt = RentPointerEvent();
                evt.Kind = EventKind.PointerUp;
                evt.X = x; evt.Y = y; evt.Button = button; evt.Buttons = buttonsMask;
                evt.ShiftKey = sh; evt.CtrlKey = ct; evt.AltKey = al; evt.MetaKey = mt;
                try {
                    Dispatch(evt, upTarget);
                    upDefaultPrevented = evt.DefaultPrevented;
                } finally {
                    ReturnPointerEvent(evt);
                }
            }

            // Input/selection audit #9: a consumed pointer-up synthesizes NO
            // click. ScrollEventHandler PreventDefaults the up that ends an
            // ARMED drag-pan / scrollbar drag precisely to suppress the click
            // (Chrome: a scroll gesture never also clicks) — but this
            // synthesis never read the flag, so panning a list and releasing
            // over a button clicked the button.
            var clickTarget = upDefaultPrevented ? null : FindNearestCommonElement(downTarget, hit);
            if (clickTarget != null) {
                // HTML spec §4.10.18.5: a click on (or inside) a disabled
                // form control fires no `click` event AND runs no activation
                // behaviour. Gate the default action alongside the dispatch
                // — without this, an `<a href>` nested in a `<button disabled>`
                // would still navigate via RunClickDefaultAction even after
                // the listener-side suppression. Dispatch() also short-circuits
                // on the same predicate; this guard exists to also cancel
                // the default action when no listeners are registered.
                if (IsClickSuppressedByDisabledAncestor(clickTarget)) {
                    // Auto-release capture on PointerUp still happens below.
                } else {
                    var click = RentPointerEvent();
                    click.Kind = EventKind.Click;
                    click.X = x; click.Y = y; click.Button = button; click.Buttons = buttonsMask;
                    click.ShiftKey = sh; click.CtrlKey = ct; click.AltKey = al; click.MetaKey = mt;
                    bool defaultPrevented;
                    try {
                        Dispatch(click, clickTarget);
                        // Capture the flag before returning to the pool — the
                        // pool will reset it on the next rent.
                        defaultPrevented = click.DefaultPrevented;
                    } finally {
                        ReturnPointerEvent(click);
                    }
                    if (!defaultPrevented) RunClickDefaultAction(clickTarget);
                }
            }

            // Auto-release capture on PointerUp — matches the browser default
            // when no explicit capture is held by JS. Controllers that need
            // multi-touch would override by re-capturing on the next down.
            if (buttonsMask == 0) pointerCaptureTarget = null;
        }

        public void DispatchWheel(double x, double y, double deltaX, double deltaY, WheelDeltaMode mode, KeyModifiers mods) {
            var hit = hitTester != null ? hitTester.HitTest(x, y) : null;
            if (hit == null) return;

            var (sh, ct, al, mt) = Split(mods);
            var evt = new WheelEvent {
                Kind = EventKind.Wheel,
                X = x, Y = y,
                DeltaX = deltaX, DeltaY = deltaY,
                DeltaMode = mode,
                ShiftKey = sh, CtrlKey = ct, AltKey = al, MetaKey = mt
            };
            Dispatch(evt, hit);
        }

        public void SetTargetFragment(string fragment) {
            if (fragment == null) fragment = "";
            if (fragment.StartsWith("#", System.StringComparison.Ordinal)) fragment = fragment.Substring(1);
            fragment = DecodeFragment(fragment);
            TargetFragment = fragment;
            var target = string.IsNullOrEmpty(fragment) ? null : doc?.GetElementById(fragment);
            state.SetTargetElement(target);
        }

        void RunClickDefaultAction(Element hit) {
            var anchor = FindAnchorWithHref(hit);
            if (anchor == null) return;
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(href) || href[0] != '#') return;
            SetTargetFragment(href);
        }

        static Element FindAnchorWithHref(Element e) {
            for (var cur = e; cur != null; cur = cur.Parent as Element) {
                if (cur.TagName == "a" && cur.HasAttribute("href")) return cur;
            }
            return null;
        }

        // HTML spec §4.10.18.5 — "disabled" form-control list. Anchor (`<a>`)
        // and `<label>` are deliberately EXCLUDED: `disabled` is not a valid
        // attribute on either per spec, so its mere presence must not cancel
        // the click. Add new tags here only if HTML adds them to the
        // "disabled" attribute group.
        static bool IsDisableableFormControl(Element e) {
            if (e == null) return false;
            switch (e.TagName) {
                case "button":
                case "fieldset":
                case "input":
                case "optgroup":
                case "option":
                case "select":
                case "textarea":
                    return true;
                default:
                    return false;
            }
        }

        // Walks the ancestor chain (inclusive of `target`) looking for a
        // disabled form control. Returns true if any ancestor is in the
        // spec's "disableable" set AND has the `disabled` attribute set —
        // matching browser behaviour where clicking a `<span>` inside a
        // `<button disabled>` does not fire `click` on the span either.
        static bool IsClickSuppressedByDisabledAncestor(Element target) {
            for (var cur = target; cur != null; cur = cur.Parent as Element) {
                if (IsDisableableFormControl(cur) && cur.HasAttribute("disabled")) return true;
            }
            return false;
        }

        static Element FindNearestCommonElement(Element a, Element b) {
            if (a == null || b == null) return null;
            for (var x = a; x != null; x = x.Parent as Element) {
                for (var y = b; y != null; y = y.Parent as Element) {
                    if (ReferenceEquals(x, y)) return x;
                }
            }
            return null;
        }

        static string DecodeFragment(string fragment) {
            if (string.IsNullOrEmpty(fragment)) return "";
            try {
                return System.Uri.UnescapeDataString(fragment);
            } catch {
                return fragment;
            }
        }

        public void DispatchPointerMove(double x, double y, KeyModifiers mods) {
            DispatchPointerMove(x, y, mods, buttonsMask);
        }

        // Variant that reports an explicit held-button mask on the move event instead of the
        // dispatcher's tracked buttonsMask. Editor panels rebuild the whole document (and this
        // dispatcher) whenever their HTML changes — which a drag controller does every frame as
        // it updates an indicator — so the rebuilt dispatcher's buttonsMask resets to 0 mid-drag.
        // The panel knows the true state from the IMGUI event type (MouseDrag = held, MouseMove =
        // not) and passes it here so PointerEvent.Buttons stays accurate across those rebuilds.
        public void DispatchPointerMove(double x, double y, KeyModifiers mods, int buttons) {
            var hit = hitTester != null ? hitTester.HitTest(x, y) : null;
            UpdateHover(hit, x, y, mods);

            var (sh, ct, al, mt) = Split(mods);
            // While a pointer is captured, route move events to the capture
            // target instead of the hit-tested element. Lets drag controllers
            // keep tracking the pointer outside the source element's bounds
            // (slider thumbs released over empty space, pan gestures that
            // wander off the gesture surface, etc.).
            Element moveTarget = pointerCaptureTarget ?? hit;
            if (moveTarget != null) {
                var move = RentPointerEvent();
                move.Kind = EventKind.PointerMove;
                move.X = x; move.Y = y; move.Button = -1; move.Buttons = buttons;
                move.ShiftKey = sh; move.CtrlKey = ct; move.AltKey = al; move.MetaKey = mt;
                try {
                    Dispatch(move, moveTarget);
                } finally {
                    ReturnPointerEvent(move);
                }
            }
        }

        void UpdateHover(Element newHit, double x, double y, KeyModifiers mods) {
            if (newHit == hovered) {
                if (newHit != null) {
                    // Pooled BuildPath: SetHoverChain only reads the chain
                    // synchronously, so the list can be returned immediately.
                    var path = RentPath(newHit);
                    try {
                        state.SetHoverChain(path);
                    } finally {
                        ReturnPathList(path);
                    }
                }
                return;
            }

            var (sh, ct, al, mt) = Split(mods);

            var oldChain = hovered != null ? RentPath(hovered) : RentPath(null);
            var newChain = newHit != null ? RentPath(newHit) : RentPath(null);
            try {
                int common = 0;
                while (common < oldChain.Count && common < newChain.Count && oldChain[common] == newChain[common]) common++;

                for (int i = oldChain.Count - 1; i >= common; i--) {
                    var leave = RentPointerEvent();
                    leave.Kind = EventKind.PointerLeave;
                    leave.X = x; leave.Y = y; leave.Button = -1; leave.Buttons = buttonsMask;
                    leave.ShiftKey = sh; leave.CtrlKey = ct; leave.AltKey = al; leave.MetaKey = mt;
                    leave.Bubbles = false;
                    try {
                        DispatchSingle(leave, oldChain[i]);
                    } finally {
                        ReturnPointerEvent(leave);
                    }
                }

                for (int i = common; i < newChain.Count; i++) {
                    var enter = RentPointerEvent();
                    enter.Kind = EventKind.PointerEnter;
                    enter.X = x; enter.Y = y; enter.Button = -1; enter.Buttons = buttonsMask;
                    enter.ShiftKey = sh; enter.CtrlKey = ct; enter.AltKey = al; enter.MetaKey = mt;
                    enter.Bubbles = false;
                    try {
                        DispatchSingle(enter, newChain[i]);
                    } finally {
                        ReturnPointerEvent(enter);
                    }
                }

                hovered = newHit;
                state.SetHoverChain(newChain);
            } finally {
                ReturnPathList(oldChain);
                ReturnPathList(newChain);
            }
        }

        void DispatchSingle(UIEvent evt, Element target) {
            if (target == null) return;
            evt.Target = target;
            evt.TimestampSeconds = clock.NowSeconds;
            evt.Phase = EventPhase.AtTarget;
            evt.CurrentTarget = target;
            // Per-element fanout for non-bubbling events (pointerenter /
            // pointerleave): each element along the entered/left chain
            // gets the event ONCE. `useCapture: true` listeners are
            // intended for the capture-phase walk and are deliberately
            // SKIPPED here — the per-element model has no capture walk.
            // The EventDispatcherTests.PointerEnter_and_PointerLeave
            // _do_not_bubble test pins this semantic. A strict DOM-L3
            // §3.1 reading would also fire capture listeners at AtTarget,
            // but the engine has chosen the per-element design.
            InvokeListeners(target, evt, EventPhase.Bubble);
        }

        public void DispatchKeyDown(string key, string code, KeyModifiers mods, bool repeat) {
            var (sh, ct, al, mt) = Split(mods);
            var target = focused ?? RootElement;
            if (target == null) return;

            var evt = new KeyboardEvent {
                Kind = EventKind.KeyDown,
                Key = key, Code = code,
                ShiftKey = sh, CtrlKey = ct, AltKey = al, MetaKey = mt,
                Repeat = repeat
            };
            Dispatch(evt, target);

            if (!evt.DefaultPrevented && key == "Tab") {
                AdvanceFocusByKeyboard(reverse: sh);
            } else if (!evt.DefaultPrevented && ArrowKeySpatialNavigation && !IsTextEditingTarget(focused)) {
                // W3 phase 2 — arrow keys drive spatial focus navigation
                // (game-UI convention; gamepad d-pad/stick feeds the same
                // entry point via NavigateFocusSpatially). Listener-level
                // preventDefault opts out per element; text-editing targets
                // keep arrows for caret movement.
                switch (key) {
                    case "ArrowUp": NavigateFocusSpatially(SpatialDirection.Up); break;
                    case "ArrowDown": NavigateFocusSpatially(SpatialDirection.Down); break;
                    case "ArrowLeft": NavigateFocusSpatially(SpatialDirection.Left); break;
                    case "ArrowRight": NavigateFocusSpatially(SpatialDirection.Right); break;
                }
            }
        }

        // ---- W3: spatial focus navigation ------------------------------
        // Geometry providers wired by UIDocumentBuilder (the dispatcher is
        // deliberately box-tree-agnostic otherwise; null providers = spatial
        // nav disabled, e.g. bare-dispatcher unit tests).
        public Func<Element, Weva.Layout.Boxes.Box> ElementToBox { get; set; }
        public Func<Weva.Layout.Boxes.Box> RootBoxProvider { get; set; }
        // Default ON: arrows move focus like a controller d-pad. Hosts
        // building browser-like experiences can turn it off.
        public bool ArrowKeySpatialNavigation { get; set; } = true;

        // Moves focus to the spatially-best candidate in `dir` from the
        // currently-focused element, marking the move keyboard-driven so
        // :focus-visible applies (same contract as Tab). With nothing
        // focused, falls back to the first focusable (document order) so a
        // first d-pad press "enters" the UI. Returns true when focus moved.
        public bool NavigateFocusSpatially(SpatialDirection dir) {
            var root = RootBoxProvider?.Invoke();
            if (root == null) return false;
            if (focused == null) {
                AdvanceFocusByKeyboard(reverse: false);
                return focused != null;
            }
            var currentBox = ElementToBox?.Invoke(focused);
            if (currentBox == null) return false;
            var next = SpatialNavigator.FindNext(root, currentBox, dir);
            if (next?.Element == null) return false;
            FocusInternal(next.Element, keyboard: true);
            return true;
        }

        // Arrow keys belong to the caret when a text-editing element is
        // focused (HTML-AAM convention). Tag check mirrors FocusFromPointer's
        // pragmatism — contenteditable is out of scope until the engine
        // grows it.
        static bool IsTextEditingTarget(Element e) {
            if (e == null) return false;
            string tag = e.TagName;
            return tag == "input" || tag == "textarea" || tag == "select";
        }

        public void DispatchKeyUp(string key, string code, KeyModifiers mods, bool repeat) {
            var (sh, ct, al, mt) = Split(mods);
            var target = focused ?? RootElement;
            if (target == null) return;

            var evt = new KeyboardEvent {
                Kind = EventKind.KeyUp,
                Key = key, Code = code,
                ShiftKey = sh, CtrlKey = ct, AltKey = al, MetaKey = mt,
                Repeat = repeat
            };
            Dispatch(evt, target);
        }

        void AdvanceFocusByKeyboard(bool reverse) {
            var next = focusManager.NextFocusable(doc, focused, reverse);
            if (next == null) return;
            FocusInternal(next, keyboard: true);
        }

        void FocusFromPointer(Element hit) {
            Element focusTarget = null;
            for (var n = hit; n != null; n = n.Parent as Element) {
                if (focusManager.IsFocusable(n)) { focusTarget = n; break; }
            }
            if (focusTarget != null) {
                FocusInternal(focusTarget, keyboard: false);
            } else {
                FocusInternal(null, keyboard: false);
            }
        }

        public void Focus(Element e) {
            FocusInternal(e, keyboard: false);
        }

        void FocusInternal(Element next, bool keyboard) {
            if (next != null && !focusManager.IsProgrammaticallyFocusable(next)) {
                next = null;
            }
            if (next == focused) {
                if (next != null) {
                    state.SetFlag(next, ElementState.FocusVisible, keyboard);
                    focusVisible = keyboard;
                    var path = RentPath(next);
                    try { state.SetFocusWithinChain(path); }
                    finally { ReturnPathList(path); }
                }
                return;
            }

            var previous = focused;
            if (previous != null) {
                state.SetFlag(previous, ElementState.Focus, false);
                state.SetFlag(previous, ElementState.FocusVisible, false);
                var blur = new FocusEvent { Kind = EventKind.Blur, RelatedTarget = next };
                Dispatch(blur, previous);
            }

            focused = next;
            focusVisible = keyboard;

            if (next != null) {
                state.SetFlag(next, ElementState.Focus, true);
                state.SetFlag(next, ElementState.FocusVisible, keyboard);
                var fe = new FocusEvent { Kind = EventKind.Focus, RelatedTarget = previous };
                Dispatch(fe, next);
            } else {
                state.ClearFlagEverywhere(ElementState.FocusVisible);
            }

            if (next != null) {
                var path = RentPath(next);
                try { state.SetFocusWithinChain(path); }
                finally { ReturnPathList(path); }
            } else {
                state.SetFocusWithinChain(null);
            }
        }

        public void Tick(double currentTimeSeconds) {
            if (clock is FakeUIClock fc) fc.Set(currentTimeSeconds);
        }

        // Synthetic event dispatch for form-control event triggers
        // (input/change/submit) and any other publisher that already has a
        // pre-built UIEvent and a target element. Routes through the same
        // capture/target/bubble pipeline as native events.
        public void DispatchSynthetic(UIEvent evt, Element target) {
            if (evt == null || target == null) return;
            Dispatch(evt, target);
        }

        internal FocusManager FocusManagerForTests => focusManager;

        // Test-only accessor for the MS1 leak-regression suite. Production
        // code goes through Add/RemoveEventListener which already encapsulate
        // the map.
        internal EventListeners ListenersForTests => listeners;
    }
}
