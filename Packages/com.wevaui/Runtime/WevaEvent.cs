// One core event as WevaDocument.Event raises it: the kind, the element it
// targets, where the pointer was, the text a key or a control produced, and
// the on-<event> handler name the markup named. The kinds mirror the ABI's
// weva_event_kind value for value (a test pins the two enums together).
using UnityEngine;

namespace Weva
{
    public enum WevaEventKind
    {
        None = 0,
        PointerDown = 1,
        PointerUp = 2,
        Click = 3,
        PointerEnter = 4,
        PointerLeave = 5,
        KeyDown = 6,
        KeyUp = 7,
        TextInput = 8,
        Focus = 9,
        Blur = 10,
        ValueChanged = 11,
        Change = 12,
        Submit = 13,
        Scroll = 14,
        Toggle = 15,
        ContextMenu = 16,
        CompositionStart = 17,
        CompositionUpdate = 18,
        CompositionEnd = 19,
        Reset = 20,
        Close = 21,
        Cancel = 22,
        Invalid = 23,
        BeforeToggle = 24,
    }

    public readonly struct WevaEvent
    {
        public readonly WevaEventKind Kind;
        /// <summary>The element the event targets; None for a document-level event.</summary>
        public readonly WevaElement Target;
        /// <summary>The pointer position in document pixels, for pointer events.</summary>
        public readonly Vector2 Position;
        /// <summary>The pointer buttons held, as a bit mask (1 = primary, 2 = secondary, 4 = middle).</summary>
        public readonly uint Buttons;
        /// <summary>The text a key produced, a control's value, or a toggle state; empty otherwise.</summary>
        public readonly string Text;
        /// <summary>The value of the nearest <c>on-&lt;event&gt;</c> attribute, or empty.</summary>
        public readonly string Handler;
        public readonly bool Shift, Ctrl, Alt, Meta;

        internal WevaEvent(WevaEventKind kind, WevaElement target, Vector2 position, uint buttons, uint modifiers, string text, string handler)
        {
            Kind = kind;
            Target = target;
            Position = position;
            Buttons = buttons;
            Text = text ?? string.Empty;
            Handler = handler ?? string.Empty;
            Shift = (modifiers & 1u) != 0;
            Ctrl = (modifiers & 2u) != 0;
            Alt = (modifiers & 4u) != 0;
            Meta = (modifiers & 8u) != 0;
        }

        public override string ToString() => Kind + (Target.IsValid ? " " + Target : string.Empty) + (Handler.Length > 0 ? " on-" + Handler : string.Empty);
    }
}
