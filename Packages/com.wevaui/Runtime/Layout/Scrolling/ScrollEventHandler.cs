using System;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling.Smooth;
using Weva.Layout.Scrolling.Snap;
using Weva.Profiling;
using Weva.Reactive;

namespace Weva.Layout.Scrolling {
    // Bridge between UIEvent dispatch and ScrollContainer mutation. Subscribes
    // to wheel and arrow-key events on a Document, walks from the event's
    // target up to the nearest scroll container Box (overflow != visible), then
    // mutates that Box's ScrollState.
    //
    // The handler is detached by the caller via Dispose. It's safe to recreate
    // per Layout pass; it doesn't keep references to Boxes from previous frames.
    public sealed class ScrollEventHandler : IDisposable {
        public const double SnapSettleSeconds = 0.1;

        readonly EventDispatcher dispatcher;
        readonly Document document;
        readonly ScrollContainer container;
        readonly Func<Element, Box> elementToBox;
        readonly Func<double> fontSizeProvider;
        readonly Func<double> nowProvider;
        readonly Action<Element> invalidatePaint;

        readonly EventListener onWheel;
        readonly EventListener onKey;
        readonly EventListener onPointerDown;
        readonly EventListener onPointerMove;
        readonly EventListener onPointerUp;

        SmoothScrollAnimator smoothAnimator;
        SnapResolver snapResolver;
        ScrollMomentum momentumAnimator;
        readonly System.Collections.Generic.Dictionary<Box, double> lastWheelTime = new();
        readonly System.Collections.Generic.Dictionary<Box, (double x, double y)> wheelTargets = new();
        readonly System.Collections.Generic.Dictionary<Box, (double x, double y)> wheelStartPositions = new();
        ScrollbarDrag activeScrollbarDrag;

        // Touch/flick drag tracking state (separate from scrollbar drag).
        // Only one touch drag is active at a time (pointer-capture semantics).
        //
        // Slop arming (browser touch semantics): pointer-down only records a
        // CANDIDATE. The drag ARMS — starts scrolling, takes pointer capture,
        // and consumes events — once the pointer travels TouchDragSlopPx from
        // the down point. Below the slop, taps on buttons inside scrollables
        // click through untouched; past it, the gesture is a scroll and the
        // pointer-up suppresses the click (PreventDefault), matching Chrome.
        //
        // Rubber-band overscroll: when the content reaches an edge, further
        // drag movement accumulates as raw overscroll (RawOverX/Y). The
        // actual ScrollX/Y is set to the rubber-banded visual position which
        // may temporarily be outside [0, MaxScroll]. On release, StartGlide
        // forwards the raw overscroll so spring-back can be triggered.
        struct TouchDrag {
            public bool Active;
            public bool Armed;    // moved past the slop — scrolling + consuming events
            public Box Box;
            public double StartX; // pointer-down position (slop reference)
            public double StartY;
            public double LastX;  // last pointer position in screen coords
            public double LastY;
            // Raw (unrubberized) overscroll accumulated past each edge.
            // Positive = past bottom/right; negative = past top/left.
            // Zero means the content is in-bounds on that axis.
            public double RawOverX;
            public double RawOverY;
        }
        TouchDrag activeTouchDrag;

        // Chrome's touch slop is ~10 device px on Android, ~8 on desktop
        // touch; 8 keeps short flicks responsive without eating taps.
        public static double TouchDragSlopPx = 8.0;

        // Pointer-drag scrolling of the VIEWPORT (the whole document) — the
        // "drag anywhere to pan the whole UI" gesture. OFF by default because it
        // is disruptive for in-game HUDs: any drag over empty space slides (or
        // rubber-bands) the entire UI. Element-level scroll containers
        // (overflow:scroll/auto) still drag-scroll regardless of this flag, and
        // wheel + scrollbar scrolling of the viewport are unaffected. Set true to
        // opt back into mobile/touch-style whole-page drag scrolling.
        public static bool EnableViewportDragScroll = false;

        // Rubber-band overscroll on touch-drag: when true (default), dragging a
        // scroll container past its content edge stretches with iOS-style
        // resistance and springs back. Set false to clamp the drag hard at the
        // bounds — no panning past the edge. Wheel/scrollbar are unaffected.
        public static bool RubberBandOverscroll = true;

        public ScrollEventHandler(
            EventDispatcher dispatcher,
            Document document,
            ScrollContainer container,
            Func<Element, Box> elementToBox,
            Func<double> fontSizeProvider)
            : this(dispatcher, document, container, elementToBox, fontSizeProvider, null) { }

        public ScrollEventHandler(
            EventDispatcher dispatcher,
            Document document,
            ScrollContainer container,
            Func<Element, Box> elementToBox,
            Func<double> fontSizeProvider,
            Func<double> nowProvider)
            : this(dispatcher, document, container, elementToBox, fontSizeProvider, nowProvider, null) { }

        public ScrollEventHandler(
            EventDispatcher dispatcher,
            Document document,
            ScrollContainer container,
            Func<Element, Box> elementToBox,
            Func<double> fontSizeProvider,
            Func<double> nowProvider,
            Action<Element> invalidatePaint) {
            this.dispatcher = dispatcher;
            this.document = document;
            this.container = container;
            this.elementToBox = elementToBox;
            this.fontSizeProvider = fontSizeProvider ?? (() => 16);
            this.nowProvider = nowProvider ?? (() => 0);
            this.invalidatePaint = invalidatePaint;
            SmoothScrollProperties.EnsureRegistered();
            ScrollSnapProperties.EnsureRegistered();

            onWheel = HandleWheel;
            onKey = HandleKey;
            onPointerDown = HandlePointerDown;
            onPointerMove = HandlePointerMove;
            onPointerUp = HandlePointerUp;

            // Attach by listening at the document root via the dispatcher path:
            // EventDispatcher dispatches based on element targets so a single
            // listener at the root receives bubbled events from every descendant.
            var root = RootElement(document);
            if (root != null && dispatcher != null) {
                dispatcher.AddEventListener(root, EventKind.Wheel, onWheel, useCapture: false);
                dispatcher.AddEventListener(root, EventKind.KeyDown, onKey, useCapture: false);
                dispatcher.AddEventListener(root, EventKind.PointerDown, onPointerDown, useCapture: true);
                dispatcher.AddEventListener(root, EventKind.PointerMove, onPointerMove, useCapture: true);
                dispatcher.AddEventListener(root, EventKind.PointerUp, onPointerUp, useCapture: true);
            }
        }

