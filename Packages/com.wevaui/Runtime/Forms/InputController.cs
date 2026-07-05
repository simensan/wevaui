using System;
using Weva.Dom;
using Weva.Events;
using Weva.Forms.Ime;
using Weva.Reactive;

namespace Weva.Forms {
    // Drives a single <input> or <textarea>. Listens for KeyDown on the focused
    // element, routes text-input keystrokes to the TextEditModel, and writes
    // the model's text back via SetAttribute("value", ...) so the reactivity
    // layer sees the change. Also bridges an ImeSession when one is present.
    public sealed class InputController : IDisposable {
        public Element Element { get; }
        public TextEditModel Model { get; }
        public bool IsTextArea { get; }

        readonly EventDispatcher dispatcher;
        readonly ImeSession ime;
        // Optional: lets a caret/selection move (which doesn't change the value
        // attribute, so it doesn't auto-invalidate) repaint the input so the
        // live caret follows. May be null (bare tests).
        readonly InvalidationTracker tracker;
        readonly EventListener keyListener;
        readonly EventListener focusListener;
        readonly EventListener blurListener;
        readonly EventListener clickListener;
        readonly EventListener pointerDownListener;
        readonly EventListener pointerMoveListener;
        readonly EventListener pointerUpListener;

        // FM2: pointer caret/selection state. `ElementToBox` is wired by
        // FormControlsRegistry (same resolver RangeController uses) so a
        // pointer X can map into the input's content box; without it the
        // pointer gestures are inert (legacy focus behavior only).
        public Func<Element, Weva.Layout.Boxes.Box> ElementToBox { get; set; }
        bool pointerSelecting;
        int dragAnchor;
        bool suppressFocusSelectAll;
        bool subscribed;
        bool suppressWriteBack;

        public event Action ValueCommitted;
        public event Action ValueChanged;

        public InputController(Element element, EventDispatcher dispatcher, ImeSession ime = null,
                               InvalidationTracker tracker = null) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            Element = element;
            this.dispatcher = dispatcher;
            this.ime = ime;
            this.tracker = tracker;

            IsTextArea = element.TagName == "textarea";
            int? maxLen = null;
            string initial = "";
            bool multiline;
            if (IsTextArea) {
                multiline = true;
                var ta = new TextAreaElement(element);
                initial = ta.Value;
            } else if (element.TagName == "input") {
                multiline = false;
                var ie = new InputElement(element);
                initial = ie.Value;
                maxLen = ie.MaxLength;
            } else {
                throw new ArgumentException($"InputController requires <input> or <textarea>; got <{element.TagName}>", nameof(element));
            }

            Model = new TextEditModel(initial, multiline, maxLen);
            Model.Changed += OnModelChanged;
            // Caret / selection moves don't touch the value attribute, so they
            // wouldn't otherwise invalidate paint — repaint so the live caret
            // (BoxToPaintConverter.EmitInputOverlays) follows the cursor.
            Model.SelectionChanged += OnSelectionChanged;
            lastCommitedValue = initial;

            keyListener = OnKey;
            focusListener = OnFocus;
            blurListener = OnBlur;
            clickListener = OnClick;
            pointerDownListener = OnPointerDown;
            pointerMoveListener = OnPointerMove;
            pointerUpListener = OnPointerUp;

            if (ime != null) {
                ime.CompositionUpdated += OnImeUpdated;
                ime.CompositionCommitted += OnImeCommitted;
                ime.CompositionCancelled += OnImeCancelled;
            }
        }

        public void Wire() {
            if (subscribed) return;
            dispatcher.AddEventListener(Element, EventKind.KeyDown, keyListener);
            dispatcher.AddEventListener(Element, EventKind.Focus, focusListener);
            dispatcher.AddEventListener(Element, EventKind.Blur, blurListener);
            dispatcher.AddEventListener(Element, EventKind.Click, clickListener);
            dispatcher.AddEventListener(Element, EventKind.PointerDown, pointerDownListener);
            dispatcher.AddEventListener(Element, EventKind.PointerMove, pointerMoveListener);
            dispatcher.AddEventListener(Element, EventKind.PointerUp, pointerUpListener);
            subscribed = true;
        }

