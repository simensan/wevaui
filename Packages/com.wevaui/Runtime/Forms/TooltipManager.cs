using System;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Forms {
    // TooltipManager — auto-renders the `title=""` attribute on hover.
    //
    // Subscribes to pointer enter/move/leave at the document level; whenever
    // the pointer rests over an element (or any ancestor) carrying a `title`
    // attribute for longer than `ShowDelaySeconds`, a `<div class="ui-tooltip">`
    // with the title's text is injected into the document at the cursor
    // position. PointerLeave on that element, a click, or moving onto a
    // different title-bearing element hides it.
    //
    // The tooltip element has no special widget class — it's a normal DOM
    // node that uses the user-agent stylesheet rule for `.ui-tooltip` for
    // padding/background and is positioned via inline `style="left:Xpx;
    // top:Ypx"`. Author CSS can target `.ui-tooltip` to restyle.
    //
    // Lifecycle.Update calls Tick() every frame so the show-delay can fire
    // even when no pointer events arrive (a pointer that's been still on
    // a title-bearing element since last frame will eventually trigger
    // the tooltip without needing further movement).
    public sealed class TooltipManager {
        public double ShowDelaySeconds { get; set; } = 0.6;
        public double CursorOffsetX { get; set; } = 12;
        public double CursorOffsetY { get; set; } = 18;
        public string TooltipClassName { get; set; } = "ui-tooltip";

        readonly Document doc;
        readonly EventDispatcher dispatcher;
        readonly IUIClock clock;
        readonly InvalidationTracker tracker;

        readonly EventListener enter;
        readonly EventListener move;
        readonly EventListener leave;
        readonly EventListener pointerDown;

        Element titleHost;          // element whose title we'd show
        double hoverStartSeconds;
        double pointerX, pointerY;
        Element tooltipElement;     // the injected DOM node, null when hidden
        bool subscribed;

        public TooltipManager(Document doc, EventDispatcher dispatcher, IUIClock clock, InvalidationTracker tracker) {
            this.doc = doc ?? throw new ArgumentNullException(nameof(doc));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.clock = clock ?? new SystemUIClock();
            this.tracker = tracker;
            enter = OnEnter;
            move = OnMove;
            leave = OnLeave;
            pointerDown = OnDown;
        }

        public Element CurrentTooltipElement => tooltipElement;

        public void Wire() {
            if (subscribed) return;
            var root = RootElement();
            if (root == null) return;
            // Capture-phase listeners on the root catch every pointer event
            // in the document so we can track hover/move/leave globally
            // without needing per-element subscriptions for every node that
            // might carry a title.
            dispatcher.AddEventListener(root, EventKind.PointerEnter, enter, useCapture: true);
            dispatcher.AddEventListener(root, EventKind.PointerLeave, leave, useCapture: true);
            dispatcher.AddEventListener(root, EventKind.PointerMove, move, useCapture: true);
            dispatcher.AddEventListener(root, EventKind.PointerDown, pointerDown, useCapture: true);
            subscribed = true;
        }

        public void Unwire() {
            if (!subscribed) return;
            var root = RootElement();
            if (root != null) {
                dispatcher.RemoveEventListener(root, EventKind.PointerEnter, enter, useCapture: true);
                dispatcher.RemoveEventListener(root, EventKind.PointerLeave, leave, useCapture: true);
                dispatcher.RemoveEventListener(root, EventKind.PointerMove, move, useCapture: true);
                dispatcher.RemoveEventListener(root, EventKind.PointerDown, pointerDown, useCapture: true);
            }
            HideTooltip();
            subscribed = false;
        }

        // Called from UIDocumentLifecycle.Update so the show-delay can fire
        // even on idle frames where no pointer event arrives. Re-checks the
        // current hover host and shows the tooltip when the dwell time has
        // elapsed.
        public void Tick() {
            if (titleHost == null || tooltipElement != null) return;
            if (clock.NowSeconds - hoverStartSeconds < ShowDelaySeconds) return;
            ShowTooltip();
        }

        Element RootElement() {
            if (doc == null) return null;
            foreach (var c in doc.Children) {
                if (c is Element e) return e;
            }
            return null;
        }

        // Walk from `target` up the parent chain and return the first
        // ancestor (inclusive) carrying a non-empty `title` attribute.
        // Mirrors HTMLElement title inheritance — the closest title wins.
        static Element FindTitleHost(Element target) {
            for (var n = target; n != null; n = n.Parent as Element) {
                var t = n.GetAttribute("title");
                if (!string.IsNullOrEmpty(t)) return n;
            }
            return null;
        }

        void OnEnter(UIEvent evt) {
            if (!(evt is PointerEvent pe)) return;
            if (!(evt.Target is Element target)) return;
            pointerX = pe.X;
            pointerY = pe.Y;
            var host = FindTitleHost(target);
            if (host == titleHost) return;
            titleHost = host;
            hoverStartSeconds = clock.NowSeconds;
            HideTooltip();
        }

        void OnMove(UIEvent evt) {
            if (!(evt is PointerEvent pe)) return;
            pointerX = pe.X;
            pointerY = pe.Y;
            // When moving onto a different title host, restart the dwell
            // timer so the tooltip retracks. Pointer-enter doesn't fire for
            // every element transition (it bubbles), so re-check on move.
            if (evt.Target is Element t) {
                var host = FindTitleHost(t);
                if (host != titleHost) {
                    titleHost = host;
                    hoverStartSeconds = clock.NowSeconds;
                    HideTooltip();
                    return;
                }
            }
            if (tooltipElement != null) UpdateTooltipPosition();
        }

        void OnLeave(UIEvent evt) {
            // Bubble leave on the root reaches us when the pointer leaves
            // the document entirely.
            if (evt.Target is Element t && t == titleHost) {
                titleHost = null;
                HideTooltip();
            }
        }

        void OnDown(UIEvent _) {
            // Click anywhere dismisses the tooltip — matches browser
            // behavior where activating a control pops the tooltip down.
            HideTooltip();
        }

        void ShowTooltip() {
            if (titleHost == null) return;
            var text = titleHost.GetAttribute("title");
            if (string.IsNullOrEmpty(text)) return;
            var elem = new Element("div");
            elem.SetAttribute("class", TooltipClassName);
            elem.SetAttribute("style", BuildPositionStyle(pointerX + CursorOffsetX, pointerY + CursorOffsetY));
            elem.AppendChild(new TextNode(text));
            doc.AppendChild(elem);
            tooltipElement = elem;
            tracker?.MarkDirty(doc, InvalidationKind.Layout | InvalidationKind.Paint);
        }

        void UpdateTooltipPosition() {
            if (tooltipElement == null) return;
            tooltipElement.SetAttribute("style", BuildPositionStyle(pointerX + CursorOffsetX, pointerY + CursorOffsetY));
        }

        void HideTooltip() {
            if (tooltipElement == null) return;
            tooltipElement.Parent?.RemoveChild(tooltipElement);
            tracker?.MarkDirty(doc, InvalidationKind.Layout | InvalidationKind.Paint);
            tooltipElement = null;
        }

        static string BuildPositionStyle(double x, double y) {
            return $"position:fixed;left:{x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}px;top:{y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}px;pointer-events:none;z-index:99999";
        }
    }
}
