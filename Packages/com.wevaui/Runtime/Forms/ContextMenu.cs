using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Forms {
    // Single menu item description for ContextMenu / DropdownMenu.
    public sealed class MenuItem {
        public string Label { get; set; }
        public Action OnSelect { get; set; }
        public bool Disabled { get; set; }
        public bool IsSeparator { get; set; }
        public string Shortcut { get; set; }   // optional right-aligned hint, e.g. "Ctrl+C"
        public string Icon { get; set; }       // optional left-side icon glyph

        public static MenuItem Item(string label, Action onSelect, bool disabled = false, string shortcut = null, string icon = null) =>
            new MenuItem { Label = label, OnSelect = onSelect, Disabled = disabled, Shortcut = shortcut, Icon = icon };

        public static MenuItem Separator() => new MenuItem { IsSeparator = true };
    }

    // ContextMenu — pops a list-of-items widget at a given (x, y) coordinate
    // and dismisses on outside click, Escape, or item selection. The widget
    // is a plain DOM subtree (`<div class="ui-menu"> <div class="ui-menu-item">…`)
    // injected at the document root with `position: fixed` styles, so author
    // CSS can fully restyle via the `.ui-menu` and `.ui-menu-item` selectors.
    //
    // Use as a one-shot:
    //   ContextMenu.Show(doc, dispatcher, x, y, new[] {
    //       MenuItem.Item("Copy",  () => Copy(),  shortcut:"Ctrl+C"),
    //       MenuItem.Item("Paste", () => Paste(), shortcut:"Ctrl+V"),
    //       MenuItem.Separator(),
    //       MenuItem.Item("Delete", () => Delete(), disabled: !canDelete),
    //   });
    //
    // Pair with ContextualMenuManipulator's MenuRequested callback for the
    // standard right-click + long-press + Shift+F10 affordance.
    public sealed class ContextMenu {
        public string MenuClassName { get; set; } = "ui-menu";
        public string ItemClassName { get; set; } = "ui-menu-item";
        public string SeparatorClassName { get; set; } = "ui-menu-separator";
        public string DisabledClassName { get; set; } = "is-disabled";
        public string FocusedClassName { get; set; } = "is-focused";

        readonly Document doc;
        readonly EventDispatcher dispatcher;
        readonly InvalidationTracker tracker;
        readonly IReadOnlyList<MenuItem> items;
        readonly EventListener outsideClick;
        readonly EventListener keyDown;
        readonly Element root;
        readonly Element[] itemElements;
        int focusedIndex;
        bool dismissed;

        ContextMenu(Document doc, EventDispatcher dispatcher, InvalidationTracker tracker, IReadOnlyList<MenuItem> items) {
            this.doc = doc;
            this.dispatcher = dispatcher;
            this.tracker = tracker;
            this.items = items;
            outsideClick = OnOutsideClick;
            keyDown = OnKey;
            root = new Element("div");
            root.SetAttribute("class", MenuClassName);
            itemElements = new Element[items.Count];
            for (int i = 0; i < items.Count; i++) {
                itemElements[i] = BuildItem(items[i], i);
                root.AppendChild(itemElements[i]);
            }
            focusedIndex = FindFirstFocusable();
            UpdateFocusVisual();
        }

        public Element Root => root;

        // Static one-shot factory. Constructs the menu, attaches it at (x, y),
        // wires dismiss behaviour, and returns a handle so callers can force-
        // dismiss (e.g. to coordinate with their own popover stack). The menu
        // self-dismisses on outside click, Escape, or item activation.
        public static ContextMenu Show(Document doc, EventDispatcher dispatcher, InvalidationTracker tracker,
                                       double x, double y, IReadOnlyList<MenuItem> items) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (items == null) throw new ArgumentNullException(nameof(items));
            var menu = new ContextMenu(doc, dispatcher, tracker, items);
            menu.AttachAt(x, y);
            return menu;
        }

        void AttachAt(double x, double y) {
            root.SetAttribute("style", BuildPositionStyle(x, y));
            doc.AppendChild(root);
            tracker?.MarkDirty(doc, InvalidationKind.Layout | InvalidationKind.Paint);
            // Subscribe at document root with capture so we beat per-element
            // handlers — outside click should dismiss BEFORE the click event
            // reaches its target's own handler (avoid triggering an action
            // and dismissing in the same frame).
            var docRoot = FirstElementChild(doc);
            if (docRoot != null) {
                dispatcher.AddEventListener(docRoot, EventKind.PointerDown, outsideClick, useCapture: true);
                dispatcher.AddEventListener(docRoot, EventKind.KeyDown, keyDown, useCapture: true);
            }
        }

        public void Dismiss() {
            if (dismissed) return;
            dismissed = true;
            var docRoot = FirstElementChild(doc);
            if (docRoot != null) {
                dispatcher.RemoveEventListener(docRoot, EventKind.PointerDown, outsideClick, useCapture: true);
                dispatcher.RemoveEventListener(docRoot, EventKind.KeyDown, keyDown, useCapture: true);
            }
            root.Parent?.RemoveChild(root);
            tracker?.MarkDirty(doc, InvalidationKind.Layout | InvalidationKind.Paint);
        }

        Element BuildItem(MenuItem item, int index) {
            if (item.IsSeparator) {
                var sep = new Element("div");
                sep.SetAttribute("class", SeparatorClassName);
                return sep;
            }
            var div = new Element("div");
            string cls = ItemClassName;
            if (item.Disabled) cls += " " + DisabledClassName;
            div.SetAttribute("class", cls);
            div.SetAttribute("data-menu-index", index.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(item.Icon)) {
                var icon = new Element("span");
                icon.SetAttribute("class", "ui-menu-icon");
                icon.AppendChild(new TextNode(item.Icon));
                div.AppendChild(icon);
            }
            var label = new Element("span");
            label.SetAttribute("class", "ui-menu-label");
            label.AppendChild(new TextNode(item.Label ?? ""));
            div.AppendChild(label);
            if (!string.IsNullOrEmpty(item.Shortcut)) {
                var sh = new Element("span");
                sh.SetAttribute("class", "ui-menu-shortcut");
                sh.AppendChild(new TextNode(item.Shortcut));
                div.AppendChild(sh);
            }
            EventListener clickListener = null;
            clickListener = _ => {
                if (item.Disabled) return;
                Activate(index);
            };
            dispatcher.AddEventListener(div, EventKind.Click, clickListener);
            return div;
        }

        int FindFirstFocusable() {
            for (int i = 0; i < items.Count; i++) {
                if (!items[i].IsSeparator && !items[i].Disabled) return i;
            }
            return -1;
        }

        void Activate(int index) {
            if (dismissed) return;
            if (index < 0 || index >= items.Count) return;
            var item = items[index];
            if (item.IsSeparator || item.Disabled) return;
            // Dismiss BEFORE invoking — the action might mutate the doc and
            // a subsequent layout pass shouldn't see a leaked menu. If the
            // action throws, the menu still goes away (use try/finally).
            try { Dismiss(); }
            finally { item.OnSelect?.Invoke(); }
        }

        void OnOutsideClick(UIEvent evt) {
            if (!(evt is PointerEvent)) return;
            // Walk up from the event target; if we hit our menu root we're
            // clicking inside, ignore (item handlers will fire normally).
            for (var n = evt.Target; n != null; n = n.Parent as Element) {
                if (n == root) return;
            }
            Dismiss();
        }

        void OnKey(UIEvent evt) {
            if (!(evt is KeyboardEvent ke)) return;
            switch (ke.Key) {
                case "Escape":
                    ke.PreventDefault();
                    Dismiss();
                    return;
                case "ArrowDown":
                    ke.PreventDefault();
                    StepFocus(+1);
                    return;
                case "ArrowUp":
                    ke.PreventDefault();
                    StepFocus(-1);
                    return;
                case "Home":
                    ke.PreventDefault();
                    focusedIndex = FindFirstFocusable();
                    UpdateFocusVisual();
                    return;
                case "End":
                    ke.PreventDefault();
                    for (int i = items.Count - 1; i >= 0; i--) {
                        if (!items[i].IsSeparator && !items[i].Disabled) { focusedIndex = i; break; }
                    }
                    UpdateFocusVisual();
                    return;
                case "Enter":
                case " ":
                    ke.PreventDefault();
                    if (focusedIndex >= 0) Activate(focusedIndex);
                    return;
            }
        }

        void StepFocus(int delta) {
            if (items.Count == 0) return;
            int i = focusedIndex < 0 ? (delta > 0 ? -1 : items.Count) : focusedIndex;
            for (int n = 0; n < items.Count; n++) {
                i += delta;
                if (i < 0) i = items.Count - 1;
                else if (i >= items.Count) i = 0;
                if (!items[i].IsSeparator && !items[i].Disabled) {
                    focusedIndex = i;
                    UpdateFocusVisual();
                    return;
                }
            }
        }

        void UpdateFocusVisual() {
            for (int i = 0; i < itemElements.Length; i++) {
                if (itemElements[i] == null) continue;
                var cur = itemElements[i].GetAttribute("class") ?? "";
                bool wantFocus = i == focusedIndex && !items[i].IsSeparator && !items[i].Disabled;
                bool hasFocus = cur.Contains(FocusedClassName);
                if (wantFocus == hasFocus) continue;
                if (wantFocus) itemElements[i].SetAttribute("class", cur.TrimEnd() + " " + FocusedClassName);
                else itemElements[i].SetAttribute("class", cur.Replace(" " + FocusedClassName, "").Replace(FocusedClassName, "").Trim());
            }
        }

        static Element FirstElementChild(Document d) {
            foreach (var c in d.Children) if (c is Element e) return e;
            return null;
        }

        static string BuildPositionStyle(double x, double y) {
            return $"position:fixed;left:{x.ToString("R", CultureInfo.InvariantCulture)}px;top:{y.ToString("R", CultureInfo.InvariantCulture)}px;z-index:99998";
        }
    }
}