        public void Unwire() {
            if (!subscribed) return;
            dispatcher.RemoveEventListener(Element, EventKind.KeyDown, keyListener);
            dispatcher.RemoveEventListener(Element, EventKind.Focus, focusListener);
            dispatcher.RemoveEventListener(Element, EventKind.Blur, blurListener);
            dispatcher.RemoveEventListener(Element, EventKind.Click, clickListener);
            dispatcher.RemoveEventListener(Element, EventKind.PointerDown, pointerDownListener);
            dispatcher.RemoveEventListener(Element, EventKind.PointerMove, pointerMoveListener);
            dispatcher.RemoveEventListener(Element, EventKind.PointerUp, pointerUpListener);
            subscribed = false;
        }

        // Drop the non-dispatcher subscriptions the constructor made:
        // Model.Changed (writes value back to the attribute) and the three
        // IME composition delegates. Unwire() handles only the dispatcher
        // side, so without this the controller leaks via the Model and
        // ImeSession references after every <input> removal.
        // FormControlsRegistry.UnwireSubtree calls this when the element
        // leaves the DOM. Idempotent.
        bool released;
        public void Release() {
            if (released) return;
            released = true;
            Unwire();
            if (Model != null) {
                Model.Changed -= OnModelChanged;
                Model.SelectionChanged -= OnSelectionChanged;
            }
            if (ime != null) {
                ime.CompositionUpdated -= OnImeUpdated;
                ime.CompositionCommitted -= OnImeCommitted;
                ime.CompositionCancelled -= OnImeCancelled;
            }
        }

        void IDisposable.Dispose() => Release();

        bool DisabledAttr() => Element.HasAttribute("disabled");
        bool ReadOnlyAttr() => Element.HasAttribute("readonly");

        // Has this controller's element been detached from the document
        // since the event was queued? A Click handler can synchronously
        // remove the host element (controller-driven todo list, dynamic
        // form). Any subsequent dispatcher callback that fires for the
        // same event (Focus, Blur, Key) would otherwise touch attributes
        // on a node whose Parent has been nulled. We also short-circuit
        // when `released` is set so an explicit Release() race against
        // an in-flight dispatch can't read freed state.
        bool DroppedOrDetached() => released || Element == null || Element.Parent == null;

        void OnFocus(UIEvent _) {
            if (DroppedOrDetached()) return;
            // Re-sync the model from the attribute in case it was changed externally.
            var current = IsTextArea ? new TextAreaElement(Element).Value : Element.GetAttribute("value") ?? "";
            if (current != Model.Text) Model.SetText(current);
            // FM2: focus arrives AFTER the pointer-down dispatch. When the
            // pointer handler just placed a caret, select-all would clobber
            // it — Chrome selects-all only on keyboard/programmatic focus;
            // a click places a collapsed caret.
            if (suppressFocusSelectAll) { suppressFocusSelectAll = false; return; }
            Model.SelectAll();
        }

        // ── FM2: pointer caret placement + drag selection ────────────────
        // Every primitive existed (CaretGeometry, the model's measurer, the
        // dispatcher's pointer capture) with zero pointer consumers — every
        // click select-all'd the value and the next keystroke replaced it.

        bool IsPointerTextTarget() {
            if (IsTextArea) return false; // multiline caret mapping not built yet
            if (Element.TagName != "input") return false;
            var type = Element.GetAttribute("type");
            return !(type == "checkbox" || type == "radio" || type == "button" ||
                     type == "submit" || type == "reset" || type == "hidden" ||
                     type == "range" || type == "file" || type == "image");
        }

        // Input/selection audit #4 — click-streak selection modes. detail 1 =
        // caret + char drag, 2 = word select + drag-by-word, ≥3 = select-all
        // (Chrome's paragraph select == the whole value on a single-line
        // input). The anchor UNIT (a char slot or a word range) is fixed at
        // pointer-down; drags extend by whole units from it.
        int dragDetail = 1;
        int dragAnchorStart, dragAnchorEnd;

