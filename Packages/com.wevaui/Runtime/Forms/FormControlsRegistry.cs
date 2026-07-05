using System;
using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;
using Weva.Layout.Boxes;

namespace Weva.Forms {
    // Maintains an InputController per <input> and <textarea> in the document
    // so text editing, focus, and value commit work out-of-the-box without
    // authors having to construct controllers themselves. Subscribes to
    // Document.Mutated so DOM-mutating controllers (todo lists, dynamic
    // forms) don't need to re-seat anything when they AppendChild a new
    // input — the registry sees the mutation and wires/unwires accordingly.
    //
    // Lifetime: built by UIDocumentBuilder after Bindings.AttachLive so it
    // sees the same controller and dispatcher the bindings layer does.
    // Disposed alongside Bindings on WevaDocument teardown — Dispose detaches
    // the mutation listener and Unwires every controller. Idempotent: a
    // second Dispose is a no-op.
    public sealed class FormControlsRegistry : IDisposable {
        readonly Document doc;
        readonly EventDispatcher dispatcher;
        readonly Dictionary<Element, InputController> controllers = new();
        readonly Dictionary<Element, RangeController> rangeControllers = new();
        readonly Dictionary<Element, LabelController> labelControllers = new();
        readonly Dictionary<Element, SelectController> selectControllers = new();
        readonly Dictionary<Element, DetailsController> detailsControllers = new();
        // W4 phase 2: per-element substring measurer factory. When supplied,
        // each wired controller's TextEditModel gets a metric-aware
        // (text, start, count) -> px function so vertical caret movement
        // preserves PIXEL X (goal column) instead of char column. Null keeps
        // the char-column fallback (bare-registry tests, headless callers
        // without metrics). UIDocumentBuilder provides a live-resolving one.
        readonly Func<Element, Func<string, int, int, double>> measureProvider;
        // Resolves an element to its laid-out box — RangeController needs it to
        // map a pointer X to a value along the slider track. May be null
        // (bare-registry tests); range drag is then inert but wiring stays safe.
        readonly Func<Element, Box> elementToBox;
        // Tracker for popups (the <select> dropdown opens a ContextMenu, which
        // needs to mark the document dirty). May be null (bare-registry tests).
        readonly InvalidationTracker tracker;
        Action<DomMutation> mutationListener;
        bool disposed;

        public FormControlsRegistry(Document doc, EventDispatcher dispatcher,
                                    Func<Element, Func<string, int, int, double>> measureProvider = null,
                                    Func<Element, Box> elementToBox = null,
                                    InvalidationTracker tracker = null) {
            this.doc = doc ?? throw new ArgumentNullException(nameof(doc));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.measureProvider = measureProvider;
            this.elementToBox = elementToBox;
            this.tracker = tracker;
            ScanAndWire(doc);
            mutationListener = OnMutation;
            doc.Mutated += mutationListener;
        }

        // Input/selection audit #3: caret-activity signal for the blink timer,
        // fanned out to every wired InputController (current and future).
        Action caretActivity;
        public Action CaretActivity {
            get => caretActivity;
            set {
                caretActivity = value;
                foreach (var kv in controllers) kv.Value.CaretActivity = value;
            }
        }

        // Input/selection audit #5: clipboard bridge for Ctrl+C/X/V, fanned
        // out the same way.
        Bridge.IClipboardBridge clipboard;
        public Bridge.IClipboardBridge Clipboard {
            get => clipboard;
            set {
                clipboard = value;
                foreach (var kv in controllers) kv.Value.Clipboard = value;
            }
        }

        // Exposed so tests / authors can introspect or unwire/replace a specific
        // controller if they want custom behavior on a particular element.
        public bool TryGet(Element e, out InputController controller) {
            if (e == null) { controller = null; return false; }
            return controllers.TryGetValue(e, out controller);
        }

        void OnMutation(DomMutation m) {
            if (disposed) return;
            switch (m.Kind) {
                case DomMutationKind.ChildAdded:
                    ScanAndWire(m.Subject);
                    break;
                case DomMutationKind.ChildRemoved:
                    UnwireSubtree(m.Subject);
                    break;
            }
        }

