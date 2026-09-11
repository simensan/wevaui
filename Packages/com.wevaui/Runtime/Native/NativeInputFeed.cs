// Feeds Unity input to a core-hosted document the way the Godot host's
// _gui_input does: pointer position and buttons as one state, the wheel as a
// scroll at a point, engine keys as edges, everything a key produces as text,
// and the clipboard and undo shortcuts a browser has. Reads the Input System
// (the package's dependency); a game that routes input itself calls the
// document's own SetPointer/Key/TryTextInput instead of enabling this.
//
// Coordinates: Unity's pointer has its origin at the bottom-left of the
// screen, the document at its top-left, so y is flipped by the viewport
// height the document was laid out at.
#if WEVA_INPUTSYSTEM
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Weva.Native
{
    public sealed class NativeInputFeed
    {
        private struct EngineKey
        {
            public Key Unity;
            public weva_key Weva;
        }

        private static readonly EngineKey[] EngineKeys =
        {
            new EngineKey { Unity = Key.Tab, Weva = weva_key.WEVA_KEY_TAB },
            new EngineKey { Unity = Key.Enter, Weva = weva_key.WEVA_KEY_ENTER },
            new EngineKey { Unity = Key.NumpadEnter, Weva = weva_key.WEVA_KEY_ENTER },
            new EngineKey { Unity = Key.Space, Weva = weva_key.WEVA_KEY_SPACE },
            new EngineKey { Unity = Key.Escape, Weva = weva_key.WEVA_KEY_ESCAPE },
            new EngineKey { Unity = Key.Backspace, Weva = weva_key.WEVA_KEY_BACKSPACE },
            new EngineKey { Unity = Key.Delete, Weva = weva_key.WEVA_KEY_DELETE },
            new EngineKey { Unity = Key.LeftArrow, Weva = weva_key.WEVA_KEY_LEFT },
            new EngineKey { Unity = Key.RightArrow, Weva = weva_key.WEVA_KEY_RIGHT },
            new EngineKey { Unity = Key.UpArrow, Weva = weva_key.WEVA_KEY_UP },
            new EngineKey { Unity = Key.DownArrow, Weva = weva_key.WEVA_KEY_DOWN },
            new EngineKey { Unity = Key.Home, Weva = weva_key.WEVA_KEY_HOME },
            new EngineKey { Unity = Key.End, Weva = weva_key.WEVA_KEY_END },
            new EngineKey { Unity = Key.PageUp, Weva = weva_key.WEVA_KEY_PAGE_UP },
            new EngineKey { Unity = Key.PageDown, Weva = weva_key.WEVA_KEY_PAGE_DOWN },
        };

        private readonly NativeDocument _doc;
        private readonly List<char> _typed = new List<char>();
        private Keyboard _subscribed;
        private uint _buttons;
        private Vector2 _pointer = new Vector2(float.NaN, float.NaN);

        /// <summary>Pixels per wheel notch, what a browser scrolls per line.</summary>
        public float WheelLine = 40f;
        /// <summary>Whether the last fed input was consumed by the document (a host keeps it from gameplay then).</summary>
        public bool Consumed { get; private set; }

        public NativeInputFeed(NativeDocument doc)
        {
            _doc = doc;
        }

        public void Dispose()
        {
            if (_subscribed != null) _subscribed.onTextInput -= OnTextInput;
            _subscribed = null;
        }

        private void OnTextInput(char c)
        {
            if (c >= ' ' && c != (char)127) _typed.Add(c);
        }

        private static uint Modifiers(Keyboard keyboard)
        {
            uint m = 0;
            if (keyboard.shiftKey.isPressed) m |= (uint)weva_key_modifier.WEVA_MOD_SHIFT;
            if (keyboard.ctrlKey.isPressed) m |= (uint)weva_key_modifier.WEVA_MOD_CTRL;
            if (keyboard.altKey.isPressed) m |= (uint)weva_key_modifier.WEVA_MOD_ALT;
            if (keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed) m |= (uint)weva_key_modifier.WEVA_MOD_META;
            return m;
        }

        /// <summary>
        /// Reads this frame's input and feeds it. viewportHeight flips the
        /// pointer's y; call once per frame before the document's update.
        /// </summary>
        public void Tick(int viewportHeight)
        {
            Consumed = false;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (keyboard != null && !ReferenceEquals(_subscribed, keyboard))
            {
                if (_subscribed != null) _subscribed.onTextInput -= OnTextInput;
                _subscribed = keyboard;
                keyboard.onTextInput += OnTextInput;
            }
            uint modifiers = keyboard != null ? Modifiers(keyboard) : 0;

            if (mouse != null)
            {
                Vector2 at = mouse.position.ReadValue();
                var p = new Vector2(at.x, viewportHeight - at.y);
                uint buttons = 0;
                if (mouse.leftButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY;
                if (mouse.rightButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_SECONDARY;
                if (mouse.middleButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_MIDDLE;
                Vector2 wheel = mouse.scroll.ReadValue();
                if (wheel != Vector2.zero)
                {
                    // Input System reports notches (sign: up is positive); a
                    // browser scrolls 40px per notch, down being positive.
                    float dx = wheel.x / 120f;
                    float dy = -wheel.y / 120f;
                    if (Mathf.Abs(wheel.y) <= 1f && Mathf.Abs(wheel.x) <= 1f) { dx = wheel.x; dy = -wheel.y; }
                    if (_doc.Scroll(p.x, p.y, dx * WheelLine, dy * WheelLine)) Consumed = true;
                }
                if (p != _pointer || buttons != _buttons)
                {
                    if (buttons != _buttons && buttons != 0 && _doc.AcceptsPointer(p.x, p.y)) Consumed = true;
                    _doc.SetPointer(p.x, p.y, buttons, modifiers);
                    _pointer = p;
                    _buttons = buttons;
                }
            }

            if (keyboard == null) return;
            bool shortcut = keyboard.ctrlKey.isPressed && !keyboard.altKey.isPressed;
            bool spaceConsumed = false;
            foreach (EngineKey k in EngineKeys)
            {
                KeyControl control = keyboard[k.Unity];
                if (control.wasPressedThisFrame)
                {
                    bool consumed;
                    if (k.Weva == weva_key.WEVA_KEY_TAB && !keyboard.ctrlKey.isPressed)
                    {
                        consumed = _doc.FocusStep(keyboard.shiftKey.isPressed, true) != WevaNative.WEVA_ELEMENT_NONE;
                    }
                    else
                    {
                        consumed = _doc.Key(k.Weva, true, modifiers);
                    }
                    Consumed |= consumed;
                    if (k.Weva == weva_key.WEVA_KEY_SPACE && consumed) spaceConsumed = true;
                }
                if (control.wasReleasedThisFrame) Consumed |= _doc.Key(k.Weva, false, modifiers);
            }
            // The text a key produced is a separate thing from the key, and a
            // key the document took as a key (Space on a button) must not also
            // arrive as text; the Godot host applies the same rule.
            if (spaceConsumed) _typed.RemoveAll(c => c == ' ');
            if (shortcut)
            {
                // The editing shortcuts a browser has, routed the way the Godot
                // host routes them; the characters they produce are not text.
                if (keyboard.aKey.wasPressedThisFrame) Consumed |= _doc.SelectAll();
                if (keyboard.cKey.wasPressedThisFrame || keyboard.xKey.wasPressedThisFrame)
                {
                    string selected = _doc.SelectedText();
                    if (selected.Length > 0)
                    {
                        GUIUtility.systemCopyBuffer = selected;
                        if (keyboard.xKey.wasPressedThisFrame)
                        {
                            _doc.Key(weva_key.WEVA_KEY_BACKSPACE, true, 0);
                            _doc.Key(weva_key.WEVA_KEY_BACKSPACE, false, 0);
                        }
                        Consumed = true;
                    }
                }
                if (keyboard.vKey.wasPressedThisFrame) Consumed |= _doc.PasteText(GUIUtility.systemCopyBuffer ?? string.Empty);
                if (keyboard.zKey.wasPressedThisFrame) Consumed |= keyboard.shiftKey.isPressed ? _doc.Redo() : _doc.Undo();
                if (keyboard.yKey.wasPressedThisFrame) Consumed |= _doc.Redo();
                _typed.Clear();
                return;
            }
            if (_typed.Count > 0)
            {
                foreach (char c in _typed)
                {
                    // A space on a button is the button's key, not text; the core
                    // rejects text a control does not take, which is the answer.
                    Consumed |= _doc.TryTextInput(c.ToString(), modifiers);
                }
                _typed.Clear();
            }
        }
    }
}
#endif