        public void Dispose() {
            var root = RootElement(document);
            if (root != null && dispatcher != null) {
                dispatcher.RemoveEventListener(root, EventKind.Wheel, onWheel, useCapture: false);
                dispatcher.RemoveEventListener(root, EventKind.KeyDown, onKey, useCapture: false);
                dispatcher.RemoveEventListener(root, EventKind.PointerDown, onPointerDown, useCapture: true);
                dispatcher.RemoveEventListener(root, EventKind.PointerMove, onPointerMove, useCapture: true);
                dispatcher.RemoveEventListener(root, EventKind.PointerUp, onPointerUp, useCapture: true);
            }
        }

        public SmoothScrollAnimator SmoothAnimator {
            get => smoothAnimator;
            set => smoothAnimator = value;
        }

        public SnapResolver SnapResolver {
            get => snapResolver;
            set => snapResolver = value;
        }

        public ScrollMomentum MomentumAnimator {
            get => momentumAnimator;
            set => momentumAnimator = value;
        }

        // The viewport scroll root box (the anonymous root box returned by
        // ScrollLayout.ViewportScrollRoot). When set, unconsumed wheel events
        // that find no inner scroll container ancestor fall through to this box
        // so the viewport itself scrolls. Updated by UIDocumentLifecycle after
        // each layout pass via LayoutEngine.ScrollContainer / ScrollLayout.
        // Null when no viewport scroll state is active.
        public Box ViewportRoot { get; set; }

        // Resolves snap state on a container regardless of whether we observed
        // a wheel event (e.g. after programmatic scroll, after layout, on first
        // settle). Caller is the lifecycle/test driver.
        public void SettleSnap(Box box) {
            if (box == null || snapResolver == null) return;
            var type = SnapResolver.ResolveType(box);
            if (!type.IsActive) return;
            var state = container.GetOrCreate(box);
            double curY = state.ScrollY;
            double curX = state.ScrollX;
            double startX = curX, startY = curY;
            if (wheelStartPositions.TryGetValue(box, out var s)) {
                startX = s.x;
                startY = s.y;
            }
            bool didY = type.Axis != SnapAxis.X && snapResolver.TryFindSnapTargetY(box, startY, curY, type, out curY);
            bool didX = type.Axis != SnapAxis.Y && snapResolver.TryFindSnapTargetX(box, startX, curX, type, out curX);
            if (!didX && !didY) return;
            ScrollTarget(box, curX, curY, null);
        }

        // Per-frame: caller invokes this with current time so any container that
        // saw a wheel burst more than SnapSettleSeconds ago has its snap target
        // resolved. Idempotent — repeated calls after settle are no-ops.
        public void TickSnap(double nowSeconds) {
            using (PerfMarkerScope.Auto(UIProfilerMarkers.ScrollTick)) {
                if (snapResolver == null || lastWheelTime.Count == 0) return;
                System.Collections.Generic.List<Box> done = null;
                foreach (var kv in lastWheelTime) {
                    if (nowSeconds - kv.Value < SnapSettleSeconds) continue;
                    done ??= new System.Collections.Generic.List<Box>();
                    done.Add(kv.Key);
                }
                if (done == null) return;
                foreach (var b in done) {
                    lastWheelTime.Remove(b);
                    wheelTargets.Remove(b);
                    SettleSnap(b);
                    wheelStartPositions.Remove(b);
                }
            }
        }

        // True when the pointer-down landed on (or inside) a control that drives
        // itself off the raw pointer stream:
        //   - <input type="range">: dragging the thumb adjusts the value;
        //   - text-editing controls (text-like <input> and <textarea>): a drag
        //     is a SELECTION sweep. Chrome never pans the page from a drag
        //     that starts inside a text control (in-editor find: selecting
        //     more than TouchDragSlopPx of text armed the container drag,
        //     which took pointer capture and consumed every later move — the
        //     selection froze at ~8px and the pointer-up's click was
        //     suppressed).
        // Such controls own their drag; the container drag-scroll must yield so
        // the page doesn't pan while the user is operating the control.
        static bool TargetOwnsPointerDrag(Element target) {
            for (Node n = target; n != null; n = n.Parent) {
                if (!(n is Element e)) continue;
                if (string.Equals(e.TagName, "textarea", StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.Equals(e.TagName, "input", StringComparison.OrdinalIgnoreCase)) continue;
                var type = e.GetAttribute("type") ?? "text";
                // Everything except the click-activated types edits text or
                // owns a drag (range). checkbox/radio/button/etc. don't own
                // drags — a drag from them may still pan (touch semantics).
                if (!(string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "button", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "submit", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "reset", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "file", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))) {
                    return true;
                }
            }
            return false;
        }

        static Element RootElement(Document doc) {
            if (doc == null) return null;
            foreach (var c in doc.Children) {
                if (c is Element e) return e;
            }
            return null;
        }