        void ScanAndWire(Node root) {
            if (root == null) return;
            if (root is Element e) {
                if ((IsTextInput(e) || IsCheckableInput(e)) && !controllers.ContainsKey(e)) {
                    // InputController drives text editing AND checkbox/radio click-
                    // toggle (incl. radio-group exclusivity) — see InputController.OnClick.
                    // Without wiring it for checkbox/radio, clicking them did nothing.
                    var ic = new InputController(e, dispatcher, null, tracker);
                    var measure = measureProvider?.Invoke(e);
                    if (measure != null) ic.Model.SetMeasureSubstring(measure);
                    // FM2: box resolver for pointer→caret mapping (the same
                    // resolver RangeController uses for track drag).
                    ic.ElementToBox = elementToBox;
                    ic.CaretActivity = caretActivity;
                    ic.Clipboard = clipboard;
                    ic.Wire();
                    controllers[e] = ic;
                } else if (IsRangeInput(e) && !rangeControllers.ContainsKey(e)) {
                    // Pointer-drag + keyboard control for <input type=range>.
                    var rc = new RangeController(e, dispatcher, elementToBox);
                    rc.Wire();
                    rangeControllers[e] = rc;
                } else if (IsLabel(e) && !labelControllers.ContainsKey(e)) {
                    // <label> click → forward activation to its target control
                    // (HTML label activation behavior). Without this, clicking a
                    // label does nothing — a silent a11y gap.
                    var lc = new LabelController(e, dispatcher);
                    lc.Wire();
                    labelControllers[e] = lc;
                } else if (IsSelect(e) && !selectControllers.ContainsKey(e)) {
                    // <select> click → open a dropdown popup of its <option>s.
                    var sc = new SelectController(e, dispatcher, doc, tracker, elementToBox);
                    sc.Wire();
                    selectControllers[e] = sc;
                } else if (IsDetails(e) && !detailsControllers.ContainsKey(e)) {
                    // <summary> click → toggle the <details> open attribute.
                    var dc = new DetailsController(e, dispatcher);
                    dc.Wire();
                    detailsControllers[e] = dc;
                }
            }
            for (int i = 0; i < root.Children.Count; i++) ScanAndWire(root.Children[i]);
        }

        void UnwireSubtree(Node root) {
            if (root == null) return;
            if (root is Element e && controllers.TryGetValue(e, out var ic)) {
                // Release() drops the dispatcher listeners AND the Model /
                // IME subscriptions the constructor opened. Plain Unwire()
                // would leave those alive, keeping the controller (and the
                // TextEditModel, its undo stack, and any ImeSession) live
                // for as long as the parent registry — leaking ~1 KB per
                // removed input over the document's lifetime.
                ic.Release();
                controllers.Remove(e);
            }
            if (root is Element re && rangeControllers.TryGetValue(re, out var rc)) {
                rc.Unwire();
                rangeControllers.Remove(re);
            }
            if (root is Element le && labelControllers.TryGetValue(le, out var lc)) {
                lc.Unwire();
                labelControllers.Remove(le);
            }
            if (root is Element se && selectControllers.TryGetValue(se, out var sc)) {
                sc.Unwire();
                selectControllers.Remove(se);
            }
            if (root is Element de && detailsControllers.TryGetValue(de, out var dc)) {
                dc.Unwire();
                detailsControllers.Remove(de);
            }
            for (int i = 0; i < root.Children.Count; i++) UnwireSubtree(root.Children[i]);
        }

        // Text-editing controls (drive the TextEditModel). Checkbox/radio are
        // also InputController-driven but matched separately via IsCheckableInput
        // (click-toggle, no text model). Range uses RangeController.
        // Blocklist semantics: every <input> is text-editable EXCEPT the types
        // with distinct (or no) behavior — so text/email/.../number AND
        // date/time/color/datetime-local/month/week and any unknown type all fall
        // back to a typable text box, matching the browser's unknown-type behavior.
        static bool IsTextInput(Element e) {
            if (e == null) return false;
            if (string.Equals(e.TagName, "textarea", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(e.TagName, "input", StringComparison.OrdinalIgnoreCase)) return false;
            var type = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
            switch (type) {
                case "checkbox":
                case "radio":
                case "range":
                case "hidden":
                case "file":
                case "submit":
                case "reset":
                case "button":
                case "image":
                    return false;
                default:
                    return true;
            }
        }

        static bool IsRangeInput(Element e) {
            return e != null
                && string.Equals(e.TagName, "input", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.GetAttribute("type"), "range", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsLabel(Element e) {
            return e != null && string.Equals(e.TagName, "label", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsSelect(Element e) {
            return e != null && string.Equals(e.TagName, "select", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsDetails(Element e) {
            return e != null && string.Equals(e.TagName, "details", StringComparison.OrdinalIgnoreCase);
        }

        // checkbox / radio — click-toggle controls driven by InputController.OnClick.
        static bool IsCheckableInput(Element e) {
            if (e == null || !string.Equals(e.TagName, "input", StringComparison.OrdinalIgnoreCase)) return false;
            var type = (e.GetAttribute("type") ?? "text").ToLowerInvariant();
            return type == "checkbox" || type == "radio";
        }

        public void Dispose() {
            if (disposed) return;
            if (doc != null && mutationListener != null) {
                doc.Mutated -= mutationListener;
            }
            foreach (var kv in controllers) kv.Value.Release();
            controllers.Clear();
            foreach (var kv in rangeControllers) kv.Value.Unwire();
            rangeControllers.Clear();
            foreach (var kv in labelControllers) kv.Value.Unwire();
            labelControllers.Clear();
            foreach (var kv in selectControllers) kv.Value.Unwire();
            selectControllers.Clear();
            foreach (var kv in detailsControllers) kv.Value.Unwire();
            detailsControllers.Clear();
            mutationListener = null;
            disposed = true;
        }
    }
}