        void OnPointerDown(UIEvent evtBase) {
            if (DroppedOrDetached() || DisabledAttr()) return;
            if (!(evtBase is PointerEvent pe) || pe.Button != 0) return;
            // FM7 convention (audit #10/#11): caret placement/selection is the
            // pointer-down's default action — an author preventDefault() from
            // a capture-phase listener cancels it, like Chrome. (Bubble-phase
            // prevention lands after this target handler ran — v1 divergence.)
            if (pe.DefaultPrevented) return;
            if (IsTextArea) {
                // Input/selection audit #6 — multiline caret placement. The
                // TextAreaCaretMap aligns the PAINTED LineBox/TextRun geometry
                // back to model indices, so the caret lands on the clicked
                // line/column exactly where the glyphs are. When the map can't
                // be built (no box yet, alignment bail) fall back to a caret
                // at the end — never the old invisible select-all.
                var cur = new TextAreaElement(Element).Value ?? "";
                if (cur != Model.Text) Model.SetText(cur);
                suppressFocusSelectAll = true;
                var taBox = ElementToBox?.Invoke(Element);
                var map = taBox != null && Model.MeasureSubstring != null
                    ? TextAreaCaretMap.Build(taBox, Model.Text ?? "", Model.MeasureSubstring)
                    : null;
                if (map == null) {
                    Model.SetCaret(Model.Text?.Length ?? 0);
                    return;
                }
                int taIdx = map.IndexFromPoint(pe.X - AbsoluteLeft(taBox), pe.Y - AbsoluteTop(taBox));
                dragDetail = pe.ShiftKey ? 1 : Math.Max(1, pe.Detail);
                if (dragDetail >= 3) {
                    // Triple-click in a textarea: the logical line (Chrome's
                    // paragraph), newline inclusive; drag extends by lines.
                    LineRangeAt(Model.Text ?? "", taIdx, out dragAnchorStart, out dragAnchorEnd);
                    Model.SetSelection(dragAnchorStart, dragAnchorEnd);
                } else if (dragDetail == 2) {
                    Weva.Forms.Text.WordBoundary.WordRangeAt(
                        Model.Text ?? "", taIdx, out dragAnchorStart, out dragAnchorEnd);
                    Model.SetSelection(dragAnchorStart, dragAnchorEnd);
                } else if (pe.ShiftKey) {
                    var selNow = Model.Selection;
                    dragAnchor = selNow.Direction == SelectionDirection.Backward ? selNow.End : selNow.Start;
                    Model.SetSelection(dragAnchor, taIdx);
                } else {
                    dragAnchor = taIdx;
                    Model.SetSelection(taIdx, taIdx);
                }
                pointerSelecting = true;
                textAreaDragMap = map; // gesture-scoped cache; cleared on up
                dispatcher.SetPointerCapture(Element);
                return;
            }
            if (!IsPointerTextTarget()) return;
            // Re-sync BEFORE mapping so the index lands in the current value
            // (external writes while unfocused — same guard as OnFocus).
            var current = Element.GetAttribute("value") ?? "";
            if (current != Model.Text) Model.SetText(current);
            int idx = CaretIndexFromPointer(pe.X);
            if (idx < 0) return; // no box/measurer wired — legacy behavior
            suppressFocusSelectAll = true;
            dragDetail = pe.ShiftKey ? 1 : Math.Max(1, pe.Detail);
            if (dragDetail >= 3) {
                // Triple-click: select all; the drag keeps it (Chrome's
                // paragraph mode collapses to the whole single-line value).
                Model.SelectAll();
            } else if (dragDetail == 2) {
                // Double-click: select the word unit under the pointer; the
                // range is the drag anchor (drag extends by whole words).
                Weva.Forms.Text.WordBoundary.WordRangeAt(
                    DisplayTextForPointer(), idx, out dragAnchorStart, out dragAnchorEnd);
                Model.SetSelection(dragAnchorStart, dragAnchorEnd);
            } else if (pe.ShiftKey) {
                // Shift-click extends from the existing anchor.
                var selNow = Model.Selection;
                dragAnchor = selNow.Direction == SelectionDirection.Backward ? selNow.End : selNow.Start;
                Model.SetSelection(dragAnchor, idx);
            } else {
                dragAnchor = idx;
                Model.SetSelection(dragAnchor, idx);
            }
            pointerSelecting = true;
            // Capture so the drag keeps selecting when the pointer leaves the
            // field. NOTE (audit #11): no StopPropagation — Chrome dispatches
            // pointer events on text fields to ancestors normally (selection
            // is a default action, not a propagation stop), and the drag-pan
            // conflict this used to guard against is solved at the source:
            // ScrollEventHandler's capture-phase arming (which always ran
            // BEFORE this listener anyway) skips text controls
            // (TargetOwnsPointerDrag).
            dispatcher.SetPointerCapture(Element);
        }

