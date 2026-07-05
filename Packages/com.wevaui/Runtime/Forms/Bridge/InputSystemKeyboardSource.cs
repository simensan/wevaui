#if WEVA_INPUTSYSTEM
using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using Weva.Events;
using Weva.Forms;

namespace Weva.Forms.Bridge {
    // Bridges the new Input System keyboard text + key events into the
    // EventDispatcher.
    //
    // - Keyboard.current.onTextInput fires per-character once per typed code
    //   point, after the OS resolved IME composition. We forward that as a
    //   text-input event via DispatchTextInput.
    // - Keyboard.current key controls (anyKey events) are handled by reading
    //   the Keyboard's keys array each frame in Tick() — onAnyButtonPress is
    //   not granular enough to recover the Key/Code we need.
    //
    // This file is wrapped in WEVA_INPUTSYSTEM (asmdef versionDefine).
    public sealed class InputSystemKeyboardSource : IDisposable {
        // Resolved PER EVENT, not captured (in-editor find, the focused=NULL
        // mystery): WevaDocument rebuilds its pipeline after OnEnable
        // (DelayedRebuild, hot reload, document swaps), which replaces
        // doc.Events with a NEW EventDispatcher. A source constructed with a
        // fixed reference kept delivering text to the DEAD dispatcher — whose
        // FocusedElement is forever null — while clicks focused fields on the
        // live one. The pointer path never had this bug because
        // UnityPointerSource.Tick receives the current dispatcher each frame.
        readonly Func<EventDispatcher> dispatcherProvider;
        EventDispatcher Dispatcher => dispatcherProvider();
        Action<char> textInputDelegate;
        bool subscribed;
        // The device the onTextInput subscription actually lives on. Needed
        // both to unsubscribe from the RIGHT device (Keyboard.current may
        // have changed since Enable) and to detect device swaps in Tick.
        Keyboard subscribedKeyboard;

        public InputSystemKeyboardSource(EventDispatcher dispatcher) {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            dispatcherProvider = () => dispatcher;
        }

