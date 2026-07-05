using System;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Forms {
    // InputState — per-element reactive snapshot for a textual <input> or <textarea>.
    //
    // This is a thin wrapper over the existing TextEditModel and the underlying
    // DOM attributes (value/disabled/readonly). InputController is the operational
    // driver; InputState is the addressable state object the rest of the v1
    // surface (renderer, bindings, tests) consults to read what the box currently
    // looks like.
    //
    // The reactive Version bumps on every observable mutation (Value, cursor,
    // selection anchor, ReadOnly, Disabled). Consumers can keyed-cache against
    // Version; we follow the same convention as ComputedStyle.Version.
    public sealed class InputState : IVersioned {
        public Element Element { get; }
        long version;

        string value = "";
        int cursorIndex;
        int selectionAnchor;

        public event Action Changed;

        public InputState(Element element) {
            if (element == null) throw new ArgumentNullException(nameof(element));
            Element = element;
            value = element.GetAttribute("value") ?? "";
            cursorIndex = value.Length;
            selectionAnchor = cursorIndex;
            version = 1;
        }

        public long Version => version;

        public string Value {
            get => value;
            set {
                var v = value ?? "";
                if (v == this.value) return;
                this.value = v;
                if (cursorIndex > v.Length) cursorIndex = v.Length;
                if (selectionAnchor > v.Length) selectionAnchor = v.Length;
                Bump();
            }
        }

        public int CursorIndex {
            get => cursorIndex;
            set {
                int n = value;
                if (n < 0) n = 0;
                if (n > this.value.Length) n = this.value.Length;
                if (n == cursorIndex) return;
                cursorIndex = n;
                Bump();
            }
        }

        public int SelectionAnchor {
            get => selectionAnchor;
            set {
                int n = value;
                if (n < 0) n = 0;
                if (n > this.value.Length) n = this.value.Length;
                if (n == selectionAnchor) return;
                selectionAnchor = n;
                Bump();
            }
        }

        public bool ReadOnly {
            get => Element.HasAttribute("readonly");
            set {
                if (ReadOnly == value) return;
                if (value) Element.SetAttribute("readonly", "");
                else Element.RemoveAttribute("readonly");
                Bump();
            }
        }

        public bool Disabled {
            get => Element.HasAttribute("disabled");
            set {
                if (Disabled == value) return;
                if (value) Element.SetAttribute("disabled", "");
                else Element.RemoveAttribute("disabled");
                Bump();
            }
        }

        public int SelectionStart => cursorIndex < selectionAnchor ? cursorIndex : selectionAnchor;
        public int SelectionEnd => cursorIndex > selectionAnchor ? cursorIndex : selectionAnchor;
        public bool HasSelection => cursorIndex != selectionAnchor;

        public void SetCaret(int index) {
            int n = index;
            if (n < 0) n = 0;
            if (n > value.Length) n = value.Length;
            if (cursorIndex == n && selectionAnchor == n) return;
            cursorIndex = n;
            selectionAnchor = n;
            Bump();
        }

        public void SetSelection(int anchor, int caret) {
            int a = anchor; int c = caret;
            if (a < 0) a = 0; if (a > value.Length) a = value.Length;
            if (c < 0) c = 0; if (c > value.Length) c = value.Length;
            if (a == selectionAnchor && c == cursorIndex) return;
            selectionAnchor = a;
            cursorIndex = c;
            Bump();
        }

        public void ClearSelection() {
            if (selectionAnchor == cursorIndex) return;
            selectionAnchor = cursorIndex;
            Bump();
        }

        // Bind to a TextEditModel: writes the model's text/selection into this state
        // every time the model changes. Returns an unsubscribe action.
        public Action TrackModel(TextEditModel model) {
            if (model == null) throw new ArgumentNullException(nameof(model));
            void Sync() {
                value = model.Text;
                cursorIndex = model.Selection.Focus;
                selectionAnchor = model.Selection.Anchor;
                if (cursorIndex > value.Length) cursorIndex = value.Length;
                if (selectionAnchor > value.Length) selectionAnchor = value.Length;
                Bump();
            }
            model.Changed += Sync;
            model.SelectionChanged += Sync;
            Sync();
            return () => {
                model.Changed -= Sync;
                model.SelectionChanged -= Sync;
            };
        }

        void Bump() {
            unchecked { version++; }
            Changed?.Invoke();
        }
    }
}