        // Gesture-scoped multiline map: built on the textarea pointer-down,
        // reused for every move of that drag (text can't change mid-drag),
        // cleared on pointer-up.
        TextAreaCaretMap textAreaDragMap;

        void OnPointerMove(UIEvent evtBase) {
            if (!pointerSelecting) return;
            if (!(evtBase is PointerEvent pe)) return;
            if (IsTextArea) {
                var map = textAreaDragMap;
                var taBox = ElementToBox?.Invoke(Element);
                if (map == null || taBox == null) return;
                int taIdx = map.IndexFromPoint(pe.X - AbsoluteLeft(taBox), pe.Y - AbsoluteTop(taBox));
                if (dragDetail >= 3) {
                    LineRangeAt(Model.Text ?? "", taIdx, out int ls, out int le);
                    if (ls < dragAnchorStart) Model.SetSelection(dragAnchorEnd, ls);
                    else Model.SetSelection(dragAnchorStart, Math.Max(le, dragAnchorEnd));
                } else if (dragDetail == 2) {
                    Weva.Forms.Text.WordBoundary.WordRangeAt(Model.Text ?? "", taIdx, out int ws, out int we);
                    if (ws < dragAnchorStart) Model.SetSelection(dragAnchorEnd, ws);
                    else Model.SetSelection(dragAnchorStart, Math.Max(we, dragAnchorEnd));
                } else {
                    Model.SetSelection(dragAnchor, taIdx);
                }
                return;
            }
            if (dragDetail >= 3) return; // select-all sticks for the whole drag
            int idx = CaretIndexFromPointer(pe.X);
            if (idx < 0) return;
            if (dragDetail == 2) {
                // Drag-by-word: union of the anchor word and the word under
                // the pointer, direction away from the anchor (Chrome).
                Weva.Forms.Text.WordBoundary.WordRangeAt(
                    DisplayTextForPointer(), idx, out int ws, out int we);
                if (ws < dragAnchorStart) Model.SetSelection(dragAnchorEnd, ws);
                else Model.SetSelection(dragAnchorStart, Math.Max(we, dragAnchorEnd));
                return;
            }
            Model.SetSelection(dragAnchor, idx);
        }

        // The RENDERED text pointer gestures map against (password bullet
        // mask) — indices are code-unit aligned with the model text.
        string DisplayTextForPointer() {
            return Element.GetAttribute("type") == "password"
                ? InputRenderer.BulletMask((Model.Text ?? "").Length)
                : Model.Text ?? "";
        }

        void OnPointerUp(UIEvent evtBase) {
            if (!pointerSelecting) {
                // Defensive: a pointer-down that armed the suppression but
                // never entered a selection drag (textarea caret placement,
                // or a capture-phase consumer eating the gesture) must not
                // leave the flag latched for a later keyboard focus (audit
                // #12 residual-fragility note).
                suppressFocusSelectAll = false;
                return;
            }
            pointerSelecting = false;
            suppressFocusSelectAll = false; // focus (if any) has fired by now
            textAreaDragMap = null;
            dispatcher.ReleasePointerCapture(Element);
        }

        // Input/selection audit #7 — persistent horizontal edit-scroll.
        // Chrome model: the visible window is STATE; a caret move scrolls it
        // only when the caret would leave it (minimal move to the nearer
        // edge). The old stateless derivation pinned the caret to the exact
        // right edge whenever the value overflowed (text after the caret was
        // never visible) and JUMPED to the string start once the caret
        // re-entered the first window. The painter consumes this via
        // InputCaretGeometry.ScrollX; CaretIndexFromPointer maps against it.
        double editScrollX;
        public double EditScrollX => editScrollX;
        const double EditScrollSlack = 2.0; // the 1px bar + a hair of context

        void EnsureCaretVisibleScroll() {
            if (IsTextArea) return; // the single-line edit window doesn't apply
            var box = ElementToBox?.Invoke(Element);
            if (box == null || box.Width <= 0) return;
            double availW = box.Width - box.PaddingLeft - box.PaddingRight - box.BorderLeft - box.BorderRight;
            if (availW <= 0) return;
            string display = DisplayTextForPointer();
            double caretX = Model.CaretXForIndex(Model.Selection.Focus, display);
            if (caretX - editScrollX > availW - EditScrollSlack) {
                editScrollX = caretX + EditScrollSlack - availW;   // follow right
            } else if (caretX - editScrollX < 0) {
                editScrollX = caretX;                              // reveal left
            }
            double textW = Model.CaretXForIndex((display ?? "").Length, display);
            double maxScroll = System.Math.Max(0, textW + EditScrollSlack - availW);
            if (editScrollX > maxScroll) editScrollX = maxScroll;
            if (editScrollX < 0) editScrollX = 0;
        }

