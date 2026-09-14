// Feeds Unity input to a core-hosted document the way the Godot host's
// _gui_input does: pointer position and buttons as one state, the wheel as a
// scroll at a point, engine keys as edges, everything a key produces as text,
// and the clipboard and undo shortcuts a browser has. Reads the Input System
// (the package's dependency); a game that routes input itself calls the
// document's own SetPointer/Key/TryTextInput instead of enabling this.
//
// What the host is responsible for and what the core is: the Input System
// reports edges only, so key auto-repeat and double-click detection are the
// core's (ABI minor 39, opted into in the constructor) and are tested there.
// Everything that needs the platform stays here -- which key is the command
// key, whether the application has focus, where the surface ends, what a
// finger is doing -- and each of those is pinned by NativeInputFeedTests.
//
// Coordinates: Unity's pointer has its origin at the bottom-left of the
// screen, the document at its top-left, so y is flipped by the viewport
// height the document was laid out at.
#if WEVA_INPUTSYSTEM
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Weva.Native
{
    internal sealed class NativeInputFeed : IDisposable
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

        /// <summary>The browser's key auto-repeat: first repeat after this many seconds.</summary>
        public const double KeyRepeatDelay = 0.5;
        /// <summary>...and then one every this many seconds.</summary>
        public const double KeyRepeatInterval = 0.03;
        /// <summary>Two primary presses within this window and distance are a double click.</summary>
        public const double DoubleClickWindow = 0.5;
        public const double DoubleClickDistance = 4;
        /// <summary>A finger that moves this far is panning, not tapping.</summary>
        public const float TouchPanThreshold = 8f;

        private readonly NativeDocument _doc;
        private readonly List<char> _typed = new List<char>();
        private Keyboard _subscribed;
        private uint _buttons;
        private Vector2 _pointer = new Vector2(float.NaN, float.NaN);
        private bool _pointerCleared = true;
        private bool _touchDown, _touchPanning, _touchCovered;
        private Vector2 _touchStart, _touchLast;
        // Gamepad: a held direction repeats after the delay, then at the interval,
        // the way Godot's repeat_navigation steps a held pad through a list.
        private int _heldDirection = -1;
        private double _repeatAt;
        // IME: the composition string the OS is showing, and the text it delivered
        // while one was open, which becomes the commit rather than typed text.
        private string _composition = "";
        private readonly System.Text.StringBuilder _imeCommit = new System.Text.StringBuilder();
        private bool _imeEnabled;
        private uint _imeTarget = WevaNative.WEVA_ELEMENT_NONE;
        private Vector2 _imeCursor = new Vector2(float.NaN, float.NaN);

        /// <summary>The pad's repeat: first repeat after this many seconds, then every interval.</summary>
        public const double NavigationRepeatDelay = 0.4;
        public const double NavigationRepeatInterval = 0.1;
        /// <summary>Stick deflection that counts as a direction.</summary>
        public const float StickThreshold = 0.5f;
        /// <summary>Seconds, monotonic, for the pad's repeat. Unscaled time by default; a test replaces it.</summary>
        public Func<double> Clock = () => Time.unscaledTimeAsDouble;
        /// <summary>
        /// Whether the pad's accept on a focused text field asks the host for a
        /// keyboard (<see cref="TextEntryRequested"/>) instead of pressing Enter.
        /// The Godot host's gamepad_text_entry does the same.
        /// </summary>
        public bool GamepadTextEntry;
        /// <summary>A pad pressed accept on a text field: the field's id. The host supplies the keyboard.</summary>
        public event Action<string> TextEntryRequested;

        /// <summary>
        /// Pixels per wheel notch. Chrome on Windows scrolls 100 CSS px for one
        /// notch (WHEEL_DELTA 120), and Chrome is the reference; both hosts used
        /// 40 -- a shared deviation, changed together on 2026-09-13.
        /// </summary>
        public float WheelLine = 100f;
        /// <summary>Whether the last fed input was consumed by the document (a host keeps it from gameplay then).</summary>
        public bool Consumed { get; private set; }
        /// <summary>
        /// Whether keys and text reach the document. A host that has moved its
        /// own focus elsewhere sets this false; the Godot host gets the same
        /// gate from Control focus. Pointer input is unaffected.
        /// </summary>
        public bool AcceptsKeyboard = true;
        /// <summary>
        /// Whether the command chord is the meta key (macOS) rather than Ctrl.
        /// Decided from the platform; a test flips it.
        /// </summary>
        public bool CommandIsMeta =
            Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
        /// <summary>
        /// Whether Tab at the document's edge wraps to its other end. False lets
        /// the host continue its own focus chain: <see cref="TabbedOut"/> fires
        /// with the direction and the document keeps its focus until the host
        /// moves it. The Godot host does the same through find_next_valid_focus.
        /// </summary>
        public bool WrapTab = true;
        /// <summary>Tab left the document (only when <see cref="WrapTab"/> is false). true = backwards.</summary>
        public event Action<bool> TabbedOut;
        /// <summary>
        /// Whether the application has the OS's focus. A lost application
        /// clears the pointer and takes no keys, as a browser tab in the
        /// background does. Application.isFocused by default; a test replaces it.
        /// </summary>
        public Func<bool> HasFocus = () => Application.isFocused;

        // Every live feed, in creation order: documents share the screen, and
        // where one that paints later (a higher Order, or the same Order and
        // a later registration -- the paint order) accepts the pointer, the
        // one beneath treats the pointer as outside, the way a browser gives
        // a click to the topmost hit. Only pointers are arbitrated: keys go
        // to whichever document has focus.
        private static readonly List<NativeInputFeed> s_feeds = new List<NativeInputFeed>();

        /// <summary>The document's paint order (WevaDocument.SortingOrder): a higher one covers a lower one where it accepts the pointer.</summary>
        public Func<int> Order = () => 0;

        public NativeInputFeed(NativeDocument doc)
        {
            _doc = doc;
            // The Input System reports edges; a browser repeats a held key and
            // reads two quick presses as a double click. The core owns both
            // once told to (ABI minor 39).
            _doc.SetKeyRepeat(KeyRepeatDelay, KeyRepeatInterval);
            _doc.SetDoubleClick(DoubleClickWindow, DoubleClickDistance);
            s_feeds.Add(this);
        }

        /// <summary>Whether a document painted over this one accepts the pointer at a document-space point.</summary>
        public bool CoveredAt(float x, float y)
        {
            int mine = Order();
            int index = s_feeds.IndexOf(this);
            for (int i = 0; i < s_feeds.Count; i++)
            {
                NativeInputFeed other = s_feeds[i];
                if (other == this || other._doc == null || !other._doc.IsAlive) continue;
                int theirs = other.Order();
                bool above = theirs > mine || (theirs == mine && i > index);
                if (above && other._doc.AcceptsPointer(x, y)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            s_feeds.Remove(this);
            if (_subscribed != null)
            {
                _subscribed.onTextInput -= OnTextInput;
                _subscribed.onIMECompositionChange -= OnImeComposition;
                if (_imeEnabled) _subscribed.SetIMEEnabled(false);
            }
            _subscribed = null;
        }

        private void OnTextInput(char c)
        {
            if (c < ' ' || c == (char)127) return;
            // Text the OS delivers while an IME composition is open, or just as
            // it closes, is the composition's commit, not typing: it replaces
            // the composed text when the composition string empties. The Godot
            // host buffers it the same way (ime_commit_text_).
            if (_composition.Length > 0) _imeCommit.Append(c);
            else _typed.Add(c);
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

        /// <summary>Kept for callers that only know the height; the surface is taken to be unbounded in x.</summary>
        public void Tick(int viewportHeight) => Tick(int.MaxValue, viewportHeight);

        /// <summary>
        /// Reads this frame's input and feeds it. The viewport is the size the
        /// document was laid out at: the height flips the pointer's y, and a
        /// pointer outside either bound has left the surface. Call once per
        /// frame before the document's update.
        /// </summary>
        public void Tick(int viewportWidth, int viewportHeight)
        {
            Consumed = false;
            // Whatever moved since the last update is where the pointer will
            // land: hit tests need this frame's geometry, not last frame's.
            _doc.UpdateGeometry();

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            Touchscreen touch = Touchscreen.current;
            bool focused = HasFocus == null || HasFocus();
            if (keyboard != null && !ReferenceEquals(_subscribed, keyboard))
            {
                if (_subscribed != null)
                {
                    _subscribed.onTextInput -= OnTextInput;
                    _subscribed.onIMECompositionChange -= OnImeComposition;
                }
                _subscribed = keyboard;
                keyboard.onTextInput += OnTextInput;
                keyboard.onIMECompositionChange += OnImeComposition;
            }
            uint modifiers = keyboard != null ? Modifiers(keyboard) : 0;
            if (keyboard != null) SyncIme(keyboard, viewportHeight);

            if (touch != null && FeedTouch(touch, viewportWidth, viewportHeight, modifiers))
            {
                // A finger owns the pointer while it is down; the mouse resumes after.
            }
            else if (mouse != null)
            {
                Vector2 at = mouse.position.ReadValue();
                var p = new Vector2(at.x, viewportHeight - at.y);
                // Covered by a document painted over this one: outside, as far
                // as this document is concerned (a drag that started here
                // keeps the pointer, as one that leaves the surface does).
                bool inside = focused && p.x >= 0 && p.y >= 0 && p.x < viewportWidth && p.y < viewportHeight && !CoveredAt(p.x, p.y);
                uint buttons = 0;
                if (mouse.leftButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY;
                if (mouse.rightButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_SECONDARY;
                if (mouse.middleButton.isPressed) buttons |= (uint)weva_pointer_button.WEVA_BUTTON_MIDDLE;
                // A drag that leaves the surface keeps the pointer: the button
                // is still down on whatever it pressed. Only an unpressed
                // pointer outside, or a lost application, clears hover.
                if (!inside && _buttons == 0)
                {
                    if (!_pointerCleared)
                    {
                        _doc.ClearPointer();
                        _pointerCleared = true;
                        _pointer = new Vector2(float.NaN, float.NaN);
                        _buttons = 0;
                    }
                }
                else
                {
                    Vector2 wheel = mouse.scroll.ReadValue();
                    if (wheel != Vector2.zero)
                    {
                        // Input System reports notches (sign: up is positive); a
                        // browser scrolls WheelLine px per notch, down being positive.
                        float dx = wheel.x / 120f;
                        float dy = -wheel.y / 120f;
                        if (Mathf.Abs(wheel.y) <= 1f && Mathf.Abs(wheel.x) <= 1f) { dx = wheel.x; dy = -wheel.y; }
                        if (_doc.Scroll(p.x, p.y, dx * WheelLine, dy * WheelLine)) Consumed = true;
                    }
                    if (p != _pointer || buttons != _buttons || _pointerCleared)
                    {
                        if (buttons != _buttons && buttons != 0 && _doc.AcceptsPointer(p.x, p.y)) Consumed = true;
                        _doc.SetPointer(p.x, p.y, buttons, modifiers);
                        _pointer = p;
                        _buttons = buttons;
                        _pointerCleared = false;
                    }
                }
            }

            if (focused && AcceptsKeyboard) FeedGamepad(Gamepad.current);
            else _heldDirection = -1;

            if (keyboard == null || !AcceptsKeyboard || !focused)
            {
                _typed.Clear();
                return;
            }
            bool command = CommandIsMeta
                ? (keyboard.leftMetaKey.isPressed || keyboard.rightMetaKey.isPressed)
                : keyboard.ctrlKey.isPressed;
            // AltGr is Ctrl+Alt on Windows and must still produce text; the
            // Godot host applies the same exclusion.
            bool shortcut = command && !keyboard.altKey.isPressed;
            bool spaceConsumed = false;
            foreach (EngineKey k in EngineKeys)
            {
                KeyControl control = keyboard[k.Unity];
                if (control.wasPressedThisFrame)
                {
                    bool consumed;
                    if (k.Weva == weva_key.WEVA_KEY_TAB && !keyboard.ctrlKey.isPressed)
                    {
                        bool backwards = keyboard.shiftKey.isPressed;
                        consumed = _doc.FocusStep(backwards, WrapTab) != WevaNative.WEVA_ELEMENT_NONE;
                        if (!consumed && !WrapTab) TabbedOut?.Invoke(backwards);
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

        // ---- IME ------------------------------------------------------------
        //
        // The Input System reports the OS composition string as it changes and
        // delivers the committed text through onTextInput once it closes. The
        // core wants the composition shown in the field while it is open
        // (SetComposition) and replaced by the commit when it closes; text that
        // arrives in between is that commit, not typing. Mirrors the Godot
        // host's receive_ime_update / flush_ime_commit.
        private void OnImeComposition(UnityEngine.InputSystem.LowLevel.IMECompositionString composition)
        {
            string text = composition.ToString();
            if (text.Length > 0)
            {
                _composition = text;
                if (!_doc.SetComposition(text, text.Length, text.Length))
                {
                    // Nothing composable is focused: the OS is composing into
                    // the void. Drop it so the eventual commit types instead.
                    _composition = "";
                }
                return;
            }
            if (_composition.Length == 0) return;
            _composition = "";
            _doc.CommitComposition(_imeCommit.ToString());
            _imeCommit.Clear();
        }

        // Tells the OS whether an IME may open and where its window goes: over
        // a focused text control, at the caret's bottom-left. Only on change.
        private void SyncIme(Keyboard keyboard, int viewportHeight)
        {
            uint target = _doc.TextInputTarget;
            bool want = target != WevaNative.WEVA_ELEMENT_NONE;
            if (want != _imeEnabled || target != _imeTarget)
            {
                if (!want && _composition.Length > 0)
                {
                    // The field went away under an open composition: commit what
                    // was delivered, as Godot's close_ime does.
                    _composition = "";
                    _doc.CommitComposition(_imeCommit.ToString());
                    _imeCommit.Clear();
                }
                keyboard.SetIMEEnabled(want);
                _imeEnabled = want;
                _imeTarget = target;
                _imeCursor = new Vector2(float.NaN, float.NaN);
            }
            if (!want) return;
            if (_doc.TryGetCaretBounds(out NativeBounds caret))
            {
                var at = new Vector2((float)caret.X, (float)(viewportHeight - (caret.Y + caret.Height)));
                if (at != _imeCursor)
                {
                    keyboard.SetIMECursorPosition(at);
                    _imeCursor = at;
                }
            }
        }

        // ---- gamepad ---------------------------------------------------------
        //
        // The Godot host's navigation_action, less the InputMap: the d-pad and
        // left stick move focus by geometry, South accepts, East cancels, the
        // shoulders step the tab order. A direction on a control that owns the
        // axis -- Left/Right on any focused control's caret or value, Up/Down on
        // a select, textarea or number field -- is that control's key first
        // and a focus move only when the control did not take it.
        private static readonly (weva_key key, double dx, double dy)[] Directions =
        {
            (weva_key.WEVA_KEY_LEFT, -1, 0), (weva_key.WEVA_KEY_RIGHT, 1, 0),
            (weva_key.WEVA_KEY_UP, 0, -1), (weva_key.WEVA_KEY_DOWN, 0, 1),
        };

        private static bool DirectionHeld(Gamepad pad, int i)
        {
            Vector2 stick = pad.leftStick.ReadValue();
            switch (i)
            {
                case 0: return pad.dpad.left.isPressed || stick.x <= -StickThreshold;
                case 1: return pad.dpad.right.isPressed || stick.x >= StickThreshold;
                case 2: return pad.dpad.up.isPressed || stick.y >= StickThreshold;
                default: return pad.dpad.down.isPressed || stick.y <= -StickThreshold;
            }
        }

        private string FocusedTag(out string type)
        {
            type = "";
            uint focused = _doc.Focus;
            if (focused == WevaNative.WEVA_ELEMENT_NONE) return "";
            string tag = _doc.TagName(focused) ?? "";
            if (tag == "input") type = _doc.ElementAttribute(focused, "type") ?? "";
            return tag;
        }

        private bool Tap(weva_key key)
        {
            bool down = _doc.Key(key, true, 0);
            bool up = _doc.Key(key, false, 0);
            return down || up;
        }

        private void NavigateDirection(int i)
        {
            (weva_key key, double dx, double dy) = Directions[i];
            string tag = FocusedTag(out string type);
            bool vertical = dy != 0;
            bool elementFirst = !vertical || tag == "select" || tag == "textarea" || (tag == "input" && type == "number");
            if (elementFirst && Tap(key)) { Consumed = true; return; }
            _doc.FocusMove(dx, dy);
            Consumed = true;
        }

        private void FeedGamepad(Gamepad pad)
        {
            if (pad == null) { _heldDirection = -1; return; }
            if (pad.buttonSouth.wasPressedThisFrame)
            {
                string tag = FocusedTag(out string type);
                bool textField = tag == "textarea" || (tag == "input" && (type == "" || type == "text" || type == "search" ||
                    type == "password" || type == "email" || type == "url" || type == "tel" || type == "number"));
                if (textField && GamepadTextEntry) TextEntryRequested?.Invoke(_doc.ElementId(_doc.Focus) ?? "");
                else if (textField) Tap(weva_key.WEVA_KEY_ENTER);
                else if (!Tap(weva_key.WEVA_KEY_SPACE)) Tap(weva_key.WEVA_KEY_ENTER);
                Consumed = true;
            }
            if (pad.buttonEast.wasPressedThisFrame) Consumed |= Tap(weva_key.WEVA_KEY_ESCAPE);
            if (pad.rightShoulder.wasPressedThisFrame) { _doc.FocusStep(false, true); Consumed = true; }
            if (pad.leftShoulder.wasPressedThisFrame) { _doc.FocusStep(true, true); Consumed = true; }

            double now = Clock();
            if (_heldDirection >= 0 && !DirectionHeld(pad, _heldDirection)) _heldDirection = -1;
            if (_heldDirection < 0)
            {
                for (int i = 0; i < Directions.Length; i++)
                {
                    if (!DirectionHeld(pad, i)) continue;
                    NavigateDirection(i);
                    _heldDirection = i;
                    _repeatAt = now + NavigationRepeatDelay;
                    break;
                }
            }
            else if (now >= _repeatAt)
            {
                _repeatAt = now + NavigationRepeatInterval;
                NavigateDirection(_heldDirection);
            }
        }

        // A finger is a pointer until it moves: a tap presses and releases where
        // it landed, a drag pans the content under it -- negated, because the
        // content follows the finger -- and never becomes a press. Returns true
        // while the finger owns the pointer. Mirrors the Godot host's
        // InputEventScreenDrag path.
        private bool FeedTouch(Touchscreen touch, int viewportWidth, int viewportHeight, uint modifiers)
        {
            TouchControl primary = touch.primaryTouch;
            bool down = primary.press.isPressed;
            Vector2 raw = primary.position.ReadValue();
            var p = new Vector2(raw.x, viewportHeight - raw.y);
            if (down && !_touchDown)
            {
                _touchDown = true;
                _touchPanning = false;
                _touchStart = _touchLast = p;
                // A finger that landed on a document painted over this one is
                // that document's for as long as it is down.
                _touchCovered = CoveredAt(p.x, p.y);
                if (_touchCovered) return true;
                _doc.SetPointer(p.x, p.y, 0, modifiers);
                _pointerCleared = false;
                _pointer = p;
                return true;
            }
            if (down)
            {
                if (_touchCovered) return true;
                Vector2 delta = p - _touchLast;
                _touchLast = p;
                if (!_touchPanning && (p - _touchStart).sqrMagnitude >= TouchPanThreshold * TouchPanThreshold)
                    _touchPanning = true;
                if (_touchPanning)
                {
                    // Scrolled at the point the finger landed, not where it is
                    // now: a pan is captured by the element under touchstart, so
                    // a finger that has dragged out of a 100px list still moves
                    // that list, as it does in a browser.
                    if (delta != Vector2.zero && _doc.Scroll(_touchStart.x, _touchStart.y, -delta.x, -delta.y)) Consumed = true;
                }
                return true;
            }
            if (_touchDown)
            {
                _touchDown = false;
                if (_touchCovered)
                {
                    _touchCovered = false;
                    return true;
                }
                if (!_touchPanning)
                {
                    // A tap: press and release where the finger landed, this frame.
                    if (_doc.AcceptsPointer(_touchStart.x, _touchStart.y)) Consumed = true;
                    _doc.SetPointer(_touchStart.x, _touchStart.y, (uint)weva_pointer_button.WEVA_BUTTON_PRIMARY, modifiers);
                    _doc.SetPointer(_touchStart.x, _touchStart.y, 0, modifiers);
                }
                // The finger is gone: nothing is hovered, unlike a mouse that stays.
                _doc.ClearPointer();
                _pointerCleared = true;
                _pointer = new Vector2(float.NaN, float.NaN);
                _buttons = 0;
                return true;
            }
            return false;
        }
    }
}
#endif