        void HandleWheel(UIEvent e) {
            if (!(e is WheelEvent w)) return;
            var box = NearestScrollContainer(w.Target);
            if (box == null) return;
            var state = container.GetOrCreate(box);

            double dx = w.DeltaX;
            double dy = w.DeltaY;
            if (w.DeltaMode == WheelDeltaMode.Line) {
                double line = ScrollMath.LineStep(fontSizeProvider());
                dx *= line;
                dy *= line;
            } else if (w.DeltaMode == WheelDeltaMode.Page) {
                dx *= ScrollMath.PageStep(state.ViewportWidth);
                dy *= ScrollMath.PageStep(state.ViewportHeight);
            }

            ApplyDelta(box, state, dx, dy, w);
        }

        void HandleKey(UIEvent e) {
            if (!(e is KeyboardEvent k)) return;
            // A key the focused control already consumed must not ALSO scroll
            // (input/selection audit #1): InputController handles Arrow/Home/
            // End with PreventDefault(), but the event still bubbles to this
            // root listener — without this gate, pressing End inside a text
            // field jumped the page to the bottom while the caret moved to
            // the end of the value. Chrome never scrolls for keys consumed
            // by a focused editable. Same convention as the dispatcher's
            // Tab/spatial-nav gate.
            if (k.DefaultPrevented) return;
            // Only steal keys when the focused element resolves to a scroll
            // container — otherwise typing into a text input would bubble and
            // scroll the page. Scroll keys aren't dispatched to non-focused
            // scroll containers; we check the keyboard event's target and walk
            // up looking for a scroll-container ancestor.
            var box = NearestScrollContainer(k.Target);
            if (box == null) return;
            var state = container.GetOrCreate(box);

            double line = ScrollMath.LineStep(fontSizeProvider());
            bool handled = true;
            switch (k.Key) {
                case "ArrowDown":   ApplyDelta(box, state, 0, line, k); break;
                case "ArrowUp":     ApplyDelta(box, state, 0, -line, k); break;
                case "ArrowRight":  ApplyDelta(box, state, line, 0, k); break;
                case "ArrowLeft":   ApplyDelta(box, state, -line, 0, k); break;
                case "PageDown":    ApplyDelta(box, state, 0, ScrollMath.PageStep(state.ViewportHeight), k); break;
                case "PageUp":      ApplyDelta(box, state, 0, -ScrollMath.PageStep(state.ViewportHeight), k); break;
                case "Home":        SetScroll(box, state, state.ScrollX, 0, k); break;
                case "End":         SetScroll(box, state, state.ScrollX, state.MaxScrollY, k); break;
                default: handled = false; break;
            }
            if (handled) SettleSnap(box);
        }

        void ApplyDelta(Box box, ScrollState state, double dx, double dy, UIEvent source) {
            // Decide whether this scroll container actually consumes the delta
            // along each axis. If it can't (already at the relevant edge or no
            // overflow on that axis), the event keeps bubbling — caller above
            // gets a chance to scroll its own ancestor container. We re-walk
            // from the original target only when neither axis was consumed
            // here, to keep nested-container semantics working.
            // For smooth-behavior containers we accumulate against the in-flight
            // target so successive wheel ticks don't reset the ramp.
            bool isSmooth = smoothAnimator != null && SmoothScrollAnimator.IsSmooth(box);

            double baseX = state.ScrollX;
            double baseY = state.ScrollY;
            if (isSmooth && wheelTargets.TryGetValue(box, out var tgt)) {
                // Clamp the cached target against the CURRENT max — between
                // wheel ticks, layout may have shrunk the content so the
                // prior target now sits past MaxScrollX/Y. Without this,
                // the next wheel tick wastes its delta walking baseY back
                // toward the now-valid range and the user feels a dead
                // first tick.
                baseX = ScrollMath.Clamp(tgt.x, 0, state.MaxScrollX);
                baseY = ScrollMath.Clamp(tgt.y, 0, state.MaxScrollY);
            }

            bool consumedX = false, consumedY = false;
            double newX = baseX, newY = baseY;

            if (state.CanScrollX && dx != 0) {
                double tentative = ScrollMath.Clamp(baseX + dx, 0, state.MaxScrollX);
                if (tentative != baseX) {
                    newX = tentative;
                    consumedX = true;
                }
            }
            if (state.CanScrollY && dy != 0) {
                double tentative = ScrollMath.Clamp(baseY + dy, 0, state.MaxScrollY);
                if (tentative != baseY) {
                    newY = tentative;
                    consumedY = true;
                }
            }

            if (consumedX || consumedY) {
                if (source is WheelEvent) {
                    if (!lastWheelTime.ContainsKey(box)) {
                        wheelStartPositions[box] = (state.ScrollX, state.ScrollY);
                    }
                    lastWheelTime[box] = nowProvider();
                    wheelTargets[box] = (newX, newY);
                }
                if (isSmooth) {
                    smoothAnimator.Animate(box, newX, newY);
                } else {
                    SetImmediate(box, state, newX, newY);
                }
                source?.PreventDefault();
                source?.StopPropagation();

                // Residual on the un-consumed axis bubbles per wheel-event spec:
                // a horizontal-only scroller mustn't kill ancestor vertical scroll.
                // CSS Overscroll Behavior 1 — gate each residual axis on its
                // own `overscroll-behavior-x|-y` longhand so a containment on
                // one axis doesn't suppress the other.
                double residualX = consumedX ? 0 : dx;
                double residualY = consumedY ? 0 : dy;
                if (ScrollMath.ShouldContainOverscroll(box, ScrollMath.ScrollAxis.X)) residualX = 0;
                if (ScrollMath.ShouldContainOverscroll(box, ScrollMath.ScrollAxis.Y)) residualY = 0;
                if (residualX == 0 && residualY == 0) return;
                if (box.Parent != null) {
                    var ancestor = NearestScrollContainerStartingAt(box.Parent);
                    if (ancestor != null) {
                        ApplyDelta(ancestor, container.GetOrCreate(ancestor), residualX, residualY, source);
                    }
                }
                return;
            }

            // Bubble: walk up to the next scroll-container ancestor and try again.
            // Gate each axis independently against `overscroll-behavior-x|-y`.
            double bubbleX = ScrollMath.ShouldContainOverscroll(box, ScrollMath.ScrollAxis.X) ? 0 : dx;
            double bubbleY = ScrollMath.ShouldContainOverscroll(box, ScrollMath.ScrollAxis.Y) ? 0 : dy;
            if (bubbleX == 0 && bubbleY == 0) return;
            if (box.Parent != null) {
                var ancestor = NearestScrollContainerStartingAt(box.Parent);
                if (ancestor != null) {
                    ApplyDelta(ancestor, container.GetOrCreate(ancestor), bubbleX, bubbleY, source);
                }
            }
        }