        // Maps a document-space pointer X to a caret slot in the RENDERED
        // value, mirroring the painter's coordinate model (content box +
        // password mask + the persistent edit-scroll). -1 when the box or
        // measurer isn't available. A pointer past the field's horizontal
        // edges nudges one slot beyond the visible window per event — each
        // step pulls the scroll along (EnsureCaretVisibleScroll), so a drag
        // held outside the field progressively selects + scrolls, the
        // per-event approximation of Chrome's timed autoscroll.
        int CaretIndexFromPointer(double pointerX) {
            var box = ElementToBox?.Invoke(Element);
            if (box == null || box.Width <= 0) return -1;
            double contentLeft = AbsoluteLeft(box) + box.PaddingLeft + box.BorderLeft;
            double availW = box.Width - box.PaddingLeft - box.PaddingRight - box.BorderLeft - box.BorderRight;
            string display = DisplayTextForPointer();
            double rel = pointerX - contentLeft;
            if (pointerSelecting && rel < 0) {
                int edge = Model.CaretIndexForX(editScrollX, display);
                return System.Math.Max(0, edge - 1);
            }
            if (pointerSelecting && rel > availW) {
                int edge = Model.CaretIndexForX(editScrollX + availW, display);
                return System.Math.Min((display ?? "").Length, edge + 1);
            }
            return Model.CaretIndexForX(rel + editScrollX, display);
        }

