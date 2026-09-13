// An element of a WevaDocument, as a game sees it: a value type holding the
// core's handle, the document it belongs to and the document generation the
// handle was issued in. A Reload() replaces the tree, so a handle from before
// it is stale: IsValid says so, and every member on a stale or missing
// element returns a default rather than throwing.
//
// Writes (Value, SetAttribute, Focus, ScrollTo, ShowDialog) land on the next
// frame; text changes go through bindings, not this type.
using System;
using UnityEngine;
using Weva.Native;

namespace Weva
{
    public readonly struct WevaElement : IEquatable<WevaElement>
    {
        internal readonly WevaDocument Owner;
        internal readonly uint Handle;
        internal readonly int Generation;

        internal WevaElement(WevaDocument owner, uint handle, int generation)
        {
            Owner = owner;
            Handle = handle;
            Generation = generation;
        }

        /// <summary>The element that is not there: what Query returns for a selector nothing matches.</summary>
        public static WevaElement None => default;

        /// <summary>False for None, for an element of a document since reloaded or disabled.</summary>
        public bool IsValid => Owner != null && Handle != WevaNative.WEVA_ELEMENT_NONE && Owner.Generation == Generation && Owner.Document != null;

        private NativeDocument Doc => IsValid ? Owner.Document : null;

        /// <summary>The <c>id</c> attribute, or empty.</summary>
        public string Id => Doc?.ElementId(Handle) ?? string.Empty;
        /// <summary>The tag name, lower case.</summary>
        public string Tag => Doc?.TagName(Handle) ?? string.Empty;
        /// <summary>The element's text content.</summary>
        public string Text => Doc?.ElementText(Handle) ?? string.Empty;

        /// <summary>A form control's live value; setting it is what the user typing it would do (events, bindings and all).</summary>
        public string Value
        {
            get => Doc?.ElementValue(Handle) ?? string.Empty;
            set => Doc?.SetElementValue(Handle, value ?? string.Empty);
        }

        public string Attribute(string name) => Doc?.ElementAttribute(Handle, name) ?? string.Empty;
        public bool HasAttribute(string name) => Doc?.ElementHasAttribute(Handle, name) ?? false;
        /// <summary>Sets an attribute; the cascade follows on the next frame. Returns false on a stale element.</summary>
        public bool SetAttribute(string name, string value) => Doc?.SetElementAttribute(Handle, name, value ?? string.Empty) ?? false;

        /// <summary>Whether the <c>class</c> attribute lists the name.</summary>
        public bool HasClass(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string token in Attribute("class").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == name) return true;
            }
            return false;
        }

        /// <summary>Moves keyboard focus here (as <c>element.focus()</c> would). False on a stale element.</summary>
        public bool Focus() => Doc?.SetFocus(Handle) ?? false;
        public bool IsFocused => IsValid && Owner.Document.Focus == Handle;

        /// <summary>The border box in document pixels after the last update; Rect.zero for an element without a box.</summary>
        public Rect Bounds
        {
            get
            {
                NativeDocument d = Doc;
                if (d != null && d.TryGetBounds(Handle, out NativeBounds b)) return new Rect((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height);
                return Rect.zero;
            }
        }

        /// <summary>A scroll container's offset in document pixels; zero for anything else.</summary>
        public Vector2 Scroll
        {
            get
            {
                NativeDocument d = Doc;
                return d != null && d.TryGetElementScroll(Handle, out double x, out double y, out _, out _) ? new Vector2((float)x, (float)y) : Vector2.zero;
            }
        }

        /// <summary>How far a scroll container can scroll.</summary>
        public Vector2 MaxScroll
        {
            get
            {
                NativeDocument d = Doc;
                return d != null && d.TryGetElementScroll(Handle, out _, out _, out double mx, out double my) ? new Vector2((float)mx, (float)my) : Vector2.zero;
            }
        }

        /// <summary>Scrolls a container to an offset, clamped to what it can scroll (nothing, for a non-scroller) and eased when it has <c>scroll-behavior: smooth</c>. False on a stale element.</summary>
        public bool ScrollTo(float x, float y) => Doc?.SetElementScroll(Handle, x, y) ?? false;
        public bool ScrollTo(Vector2 offset) => ScrollTo(offset.x, offset.y);

        /// <summary>The <c>data-each</c> row this element sits in: its index and <c>data-key</c>. False outside a row.</summary>
        public bool TryGetRow(out int index, out string key)
        {
            index = -1;
            key = string.Empty;
            NativeDocument d = Doc;
            return d != null && d.TryGetRow(Handle, out index, out key);
        }

        /// <summary>The <c>data-each</c> row index, or -1 outside a row.</summary>
        public int RowIndex => TryGetRow(out int index, out _) ? index : -1;
        /// <summary>The <c>data-each</c> row's <c>data-key</c>, or empty outside a row.</summary>
        public string RowKey => TryGetRow(out _, out string key) ? key : string.Empty;

        /// <summary>Opens a <c>&lt;dialog&gt;</c> (modal traps focus and blocks the page behind it). False for another element.</summary>
        public bool ShowDialog(bool modal = true) => Doc?.ShowDialog(Handle, modal) ?? false;
        /// <summary>Closes a <c>&lt;dialog&gt;</c>; <c>on-close</c> fires.</summary>
        public bool CloseDialog() => Doc?.CloseDialog(Handle) ?? false;

        public WevaElement Parent
        {
            get
            {
                NativeDocument d = Doc;
                return d != null ? new WevaElement(Owner, d.Parent(Handle), Generation) : None;
            }
        }

        public WevaElement[] Children
        {
            get
            {
                NativeDocument d = Doc;
                if (d == null) return Array.Empty<WevaElement>();
                uint[] handles = d.Children(Handle);
                var result = new WevaElement[handles.Length];
                for (int i = 0; i < handles.Length; i++) result[i] = new WevaElement(Owner, handles[i], Generation);
                return result;
            }
        }

        // Two elements are the same when they name the same node of the same
        // tree; every invalid element (None, a miss, a stale handle) is the
        // same "nothing", so `Query("#x") == WevaElement.None` reads naturally.
        public bool Equals(WevaElement other)
        {
            bool valid = IsValid, otherValid = other.IsValid;
            if (!valid || !otherValid) return valid == otherValid;
            return ReferenceEquals(Owner, other.Owner) && Handle == other.Handle && Generation == other.Generation;
        }
        public override bool Equals(object obj) => obj is WevaElement other && Equals(other);
        public override int GetHashCode() => IsValid ? unchecked((Owner.GetHashCode() * 397 ^ (int)Handle) * 31 + Generation) : 0;
        public static bool operator ==(WevaElement a, WevaElement b) => a.Equals(b);
        public static bool operator !=(WevaElement a, WevaElement b) => !a.Equals(b);

        public override string ToString()
        {
            if (!IsValid) return "(none)";
            string id = Id;
            return id.Length > 0 ? "<" + Tag + "#" + id + ">" : "<" + Tag + ">";
        }
    }
}