        void SetScroll(Box box, ScrollState state, double x, double y, UIEvent source) {
            x = ScrollMath.Clamp(x, 0, state.MaxScrollX);
            y = ScrollMath.Clamp(y, 0, state.MaxScrollY);
            bool isSmooth = smoothAnimator != null && SmoothScrollAnimator.IsSmooth(box);
            if (!isSmooth && x == state.ScrollX && y == state.ScrollY) return;
            if (isSmooth) {
                smoothAnimator.Animate(box, x, y);
            } else {
                SetImmediate(box, state, x, y);
            }
            source?.PreventDefault();
            source?.StopPropagation();
        }

        // Programmatic / snap-driven retarget that always goes through the
        // smooth animator if attached + container is smooth, regardless of
        // SetScroll's "no-op when equal" gate.
        void ScrollTarget(Box box, double x, double y, UIEvent source) {
            var state = container.GetOrCreate(box);
            x = ScrollMath.Clamp(x, 0, state.MaxScrollX);
            y = ScrollMath.Clamp(y, 0, state.MaxScrollY);
            bool isSmooth = smoothAnimator != null && SmoothScrollAnimator.IsSmooth(box);
            if (isSmooth) {
                smoothAnimator.Animate(box, x, y);
            } else {
                if (x == state.ScrollX && y == state.ScrollY) return;
                SetImmediate(box, state, x, y);
            }
            source?.PreventDefault();
            source?.StopPropagation();
        }

        void HandlePointerDown(UIEvent e) {
            if (!(e is PointerEvent p) || p.Button != 0) return;

            // Cancel any in-flight momentum glide when a new touch begins.
            // Walk from the hit element up to find a scroll container to cancel.
            if (momentumAnimator != null) {
                var cancelBox = NearestScrollContainer(p.Target);
                if (cancelBox != null) momentumAnimator.Cancel(cancelBox);
            }

            var hit = FindScrollbarAt(p.X, p.Y);
            if (hit.Valid) {
                // Scrollbar thumb drag — takes priority over touch drag.
                double pointer = hit.Axis == ScrollbarAxis.Vertical ? p.Y : p.X;
                double offset = pointer >= hit.ThumbStart && pointer <= hit.ThumbStart + hit.ThumbLength
                    ? pointer - hit.ThumbStart
                    : hit.ThumbLength * 0.5;
                activeScrollbarDrag = new ScrollbarDrag {
                    Box = hit.Box,
                    State = hit.State,
                    Axis = hit.Axis,
                    TrackStart = hit.TrackStart,
                    TrackTravel = hit.TrackLength - hit.ThumbLength,
                    ThumbOffset = offset
                };
                // Mark the thumb as active (::-webkit-scrollbar-thumb:active) for the
                // duration of the drag. Chrome keeps hover AND active styles while dragging.
                if (hit.State != null) {
                    if (hit.Axis == ScrollbarAxis.Vertical) {
                        hit.State.ThumbActiveY = true;
                        hit.State.ThumbHoveredY = true;
                    } else {
                        hit.State.ThumbActiveX = true;
                        hit.State.ThumbHoveredX = true;
                    }
                }
                ApplyScrollbarDrag(pointer);
                // NG1: SetPointerCapture throws on null — guard the root lookup
                // so an empty document (no first-Element child) doesn't take down
                // the scrollbar drag.
                var captureRoot = RootElement(document);
                if (dispatcher != null && captureRoot != null) {
                    dispatcher.SetPointerCapture(captureRoot);
                }
                p.PreventDefault();
                p.StopPropagation();
                return;
            }

            // A control that handles its OWN pointer-drag (a range slider thumb)
            // must not also arm a container drag-scroll, or dragging the slider
            // pans the page. This handler runs in the CAPTURE phase (root→target),
            // BEFORE the control's bubble-phase listener, so the control's
            // StopPropagation can't retroactively cancel an armed drag — we have
            // to proactively skip arming here.
            if (TargetOwnsPointerDrag(p.Target)) return;

            // Touch/flick drag: record a CANDIDATE only — no capture, no
            // PreventDefault, so taps inside scrollables click through. The
            // drag arms in HandlePointerMove once the slop is exceeded.
            if (momentumAnimator != null) {
                var touchBox = NearestScrollContainer(p.Target);
                if (touchBox != null) {
                    var state = container.GetOrCreate(touchBox);
                    // Only start a drag-scroll on a container that can ACTUALLY
                    // scroll right now — i.e. it has scrollable extent on some
                    // axis (MaxScroll > 0). Without this, a full-bleed
                    // overflow:hidden panel (or any container whose content fits)
                    // arms a drag and RUBBER-BANDS the whole UI past its bounds
                    // with nothing to scroll. Browsers only pan scroll/auto
                    // content that overflows; matching that kills the spurious
                    // "drag the screen past bounds" gesture in HUDs.
                    bool canScroll = state != null && (state.CanScrollX || state.CanScrollY);
                    // The viewport (whole document) is additionally opt-in: even
                    // when it overflows, drag-pan stays off unless enabled.
                    bool viewportBlocked =
                        ReferenceEquals(touchBox, ViewportRoot) && !EnableViewportDragScroll;
                    if (canScroll && !viewportBlocked) {
                        momentumAnimator.AddSample(touchBox, nowProvider(), state.ScrollX, state.ScrollY);
                        activeTouchDrag = new TouchDrag {
                            Active = true,
                            Armed = false,
                            Box = touchBox,
                            StartX = p.X,
                            StartY = p.Y,
                            LastX = p.X,
                            LastY = p.Y
                        };
                    }
                }
            }
        }

