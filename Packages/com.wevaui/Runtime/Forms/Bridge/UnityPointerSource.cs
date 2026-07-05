using Weva.Events;
#if WEVA_INPUTSYSTEM
using UnityEngine;
using UnityEngine.InputSystem;
#endif

namespace Weva.Forms.Bridge {
    // Per-frame pump that translates a backing pointer device into
    // EventDispatcher calls. The interface is engine-agnostic so headless
    // tests can drop in a FakePointerSource that synthesises scripted
    // positions; the UnityPointerSource implementation reads
    // UnityEngine.InputSystem.Mouse.current (with Pointer.current as a
    // touch/pen fallback).
    //
    // Coordinate space: dispatchers expect CSS top-left coordinates, but the
    // Input System reports screen-space with origin at bottom-left. The
    // Y-flip happens here, at the bridge boundary; everything downstream
    // stays in CSS space.
    public interface IUIPointerSource {
        void Tick(EventDispatcher dispatcher, double currentTimeSeconds);
    }

#if WEVA_INPUTSYSTEM
    public sealed class UnityPointerSource : IUIPointerSource {
        Vector2 lastPosition;
        bool hasLastPosition;
        bool lastLeft;
        bool lastRight;
        bool lastMiddle;
        bool lastOnScreen;

        public void Tick(EventDispatcher dispatcher, double currentTimeSeconds) {
            if (dispatcher == null) return;

            Vector2 rawPos;
            bool leftDown, rightDown, middleDown;
            if (!ReadPointer(out rawPos, out leftDown, out rightDown, out middleDown)) return;

            float screenH = ScreenHeight();
            // Y-flip: Input System reports bottom-left origin; the layout +
            // hit-tester pipeline runs in CSS top-left. Convert once here so
            // every consumer downstream stays in CSS space.
            double cssX = rawPos.x;
            double cssY = screenH - rawPos.y;

            var mods = ReadModifiers();

            bool onScreen = rawPos.x >= 0f && rawPos.x <= ScreenWidth()
                            && rawPos.y >= 0f && rawPos.y <= screenH;

            if (!hasLastPosition) {
                lastPosition = rawPos;
                hasLastPosition = true;
                lastOnScreen = onScreen;
                if (onScreen) dispatcher.DispatchPointerMove(cssX, cssY, mods);
            } else if (rawPos != lastPosition) {
                if (onScreen) {
                    dispatcher.DispatchPointerMove(cssX, cssY, mods);
                } else if (lastOnScreen) {
                    // Pointer just left the window; route a move to a
                    // guaranteed-empty coordinate so the dispatcher's hover
                    // chain unwinds and emits PointerLeave for the previously
                    // hovered element.
                    dispatcher.DispatchPointerMove(-1, -1, mods);
                }
                lastPosition = rawPos;
                lastOnScreen = onScreen;
            }

            HandleButton(dispatcher, ref lastLeft, leftDown, 0, cssX, cssY, mods);
            HandleButton(dispatcher, ref lastRight, rightDown, 2, cssX, cssY, mods);
            HandleButton(dispatcher, ref lastMiddle, middleDown, 1, cssX, cssY, mods);

            // Wheel: Input System reports raw scroll delta. Devices differ:
            // notch wheels often report small line-like values while high-
            // resolution wheels/trackpads report larger pixel-like deltas.
            // Classify tiny deltas as lines so one wheel notch maps to the
            // same comfortable line step as browsers.
            if (Mouse.current != null) {
                Vector2 scroll = Mouse.current.scroll.ReadValue();
                if (scroll.x != 0f || scroll.y != 0f) {
                    // Y is flipped to match CSS-down direction (Input System
                    // reports positive Y for scroll-up).
                    var mode = LooksLikeLineWheel(scroll) ? WheelDeltaMode.Line : WheelDeltaMode.Pixel;
                    dispatcher.DispatchWheel(cssX, cssY, scroll.x, -scroll.y, mode, mods);
                }
            }
        }

        static bool LooksLikeLineWheel(Vector2 scroll) {
            float ax = Mathf.Abs(scroll.x);
            float ay = Mathf.Abs(scroll.y);
            float m = ax > ay ? ax : ay;
            return m > 0f && m <= 10f;
        }

        static void HandleButton(EventDispatcher dispatcher, ref bool last, bool current, int button, double x, double y, KeyModifiers mods) {
            if (current == last) return;
            if (current) dispatcher.DispatchPointerDown(x, y, button, mods);
            else dispatcher.DispatchPointerUp(x, y, button, mods);
            last = current;
        }

        static bool ReadPointer(out Vector2 pos, out bool left, out bool right, out bool middle) {
            var mouse = Mouse.current;
            if (mouse != null) {
                pos = mouse.position.ReadValue();
                left = mouse.leftButton != null && mouse.leftButton.isPressed;
                right = mouse.rightButton != null && mouse.rightButton.isPressed;
                middle = mouse.middleButton != null && mouse.middleButton.isPressed;
                return true;
            }
            var pointer = Pointer.current;
            if (pointer != null) {
                pos = pointer.position.ReadValue();
                left = pointer.press != null && pointer.press.isPressed;
                right = false;
                middle = false;
                return true;
            }
            pos = default;
            left = right = middle = false;
            return false;
        }

        static KeyModifiers ReadModifiers() {
            var kb = Keyboard.current;
            if (kb == null) return KeyModifiers.None;
            var m = KeyModifiers.None;
            if (kb.shiftKey != null && kb.shiftKey.isPressed) m |= KeyModifiers.Shift;
            if (kb.ctrlKey != null && kb.ctrlKey.isPressed) m |= KeyModifiers.Ctrl;
            if (kb.altKey != null && kb.altKey.isPressed) m |= KeyModifiers.Alt;
            if ((kb.leftMetaKey != null && kb.leftMetaKey.isPressed)
                || (kb.rightMetaKey != null && kb.rightMetaKey.isPressed)) m |= KeyModifiers.Meta;
            return m;
        }

        static float ScreenHeight() {
            var cam = Camera.main;
            if (cam != null) return cam.pixelHeight;
            return Screen.height;
        }

        static float ScreenWidth() {
            var cam = Camera.main;
            if (cam != null) return cam.pixelWidth;
            return Screen.width;
        }
    }
#endif
}