        static double AbsoluteLeft(Weva.Layout.Boxes.Box box) {
            double x = 0;
            for (var b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        static double AbsoluteTop(Weva.Layout.Boxes.Box box) {
            double y = 0;
            for (var b = box; b != null; b = b.Parent) y += b.Y;
            return y;
        }

        // The logical line (newline-delimited, terminator inclusive) at a
        // caret index — Chrome's textarea triple-click paragraph unit.
        internal static void LineRangeAt(string t, int idx, out int start, out int end) {
            if (string.IsNullOrEmpty(t)) { start = 0; end = 0; return; }
            if (idx < 0) idx = 0;
            if (idx > t.Length) idx = t.Length;
            start = idx > 0 ? t.LastIndexOf('\n', idx - 1) + 1 : 0;
            int nl = idx < t.Length ? t.IndexOf('\n', idx) : -1;
            end = nl < 0 ? t.Length : nl + 1;
        }

        string lastCommitedValue = "";

        void OnBlur(UIEvent _) {
            if (DroppedOrDetached()) return;
            // Audit #7: Chrome shows the START of an overflowing value when
            // the field is unfocused — reset the edit window on blur.
            editScrollX = 0;
            // On blur, fire a change event if the value differs from the
            // last-committed value. Reactivity already saw any intermediate
            // writes; the change event is the spec-defined commit signal.
            ValueCommitted?.Invoke();
            string current = Model.Text;
            if (current != lastCommitedValue) {
                lastCommitedValue = current;
                FormSubmissionEvents.DispatchChange(dispatcher, Element);
            }
        }

        void OnClick(UIEvent evt) {
            if (DroppedOrDetached()) return;
            if (Element.TagName != "input") return;
            if (DisabledAttr()) return;
            var type = Element.GetAttribute("type");
            if (type == "checkbox") {
                bool wasChecked = Element.HasAttribute("checked");
                if (wasChecked) Element.RemoveAttribute("checked");
                else Element.SetAttribute("checked", "");
                MarkUserInteracted();
                ValueCommitted?.Invoke();
                FormSubmissionEvents.DispatchChange(dispatcher, Element);
            } else if (type == "radio") {
                bool wasChecked = Element.HasAttribute("checked");
                var name = Element.GetAttribute("name");
                if (!string.IsNullOrEmpty(name)) {
                    UncheckRadioGroup(name, Element);
                }
                Element.SetAttribute("checked", "");
                MarkUserInteracted();
                ValueCommitted?.Invoke();
                if (!wasChecked) {
                    FormSubmissionEvents.DispatchChange(dispatcher, Element);
                }
            }
        }

        void UncheckRadioGroup(string name, Element except) {
            var scope = FindRadioScope(Element);
            if (scope == null) return;
            UncheckIn(scope, name, except);
        }

        static Node FindRadioScope(Element fromElement) {
            for (var n = fromElement.Parent as Element; n != null; n = n.Parent as Element) {
                if (n.TagName == "form") return n;
            }
            return fromElement.OwnerDocument;
        }

        static void UncheckIn(Node n, string name, Element except) {
            foreach (var c in n.Children) {
                if (c is Element e) {
                    if (e != except &&
                        e.TagName == "input" &&
                        e.GetAttribute("type") == "radio" &&
                        e.GetAttribute("name") == name) {
                        e.RemoveAttribute("checked");
                    }
                    UncheckIn(e, name, except);
                }
            }
        }

        void OnKey(UIEvent evtBase) {
            if (DroppedOrDetached()) return;
            if (DisabledAttr()) return;
            if (!(evtBase is KeyboardEvent evt)) return;
            // FM7: honor author preventDefault(). The engine's own convention
            // gates default actions on !DefaultPrevented (see the dispatcher's
            // Tab/spatial-nav gate); a capture-phase author listener filtering
            // KeyDown — the standard way to block characters — must stop the
            // edit action, exactly as in a browser.
            if (evt.DefaultPrevented) return;

            if (evt.IsTextInput()) {
                if (ReadOnlyAttr()) return;
                if (Element.TagName == "input") {
                    var type = Element.GetAttribute("type");
                    if (type == "checkbox" || type == "radio" || type == "button" ||
                        type == "submit" || type == "reset" || type == "hidden" || type == "range") {
                        return;
                    }
                    if (type == "number") {
                        var payload = evt.TextInputPayload();
                        if (!IsNumericPayload(payload)) {
                            evt.PreventDefault();
                            return;
                        }
                    }
                }
                MarkUserInteracted();
                Model.Insert(evt.TextInputPayload());
                evt.PreventDefault();
                return;
            }

            switch (evt.Key) {
                case "Backspace":
                    if (ReadOnlyAttr()) return;
                    MarkUserInteracted();
                    if (evt.CtrlKey) Model.DeleteWordBackward();
                    else Model.DeleteBackward();
                    evt.PreventDefault();
                    break;
                case "Delete":
                    if (ReadOnlyAttr()) return;
                    MarkUserInteracted();
                    if (evt.CtrlKey) Model.DeleteWordForward();
                    else Model.DeleteForward();
                    evt.PreventDefault();
                    break;
                case "ArrowLeft":
                    if (evt.CtrlKey) Model.MoveWordLeft(evt.ShiftKey);
                    else Model.MoveCaretLeft(evt.ShiftKey);
                    evt.PreventDefault();
                    break;
                case "ArrowRight":
                    if (evt.CtrlKey) Model.MoveWordRight(evt.ShiftKey);
                    else Model.MoveCaretRight(evt.ShiftKey);
                    evt.PreventDefault();
                    break;
                case "ArrowUp":
                    if (Element.TagName == "input") {
                        var t = Element.GetAttribute("type");
                        if (t == "number") {
                            MarkUserInteracted();
                            StepNumber(+1);
                            evt.PreventDefault();
                            return;
                        } else if (t == "range") {
                            MarkUserInteracted();
                            StepRange(+1);
                            evt.PreventDefault();
                            return;
                        }
                    }
                    if (Model.Multiline) Model.MoveLineUp(evt.ShiftKey);
                    evt.PreventDefault();
                    break;
                case "ArrowDown":
                    if (Element.TagName == "input") {
                        var t = Element.GetAttribute("type");
                        if (t == "number") {
                            MarkUserInteracted();
                            StepNumber(-1);
                            evt.PreventDefault();
                            return;
                        } else if (t == "range") {
                            MarkUserInteracted();
                            StepRange(-1);
                            evt.PreventDefault();
                            return;
                        }
                    }
                    if (Model.Multiline) Model.MoveLineDown(evt.ShiftKey);
                    evt.PreventDefault();
                    break;
                case "Home":
                    Model.MoveToHome(evt.ShiftKey, wholeText: evt.CtrlKey);
                    evt.PreventDefault();
                    break;
                case "End":
                    Model.MoveToEnd(evt.ShiftKey, wholeText: evt.CtrlKey);
                    evt.PreventDefault();
                    break;
                case "Enter":
                    if (Model.Multiline) {
                        if (ReadOnlyAttr()) return;
                        MarkUserInteracted();
                        Model.Insert("\n");
                        evt.PreventDefault();
                    } else {
                        ValueCommitted?.Invoke();
                        // Per HTML spec, Enter on a single-line text control
                        // synthesizes a `submit` event on the enclosing form (if
                        // any) and a commit-time `change` event on the control.
                        string current = Model.Text;
                        if (current != lastCommitedValue) {
                            lastCommitedValue = current;
                            FormSubmissionEvents.DispatchChange(dispatcher, Element);
                        }
                        var form = FormSubmissionEvents.FindEnclosingForm(Element);
                        if (form != null) {
                            FormSubmissionEvents.DispatchSubmit(dispatcher, form, Element);
                        }
                    }
                    break;
                case "Escape":
                    if (Model.IsComposing) Model.CancelComposition();
                    break;
                case "a":
                case "A":
                    if (evt.CtrlKey || evt.MetaKey) {
                        Model.SelectAll();
                        evt.PreventDefault();
                    }
                    break;
                case "z":
                case "Z":
                    if (evt.CtrlKey || evt.MetaKey) {
                        if (ReadOnlyAttr()) return;
                        MarkUserInteracted();
                        // Ctrl+Shift+Z = redo (Chrome supports both this and Ctrl+Y).
                        if (evt.ShiftKey) Model.Redo(); else Model.Undo();
                        evt.PreventDefault();
                    }
                    break;
                case "y":
                case "Y":
                    if (evt.CtrlKey) {
                        if (ReadOnlyAttr()) return;
                        MarkUserInteracted();
                        Model.Redo();
                        evt.PreventDefault();
                    }
                    break;
                // Input/selection audit #5 — clipboard. Chrome semantics:
                // copy allowed in readonly fields but BLOCKED in password
                // fields; cut needs a writable field; paste sanitizes CR/LF
                // out of single-line values (HTML value sanitization).
                case "c":
                case "C":
                    if ((evt.CtrlKey || evt.MetaKey) && Clipboard != null) {
                        var selC = Model.Selection;
                        if (selC.End > selC.Start && !IsPassword()) {
                            Clipboard.SetText(Model.Text.Substring(selC.Start, selC.End - selC.Start));
                        }
                        evt.PreventDefault();
                    }
                    break;
                case "x":
                case "X":
                    if ((evt.CtrlKey || evt.MetaKey) && Clipboard != null) {
                        var selX = Model.Selection;
                        if (selX.End > selX.Start && !IsPassword() && !ReadOnlyAttr()) {
                            Clipboard.SetText(Model.Text.Substring(selX.Start, selX.End - selX.Start));
                            MarkUserInteracted();
                            Model.Insert(""); // delete the selection through the edit path
                        }
                        evt.PreventDefault();
                    }
                    break;
                case "v":
                case "V":
                    if ((evt.CtrlKey || evt.MetaKey) && Clipboard != null) {
                        if (!ReadOnlyAttr()) {
                            string paste = Clipboard.GetText();
                            if (!string.IsNullOrEmpty(paste)) {
                                if (!Model.Multiline) {
                                    paste = paste.Replace("\r", "").Replace("\n", "");
                                }
                                if (paste.Length != 0) {
                                    MarkUserInteracted();
                                    Model.Insert(paste);
                                }
                            }
                        }
                        evt.PreventDefault();
                    }
                    break;
            }
        }

        // Input/selection audit #5: clipboard bridge. Wired by
        // FormControlsRegistry / UIDocumentBuilder (UnityClipboardBridge on
        // desktop/editor); null leaves Ctrl+C/X/V inert (headless callers
        // stub with InMemoryClipboardBridge).
        public Weva.Forms.Bridge.IClipboardBridge Clipboard { get; set; }

        bool IsPassword() =>
            Element.TagName == "input"
            && string.Equals(Element.GetAttribute("type"), "password", StringComparison.OrdinalIgnoreCase);

        static bool IsNumericPayload(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) {
                if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E')) return false;
            }
            return true;
        }

        void StepNumber(int direction) {
            var ie = new InputElement(Element);
            double cur;
            if (!double.TryParse(ie.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cur)) cur = 0;
            double step = 1;
            if (!string.IsNullOrEmpty(ie.Step)) double.TryParse(ie.Step, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out step);
            cur += step * direction;
            cur = ClampToMinMax(cur, ie.Min, ie.Max);
            ie.Value = cur.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Keep model in sync (since attribute write feeds through).
            Model.SetText(ie.Value);
        }

        void StepRange(int direction) {
            var ie = new InputElement(Element);
            double cur;
            if (!double.TryParse(ie.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cur)) cur = 0;
            double step = 1;
            if (!string.IsNullOrEmpty(ie.Step)) double.TryParse(ie.Step, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out step);
            cur += step * direction;
            cur = ClampToMinMax(cur, ie.Min, ie.Max);
            ie.Value = cur.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static double ClampToMinMax(double v, string minStr, string maxStr) {
            double minV;
            if (!string.IsNullOrEmpty(minStr) && double.TryParse(minStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out minV)) {
                if (v < minV) v = minV;
            }
            double maxV;
            if (!string.IsNullOrEmpty(maxStr) && double.TryParse(maxStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out maxV)) {
                if (v > maxV) v = maxV;
            }
            return v;
        }

        void OnModelChanged() {
            if (suppressWriteBack) return;
            try {
                suppressWriteBack = true;
                Element.SetAttribute("value", Model.Text);
                // FM1: a <textarea>'s RENDERED content is its child TextNodes,
                // not the value attribute — layout and paint read the
                // children, so without this sync typing was swallowed
                // invisibly (the attribute changed, keystrokes were
                // PreventDefault'ed, the screen never moved). Mirroring the
                // model into the child TextNode makes the edit visible
                // through the normal text pipeline: TextNode.Data raises
                // TextChanged, which marks the textarea for layout + paint.
                if (Element.TagName == "textarea") SyncTextAreaChildText();
            } finally {
                suppressWriteBack = false;
            }
            ValueChanged?.Invoke();
            FormSubmissionEvents.DispatchInput(dispatcher, Element);
        }

        // Canonical single-TextNode form: the first TextNode receives the
        // full model text (created on demand for an initially-empty
        // textarea); any further parse-time TextNodes are emptied.
        void SyncTextAreaChildText() {
            TextNode first = null;
            var kids = Element.Children;
            for (int i = 0; i < kids.Count; i++) {
                if (kids[i] is TextNode t) {
                    if (first == null) first = t;
                    else if (t.Data != "") t.Data = "";
                }
            }
            string text = Model.Text ?? "";
            if (first != null) {
                first.Data = text;
            } else if (text.Length != 0) {
                Element.AppendChild(new TextNode(text));
            }
        }

        // Input/selection audit #3: caret-activity signal for the blink timer.
        // Chrome resets the blink phase on every keystroke / caret move so the
        // caret is SOLID while the user is active; the lifecycle anchors the
        // blink clock to the last invocation of this hook. Wired by
        // FormControlsRegistry / UIDocumentBuilder; null in bare tests.
        public Action CaretActivity { get; set; }

        void OnSelectionChanged() {
            if (DroppedOrDetached()) return;
            CaretActivity?.Invoke();
            // Audit #7: keep the persistent edit window tracking the caret
            // (minimal move — only when the caret would leave the window).
            EnsureCaretVisibleScroll();
            // Repaint the input so the caret/selection overlay re-emits at the new
            // position (a caret move alone changes no attribute, so nothing else
            // would mark this element dirty).
            tracker?.MarkDirty(Element, InvalidationKind.Paint);
        }

        void MarkUserInteracted() {
            dispatcher?.StateProvider?.SetFlag(Element, Weva.Css.Selectors.ElementState.UserInteracted, true);
        }

        void OnImeUpdated(string composing) {
            if (DroppedOrDetached()) return;
            Model.UpdateComposition(composing);
        }

        void OnImeCommitted(string finalText) {
            if (DroppedOrDetached()) return;
            if (!string.IsNullOrEmpty(finalText)) MarkUserInteracted();
            Model.CommitComposition(finalText);
            ValueCommitted?.Invoke();
        }

        void OnImeCancelled() {
            if (DroppedOrDetached()) return;
            Model.CancelComposition();
        }
    }
}
