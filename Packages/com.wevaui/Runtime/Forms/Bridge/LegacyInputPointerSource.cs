#if !WEVA_INPUTSYSTEM
using UnityEngine;
using Weva.Events;

namespace Weva.Forms.Bridge {
    // Legacy `UnityEngine.Input` fallback pointer source. Activated when the
    // new Input System package isn't installed (WEVA_INPUTSYSTEM define
    // absent). Reads `Input.mousePosition`, button states, and modifier keys
    // and pumps the same DispatchPointerMove / DispatchPointerDown /
    // DispatchPointerUp / DispatchWheel calls the new-system implementation
    // does. Same Y-flip at the bridge boundary so downstream consumers stay
    // in CSS top-left coordinates.
    //
    // The legacy Input class is deprecated in Unity 6 but still functional
    // when the project's "Active Input Handling" setting is set to "Both"
    // (default for the legacy game template) or "Input Manager (Old)". When
    // "Input System Package (New)" is the only option AND WEVA_INPUTSYSTEM
    // is not defined, this source will silently no-op — that's a project
    // misconfiguration the user must resolve (install the Input System
    // package and add `WEVA_INPUTSYSTEM` to scripting define symbols).
    public sealed class LegacyInputPointerSource : IUIPointerSource {
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
                    // Pointer just left the window — flush hover chain by
                    // dispatching to a guaranteed-empty coordinate so the
                    // previously-hovered element emits PointerLeave.
                    dispatcher.DispatchPointerMove(-1, -1, mods);
                }
                lastPosition = rawPos;
                lastOnScreen = onScreen;
            }

            HandleButton(dispatcher, ref lastLeft, leftDown, 0, cssX, cssY, mods);
            HandleButton(dispatcher, ref lastRight, rightDown, 2, cssX, cssY, mods);
            HandleButton(dispatcher, ref lastMiddle, middleDown, 1, cssX, cssY, mods);

            // Legacy mouseScrollDelta is notch/line units, not CSS pixels.
            // Tagging it as Pixel made one wheel notch scroll by ~1px, which
            // feels nearly stuck in real scrollports.
            var scroll = Input.mouseScrollDelta;
            if (scroll.x != 0f || scroll.y != 0f) {
                dispatcher.DispatchWheel(cssX, cssY, scroll.x, -scroll.y, WheelDeltaMode.Line, mods);
            }
        }

        static void HandleButton(EventDispatcher dispatcher, ref bool last, bool current, int button, double x, double y, KeyModifiers mods) {
            if (current == last) return;
            if (current) dispatcher.DispatchPointerDown(x, y, button, mods);
            else dispatcher.DispatchPointerUp(x, y, button, mods);
            last = current;
        }

        static bool ReadPointer(out Vector2 pos, out bool left, out bool right, out bool middle) {
            pos = Input.mousePosition;
            left = Input.GetMouseButton(0);
            right = Input.GetMouseButton(1);
            middle = Input.GetMouseButton(2);
            return true;
        }

        static KeyModifiers ReadModifiers() {
            var m = KeyModifiers.None;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= KeyModifiers.Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= KeyModifiers.Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= KeyModifiers.Alt;
            if (Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand)
                || Input.GetKey(KeyCode.LeftWindows) || Input.GetKey(KeyCode.RightWindows)) m |= KeyModifiers.Meta;
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
}
#endif