        void HandlePointerMove(UIEvent e) {
            if (!(e is PointerEvent p)) return;

            // Update per-axis thumb hover flags for ::-webkit-scrollbar-thumb:hover.
            // We run this before the drag-priority check so that the hover flag is
            // accurate even during scrollbar drags (Chrome keeps hover+active during drag).
            UpdateThumbHoverState(p.X, p.Y);

            // Scrollbar drag takes priority.
            if (activeScrollbarDrag.Valid) {
                double pointer = activeScrollbarDrag.Axis == ScrollbarAxis.Vertical ? p.Y : p.X;
                ApplyScrollbarDrag(pointer);
                p.PreventDefault();
                p.StopPropagation();
                return;
            }

            // Touch/flick drag: accumulate samples and scroll the container.
            if (activeTouchDrag.Active && momentumAnimator != null) {
                var box = activeTouchDrag.Box;
                var state = container?.Get(box);
                if (box != null && state != null) {
                    // Slop gate: below the threshold this is still a tap —
                    // don't scroll, don't capture, don't consume the event.
                    if (!activeTouchDrag.Armed) {
                        double travelX = System.Math.Abs(p.X - activeTouchDrag.StartX);
                        double travelY = System.Math.Abs(p.Y - activeTouchDrag.StartY);
                        if (System.Math.Max(travelX, travelY) < TouchDragSlopPx) return;
                        activeTouchDrag.Armed = true;
                        // Capture so fast pointer moves can't escape the
                        // container mid-drag (mirrors the scrollbar drag's
                        // NG1-guarded capture).
                        var captureEl = box.Element ?? RootElement(document);
                        if (dispatcher != null && captureEl != null) {
                            dispatcher.SetPointerCapture(captureEl);
                        }
                    }
                    double dx = -(p.X - activeTouchDrag.LastX);
                    double dy = -(p.Y - activeTouchDrag.LastY);
                    activeTouchDrag.LastX = p.X;
                    activeTouchDrag.LastY = p.Y;

                    // Apply movement with rubber-band resistance past edges.
                    double rawOverX = activeTouchDrag.RawOverX;
                    double rawOverY = activeTouchDrag.RawOverY;

                    // Only pan an axis the box can ACTUALLY user-scroll.
                    // CanScrollX/Y already require overflow:scroll|auto on that
                    // axis (not visible/hidden/clip) AND real content overflow —
                    // the SAME gate the wheel path uses. A Y-only
                    // (overflow-y:auto) container keeps overflow-x:`visible`
                    // (ResolveOverflow reads the longhands; it does not apply the
                    // §3 visible→auto cross-propagation), so CanScrollX is false
                    // and an X drag is locked — even though the reserved vertical
                    // scrollbar leaves a ≤scrollbar-thickness spurious MaxScrollX.
                    //
                    // For a both-axes overflow:auto container that overflows an
                    // axis by ONLY that ≤scrollbar-thickness gutter divergence,
                    // also require travel beyond the gutter so the artifact can't
                    // drag-pan the incidental axis.
                    double gutter = ScrollMath.ResolveScrollbarThickness(box);
                    double xSlop = state.CanScrollY ? gutter : 0;
                    double ySlop = state.CanScrollX ? gutter : 0;
                    double newX, newY;
                    if (state.CanScrollX && state.MaxScrollX > xSlop + 0.0001) {
                        ApplyRubberBandMove(state, dx, ref rawOverX, out newX);
                    } else {
                        newX = state.ScrollX; rawOverX = 0;
                    }
                    if (state.CanScrollY && state.MaxScrollY > ySlop + 0.0001) {
                        ApplyRubberBandMove(state, dy, ref rawOverY, out newY, isVertical: true);
                    } else {
                        newY = state.ScrollY; rawOverY = 0;
                    }

                    activeTouchDrag.RawOverX = rawOverX;
                    activeTouchDrag.RawOverY = rawOverY;

                    SetImmediateRaw(box, state, newX, newY);

                    // Record sample for velocity estimation (use clamped position
                    // so velocity estimate reflects in-bounds intent).
                    double sampleX = ScrollMath.Clamp(newX, 0, state.MaxScrollX);
                    double sampleY = ScrollMath.Clamp(newY, 0, state.MaxScrollY);
                    momentumAnimator.AddSample(box, nowProvider(), sampleX, sampleY);

                    // An armed drag owns the pointer stream — other listeners
                    // must not also react to these moves.
                    p.PreventDefault();
                    p.StopPropagation();
                }
            }
        }