        // Live-dispatcher variant — hosts whose pipeline can be rebuilt
        // (WevaDocument) pass a resolver so keystrokes always land on the
        // dispatcher the pointer/focus path is using.
        public InputSystemKeyboardSource(Func<EventDispatcher> dispatcherProvider) {
            this.dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));
        }

        public void Enable() {
            var kb = Keyboard.current;
            if (kb == null) return; // no device yet — Tick retries (see below)
            if (subscribed && ReferenceEquals(kb, subscribedKeyboard)) return;
            if (subscribed) Disable(); // device swapped — move the subscription
            textInputDelegate ??= OnTextInput;
            kb.onTextInput += textInputDelegate;
            subscribedKeyboard = kb;
            subscribed = true;
        }

        public void Disable() {
            if (!subscribed) return;
            if (subscribedKeyboard != null && textInputDelegate != null) {
                subscribedKeyboard.onTextInput -= textInputDelegate;
            }
            subscribedKeyboard = null;
            subscribed = false;
        }

        public void Tick() => Tick(0);

        public void Tick(double nowSeconds) {
            var kb = Keyboard.current;
            if (kb == null) return;
            // Late-subscribe (in-editor find, audit-validation §7-10 "can't
            // type"): Input System devices attach asynchronously, so
            // Keyboard.current is often still null during OnEnable on play
            // entry. The old Enable() bailed silently and NOTHING retried —
            // Tick's per-frame key polling kept arrows/backspace working,
            // but the onTextInput subscription (the only source of character
            // input) never happened, so text fields focused and moved the
            // caret yet typed nothing, all session. Also re-arms when the
            // active keyboard device changes (the subscription lives on the
            // device instance, not on the static current).
            if (!subscribed || !ReferenceEquals(kb, subscribedKeyboard)) Enable();
            var mods = ReadModifiers(kb);

            // Control keys we want to forward as KeyDown. wasPressedThisFrame
            // fires once on the activation edge; the KeyAutoRepeat clock adds
            // Chrome's hold cadence (~500ms then ~30Hz) for the editing keys
            // (input/selection audit #2 — holding Backspace deleted exactly
            // one character while held letters repeated via onTextInput).
            ForwardKeyEdge(kb.leftArrowKey,  "ArrowLeft",  "ArrowLeft",  mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.rightArrowKey, "ArrowRight", "ArrowRight", mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.upArrowKey,    "ArrowUp",    "ArrowUp",    mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.downArrowKey,  "ArrowDown",  "ArrowDown",  mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.homeKey,       "Home",       "Home",       mods, nowSeconds, repeats: false);
            ForwardKeyEdge(kb.endKey,        "End",        "End",        mods, nowSeconds, repeats: false);
            ForwardKeyEdge(kb.backspaceKey,  "Backspace",  "Backspace",  mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.deleteKey,     "Delete",     "Delete",     mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.pageUpKey,     "PageUp",     "PageUp",     mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.pageDownKey,   "PageDown",   "PageDown",   mods, nowSeconds, repeats: true);
            ForwardKeyEdge(kb.enterKey,      "Enter",      "Enter",      mods, nowSeconds, repeats: false);
            ForwardKeyEdge(kb.numpadEnterKey,"Enter",      "NumpadEnter",mods, nowSeconds, repeats: false);
            ForwardKeyEdge(kb.tabKey,        "Tab",        "Tab",        mods, nowSeconds, repeats: false);
            ForwardKeyEdge(kb.escapeKey,     "Escape",     "Escape",     mods, nowSeconds, repeats: false);
            // Ctrl-chorded editing shortcuts (input/selection audit #5).
            // With Ctrl held, onTextInput delivers control characters that
            // the filter drops (0x01 for Ctrl+A…), so letter chords have NO
            // delivery path unless forwarded as KeyDowns here. Only while
            // Ctrl/Meta is held — plain letters stay on the text-input path
            // (forwarding them always would double-insert).
            if ((mods & (KeyModifiers.Ctrl | KeyModifiers.Meta)) != 0) {
                ForwardKeyEdge(kb.aKey, "a", "KeyA", mods, nowSeconds, repeats: false);
                ForwardKeyEdge(kb.cKey, "c", "KeyC", mods, nowSeconds, repeats: false);
                ForwardKeyEdge(kb.xKey, "x", "KeyX", mods, nowSeconds, repeats: false);
                ForwardKeyEdge(kb.vKey, "v", "KeyV", mods, nowSeconds, repeats: true); // held paste repeats in Chrome
                ForwardKeyEdge(kb.zKey, "z", "KeyZ", mods, nowSeconds, repeats: true); // held undo repeats in Chrome
                ForwardKeyEdge(kb.yKey, "y", "KeyY", mods, nowSeconds, repeats: true);
            }
        }

        readonly KeyAutoRepeat autoRepeat = new KeyAutoRepeat();

        void ForwardKeyEdge(KeyControl key, string keyName, string codeName, KeyModifiers mods,
                            double nowSeconds, bool repeats) {
            if (key == null) return;
            var d = Dispatcher;
            if (d == null) return;
            if (key.wasPressedThisFrame) {
                d.DispatchKeyDown(keyName, codeName, mods, repeat: false);
                if (repeats) autoRepeat.Press(codeName, nowSeconds);
            } else if (repeats && key.isPressed) {
                int n = autoRepeat.Repeats(codeName, nowSeconds);
                for (int i = 0; i < n; i++) {
                    d.DispatchKeyDown(keyName, codeName, mods, repeat: true);
                }
            }
            if (key.wasReleasedThisFrame) {
                if (repeats) autoRepeat.Release(codeName);
                d.DispatchKeyUp(keyName, codeName, mods, repeat: false);
            }
        }

        static KeyModifiers ReadModifiers(Keyboard kb) {
            var m = KeyModifiers.None;
            if (kb.shiftKey != null && kb.shiftKey.isPressed) m |= KeyModifiers.Shift;
            if (kb.ctrlKey  != null && kb.ctrlKey.isPressed)  m |= KeyModifiers.Ctrl;
            if (kb.altKey   != null && kb.altKey.isPressed)   m |= KeyModifiers.Alt;
            // The new Input System exposes the platform meta key as 'leftMeta'/'rightMeta'.
            if ((kb.leftMetaKey != null && kb.leftMetaKey.isPressed) ||
                (kb.rightMetaKey != null && kb.rightMetaKey.isPressed)) m |= KeyModifiers.Meta;
            return m;
        }

        void OnTextInput(char ch) {
            var d = Dispatcher;
            if (d == null) return;
            // onTextInput delivers post-IME characters and also includes control
            // chars (\b, \r) on some platforms — filter those out so they don't
            // become real text in the TextEditModel.
            if (ch == '\b' || ch == '') return;
            if (ch == '\r') ch = '\n';
            if (ch < 0x20 && ch != '\n' && ch != '\t') return;
            var kb = Keyboard.current;
            var mods = kb != null ? ReadModifiers(kb) : KeyModifiers.None;
            d.DispatchTextInput(ch.ToString(), mods);
        }

        public void Dispose() {
            Disable();
        }
    }
}
#endif