        void HandlePointerUp(UIEvent e) {
            if (activeScrollbarDrag.Valid) {
                if (e is PointerEvent p2) {
                    p2.PreventDefault();
                    p2.StopPropagation();
                }
                var draggedBox = activeScrollbarDrag.Box;
                var draggedState = activeScrollbarDrag.State;
                var draggedAxis = activeScrollbarDrag.Axis;
                // Clear :active state — drag is ending. Hover state is cleared
                // when the pointer leaves the thumb on the next move event.
                if (draggedState != null) {
                    if (draggedAxis == ScrollbarAxis.Vertical) {
                        draggedState.ThumbActiveY = false;
                    } else {
                        draggedState.ThumbActiveX = false;
                    }
                }
                dispatcher?.ReleasePointerCapture(RootElement(document));
                activeScrollbarDrag = default;
                SettleSnap(draggedBox);
                return;
            }

            // Touch/flick drag release: start momentum glide. Only an ARMED
            // drag consumed the gesture — release its capture and suppress
            // the click (Chrome: a scroll gesture never also clicks). An
            // un-armed candidate was just a tap; let it click through.
            if (activeTouchDrag.Active) {
                var box = activeTouchDrag.Box;
                bool armed = activeTouchDrag.Armed;
                double rawOverX = activeTouchDrag.RawOverX;
                double rawOverY = activeTouchDrag.RawOverY;
                activeTouchDrag = default;
                if (armed) {
                    var captureEl = box?.Element ?? RootElement(document);
                    if (dispatcher != null && captureEl != null) {
                        dispatcher.ReleasePointerCapture(captureEl);
                    }
                    if (e is PointerEvent pUp) {
                        pUp.PreventDefault();
                        pUp.StopPropagation();
                    }
                    if (momentumAnimator != null && box != null) {
                        momentumAnimator.StartGlide(box, nowProvider(), rawOverX, rawOverY);
                    }
                }
            }
        }

        // Per-frame: advance any in-flight momentum glides.  Caller (UIDocumentLifecycle
        // or equivalent) should invoke this once per frame with the current delta time.
        public void TickMomentum(double deltaSeconds, double nowSeconds, InvalidationTracker tracker) {
            momentumAnimator?.Tick(deltaSeconds, nowSeconds, tracker);
        }

        // Updates ThumbHoveredX/Y on every active scroll container's state based
        // on whether (x, y) is currently over the corresponding thumb rect.
        // Called each pointer-move frame so ::-webkit-scrollbar-thumb:hover paints
        // correctly in real time. Hover is cleared for containers where the pointer
        // is NOT over the thumb (and no drag is in progress on that axis).
        void UpdateThumbHoverState(double x, double y) {
            if (container == null) return;
            foreach (var kv in container.All) {
                var box = kv.Key;
                var state = kv.Value;
                if (box == null || state == null) continue;

                // Fast skip: if neither scrollbar track is visible this container
                // has no webkit thumb to hover. Avoids all geometry work for the
                // overwhelmingly common case (non-scrolling containers in the tree).
                if (!state.ShowsTrackX && !state.ShowsTrackY) {
                    // Clear any stale hover flags so that a container that
                    // previously showed tracks and had hover set does not keep
                    // them set after it stops scrolling.
                    if (state.ThumbHoveredX || state.ThumbHoveredY) {
                        state.ThumbHoveredX = false;
                        state.ThumbHoveredY = false;
                    }
                    continue;
                }

                // Vertical thumb hover.
                if (!state.ThumbActiveY) {
                    // Only update hover (not active-drag axis) from position.
                    bool hitY = false;
                    if (state.ShowsTrackY) {
                        double thickness = ScrollMath.ResolveScrollbarThickness(box);
                        if (thickness > 0) {
                            AbsoluteBoxPosition(box, out double absX, out double absY);
                            double trackX = absX + box.Width - box.BorderRight - thickness;
                            double trackY = absY + box.BorderTop;
                            double trackH = box.Height - box.BorderTop - box.BorderBottom;
                            if (state.ShowsTrackX) trackH -= thickness;
                            if (trackH > 0) {
                                double thumbH = ThumbLength(trackH, state.ViewportHeight, state.ScrollHeight);
                                double thumbY = trackY + ThumbOffset(state.ScrollY, state.MaxScrollY, trackH, thumbH);
                                hitY = x >= trackX && x < trackX + thickness
                                    && y >= thumbY && y < thumbY + thumbH;
                            }
                        }
                    }
                    state.ThumbHoveredY = hitY;
                }

                // Horizontal thumb hover.
                if (!state.ThumbActiveX) {
                    bool hitX = false;
                    if (state.ShowsTrackX) {
                        double thickness = ScrollMath.ResolveScrollbarThickness(box);
                        if (thickness > 0) {
                            AbsoluteBoxPosition(box, out double absX2, out double absY2);
                            double trackY2 = absY2 + box.Height - box.BorderBottom - thickness;
                            double trackX2 = absX2 + box.BorderLeft;
                            double trackW = box.Width - box.BorderLeft - box.BorderRight;
                            if (state.ShowsTrackY) trackW -= thickness;
                            if (trackW > 0) {
                                double thumbW = ThumbLength(trackW, state.ViewportWidth, state.ScrollWidth);
                                double thumbX = trackX2 + ThumbOffset(state.ScrollX, state.MaxScrollX, trackW, thumbW);
                                hitX = x >= thumbX && x < thumbX + thumbW
                                    && y >= trackY2 && y < trackY2 + thickness;
                            }
                        }
                    }
                    state.ThumbHoveredX = hitX;
                }
            }
        }

        void ApplyScrollbarDrag(double pointer) {
            var d = activeScrollbarDrag;
            if (!d.Valid || d.State == null || d.Box == null) return;
            if (d.TrackTravel <= 0) return;

            double ratio = (pointer - d.TrackStart - d.ThumbOffset) / d.TrackTravel;
            ratio = ScrollMath.Clamp(ratio, 0, 1);
            if (d.Axis == ScrollbarAxis.Vertical) {
                SetImmediate(d.Box, d.State, d.State.ScrollX, ratio * d.State.MaxScrollY);
            } else {
                SetImmediate(d.Box, d.State, ratio * d.State.MaxScrollX, d.State.ScrollY);
            }
        }

        ScrollbarHit FindScrollbarAt(double x, double y) {
            if (container == null) return default;
            ScrollbarHit best = default;
            int bestDepth = -1;
            foreach (var kv in container.All) {
                var box = kv.Key;
                var state = kv.Value;
                if (box == null || state == null) continue;
                int depth = Depth(box);
                if (depth < bestDepth) continue;
                if (TryHitVerticalScrollbar(box, state, x, y, out var hit)
                    || TryHitHorizontalScrollbar(box, state, x, y, out hit)) {
                    best = hit;
                    bestDepth = depth;
                }
            }
            return best;
        }

        bool TryHitVerticalScrollbar(Box box, ScrollState state, double x, double y, out ScrollbarHit hit) {
            hit = default;
            if (!state.ShowsTrackY) return false;
            double thickness = ScrollMath.ResolveScrollbarThickness(box);
            if (thickness <= 0) return false;
            AbsoluteBoxPosition(box, out double absX, out double absY);
            double trackX = absX + box.Width - box.BorderRight - thickness;
            double trackY = absY + box.BorderTop;
            double trackH = box.Height - box.BorderTop - box.BorderBottom;
            if (state.ShowsTrackX) trackH -= thickness;
            if (trackH <= 0) return false;
            if (x < trackX || x >= trackX + thickness
                || y < trackY || y >= trackY + trackH) return false;
            double thumbH = ThumbLength(trackH, state.ViewportHeight, state.ScrollHeight);
            double thumbY = trackY + ThumbOffset(state.ScrollY, state.MaxScrollY, trackH, thumbH);
            hit = new ScrollbarHit {
                Valid = true,
                Box = box,
                State = state,
                Axis = ScrollbarAxis.Vertical,
                TrackStart = trackY,
                TrackLength = trackH,
                ThumbStart = thumbY,
                ThumbLength = thumbH
            };
            return true;
        }

        bool TryHitHorizontalScrollbar(Box box, ScrollState state, double x, double y, out ScrollbarHit hit) {
            hit = default;
            if (!state.ShowsTrackX) return false;
            double thickness = ScrollMath.ResolveScrollbarThickness(box);
            if (thickness <= 0) return false;
            AbsoluteBoxPosition(box, out double absX, out double absY);
            double trackY = absY + box.Height - box.BorderBottom - thickness;
            double trackX = absX + box.BorderLeft;
            double trackW = box.Width - box.BorderLeft - box.BorderRight;
            if (state.ShowsTrackY) trackW -= thickness;
            if (trackW <= 0) return false;
            if (x < trackX || x >= trackX + trackW
                || y < trackY || y >= trackY + thickness) return false;
            double thumbW = ThumbLength(trackW, state.ViewportWidth, state.ScrollWidth);
            double thumbX = trackX + ThumbOffset(state.ScrollX, state.MaxScrollX, trackW, thumbW);
            hit = new ScrollbarHit {
                Valid = true,
                Box = box,
                State = state,
                Axis = ScrollbarAxis.Horizontal,
                TrackStart = trackX,
                TrackLength = trackW,
                ThumbStart = thumbX,
                ThumbLength = thumbW
            };
            return true;
        }

        static double ThumbLength(double trackLength, double viewport, double scrollSize) {
            double thumb = scrollSize > 0 ? trackLength * (viewport / scrollSize) : trackLength;
            if (thumb < ScrollMath.ScrollbarMinThumbPx) thumb = ScrollMath.ScrollbarMinThumbPx;
            if (thumb > trackLength) thumb = trackLength;
            return thumb;
        }

        static double ThumbOffset(double scroll, double maxScroll, double trackLength, double thumbLength) {
            if (maxScroll <= 0) return 0;
            return (scroll / maxScroll) * (trackLength - thumbLength);
        }

        int Depth(Box box) {
            int d = 0;
            for (var b = box; b != null; b = b.Parent) d++;
            return d;
        }

        void AbsoluteBoxPosition(Box box, out double x, out double y) {
            x = 0;
            y = 0;
            for (var b = box; b != null; b = b.Parent) {
                x += b.X + b.StickyOffsetX;
                y += b.Y + b.StickyOffsetY;
                var p = b.Parent;
                if (p != null) {
                    var ps = container?.Get(p);
                    if (ps != null) {
                        x -= ps.ScrollX;
                        y -= ps.ScrollY;
                    }
                }
            }
        }

        void SetImmediate(Box box, ScrollState state, double x, double y) {
            if (box == null || state == null) return;
            x = ScrollMath.Clamp(x, 0, state.MaxScrollX);
            y = ScrollMath.Clamp(y, 0, state.MaxScrollY);
            if (x == state.ScrollX && y == state.ScrollY) return;
            state.ScrollX = x;
            state.ScrollY = y;
            box.ScrollX = x;
            box.ScrollY = y;
            state.BumpVersion();
            if (box.Element != null) invalidatePaint?.Invoke(box.Element);
        }

        // SetImmediate variant that does NOT clamp — used for rubber-band overscroll
        // positions that intentionally sit outside [0, MaxScroll].
        // Only valid during an active armed drag; all other scroll paths must use
        // SetImmediate (which hard-clamps) to maintain the containment guarantee.
        void SetImmediateRaw(Box box, ScrollState state, double x, double y) {
            if (box == null || state == null) return;
            if (x == state.ScrollX && y == state.ScrollY) return;
            state.ScrollX = x;
            state.ScrollY = y;
            box.ScrollX = x;
            box.ScrollY = y;
            state.BumpVersion();
            if (box.Element != null) invalidatePaint?.Invoke(box.Element);
        }

        // Apply a 1-D scroll delta with rubber-band resistance past edges.
        // `delta` is the signed drag delta in scroll-coordinate space (positive = scroll down/right).
        // `rawOver` accumulates raw overscroll past the edges (updated in-place).
        // `newPos` is the resulting visual scroll position (may be outside [0, Max] during overscroll).
        // `isVertical` selects ViewportHeight vs ViewportWidth for the rubber-band dimension.
        void ApplyRubberBandMove(ScrollState state, double delta, ref double rawOver, out double newPos,
                                 bool isVertical = false) {
            double current = isVertical ? state.ScrollY : state.ScrollX;
            double maxScroll = isVertical ? state.MaxScrollY : state.MaxScrollX;
            double dim = isVertical
                ? (state.ViewportHeight > 0 ? state.ViewportHeight : 100)
                : (state.ViewportWidth  > 0 ? state.ViewportWidth  : 100);

            // Hard-clamp mode: clamp the drag at the bounds, no overscroll.
            if (!RubberBandOverscroll) {
                newPos = ScrollMath.Clamp(current + delta, 0, maxScroll);
                rawOver = 0;
                return;
            }

            bool inOverscroll = System.Math.Abs(rawOver) > 0;

            if (!inOverscroll) {
                // In-bounds: apply delta normally, check for edge breach.
                double tentative = current + delta;
                if (tentative < 0) {
                    // Crossed the top/left edge.
                    rawOver = tentative;  // negative
                    double visual = -Smooth.ScrollMomentum.RubberBandVisual(-rawOver, dim);
                    newPos = visual;
                } else if (tentative > maxScroll) {
                    // Crossed the bottom/right edge.
                    rawOver = tentative - maxScroll;  // positive
                    double visual = maxScroll + Smooth.ScrollMomentum.RubberBandVisual(rawOver, dim);
                    newPos = visual;
                } else {
                    rawOver = 0;
                    newPos = tentative;
                }
            } else {
                // Already in overscroll — apply delta to raw accumulator.
                double prevRaw = rawOver;
                rawOver += delta;

                if (prevRaw < 0 && rawOver >= 0) {
                    // Crossed back into bounds from top/left overscroll.
                    // The amount past the boundary that goes in-bounds is (0 - prevRaw),
                    // so remaining in-bounds delta = delta - (0 - prevRaw) = delta + prevRaw.
                    rawOver = 0;
                    newPos = System.Math.Max(0, System.Math.Min(maxScroll, delta + prevRaw));
                } else if (prevRaw > 0 && rawOver <= 0) {
                    // Crossed back into bounds from bottom/right overscroll.
                    // Amount past boundary going in-bounds = prevRaw; remaining = delta + prevRaw.
                    rawOver = 0;
                    newPos = System.Math.Max(0, System.Math.Min(maxScroll, maxScroll + delta + prevRaw));
                } else if (rawOver < 0) {
                    double visual = -Smooth.ScrollMomentum.RubberBandVisual(-rawOver, dim);
                    newPos = visual;
                } else {
                    double visual = maxScroll + Smooth.ScrollMomentum.RubberBandVisual(rawOver, dim);
                    newPos = visual;
                }
            }
        }

        Box NearestScrollContainer(Element fromElement) {
            if (fromElement == null || elementToBox == null) return null;
            var box = elementToBox(fromElement);
            return NearestScrollContainerStartingAt(box);
        }

        Box NearestScrollContainerStartingAt(Box from) {
            for (var b = from; b != null; b = b.Parent) {
                if (ScrollContainerLookup.HasNonVisibleOverflow(b)) return b;
            }
            // No element-level scroll container found — fall back to the viewport
            // scroll root if it has a scroll state (CSS Overflow §3.3 viewport scrolling).
            return ViewportRoot != null && container.Has(ViewportRoot) ? ViewportRoot : null;
        }

        // Convenience: programmatic ScrollBy / ScrollTo against a target element.
        public void ScrollBy(Element targetElement, double dx, double dy) {
            var box = NearestScrollContainer(targetElement);
            if (box == null) return;
            var state = container.GetOrCreate(box);
            double tx = state.ScrollX + dx;
            double ty = state.ScrollY + dy;
            bool snappedTarget = ResolveSnapTargetForSmooth(box, ref tx, ref ty);
            SetScroll(box, state, tx, ty, null);
            if (!snappedTarget) SettleSnap(box);
        }

        public void ScrollTo(Element targetElement, double x, double y) {
            var box = NearestScrollContainer(targetElement);
            if (box == null) return;
            bool snappedTarget = ResolveSnapTargetForSmooth(box, ref x, ref y);
            SetScroll(box, container.GetOrCreate(box), x, y, null);
            if (!snappedTarget) SettleSnap(box);
        }

        // CSS Snap §5: for smooth scrolls into a snap container, resolve the
        // snap position against the TARGET first, then animate to that snapped
        // position — otherwise the in-flight ramp aims at the raw target and
        // a post-Animate SettleSnap would re-aim from the still-zero current
        // position, clobbering the corrected target.
        bool ResolveSnapTargetForSmooth(Box box, ref double targetX, ref double targetY) {
            if (snapResolver == null) return false;
            if (!(smoothAnimator != null && SmoothScrollAnimator.IsSmooth(box))) return false;
            var type = SnapResolver.ResolveType(box);
            if (!type.IsActive) return false;
            var state = container.GetOrCreate(box);
            double startX = state.ScrollX;
            double startY = state.ScrollY;
            bool snapped = false;
            if (type.Axis != SnapAxis.X
                && snapResolver.TryFindSnapTargetY(box, startY, targetY, type, out double snappedY)) {
                targetY = snappedY;
                snapped = true;
            }
            if (type.Axis != SnapAxis.Y
                && snapResolver.TryFindSnapTargetX(box, startX, targetX, type, out double snappedX)) {
                targetX = snappedX;
                snapped = true;
            }
            return snapped;
        }

        enum ScrollbarAxis {
            Vertical,
            Horizontal
        }

        struct ScrollbarHit {
            public bool Valid;
            public Box Box;
            public ScrollState State;
            public ScrollbarAxis Axis;
            public double TrackStart;
            public double TrackLength;
            public double ThumbStart;
            public double ThumbLength;
        }

        struct ScrollbarDrag {
            public bool Valid => Box != null && State != null;
            public Box Box;
            public ScrollState State;
            public ScrollbarAxis Axis;
            public double TrackStart;
            public double TrackTravel;
            public double ThumbOffset;
        }
    }
}
