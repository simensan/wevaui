#if WEVA_INPUTSYSTEM
using System;
using UnityEngine.InputSystem;
using Weva.Events;

namespace Weva.Forms.Bridge {
    // W3 phase 2 — bridges gamepad d-pad + left stick into the dispatcher's
    // spatial focus navigation (EventDispatcher.NavigateFocusSpatially), and
    // the south button into an "Enter" KeyDown/KeyUp pair so focused buttons
    // activate the same way keyboard Enter does.
    //
    // Repeat model (console-UI convention): first press navigates
    // immediately; holding repeats after InitialDelay, then every
    // RepeatInterval. The stick shares the repeat state with the d-pad —
    // whichever direction is held drives the timer; releasing (or crossing
    // the deadzone) resets it.
    //
    // Wrapped in WEVA_INPUTSYSTEM (asmdef versionDefine), mirroring
    // InputSystemKeyboardSource.
    public sealed class InputSystemGamepadNavSource {
        // Resolved per tick, not captured — same staleness trap as
        // InputSystemKeyboardSource: WevaDocument rebuilds replace doc.Events,
        // and a captured reference keeps navigating a dead dispatcher.
        readonly Func<EventDispatcher> dispatcherProvider;
        EventDispatcher Dispatcher => dispatcherProvider();

        // Console-typical feel; public so hosts can tune.
        public double InitialDelay = 0.40;
        public double RepeatInterval = 0.12;
        public float StickDeadzone = 0.5f;

        SpatialDirection? heldDir;
        double nextRepeatAt;

        public InputSystemGamepadNavSource(EventDispatcher dispatcher) {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            dispatcherProvider = () => dispatcher;
        }

        public InputSystemGamepadNavSource(Func<EventDispatcher> dispatcherProvider) {
            this.dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));
        }

        public void Tick(double now) {
            var pad = Gamepad.current;
            if (pad == null) { heldDir = null; return; }
            var dispatcher = Dispatcher;
            if (dispatcher == null) { heldDir = null; return; }

            // Activation: south button → Enter (matches the keyboard path so
            // existing Enter handlers / button activation just work).
            if (pad.buttonSouth.wasPressedThisFrame) {
                dispatcher.DispatchKeyDown("Enter", "Enter", KeyModifiers.None, repeat: false);
            }
            if (pad.buttonSouth.wasReleasedThisFrame) {
                dispatcher.DispatchKeyUp("Enter", "Enter", KeyModifiers.None, repeat: false);
            }

            var dir = ReadDirection(pad);
            if (dir == null) { heldDir = null; return; }

            if (heldDir != dir) {
                // New direction (or fresh press): navigate now, arm the
                // initial-delay timer.
                heldDir = dir;
                dispatcher.NavigateFocusSpatially(dir.Value);
                nextRepeatAt = now + InitialDelay;
                return;
            }
            if (now >= nextRepeatAt) {
                dispatcher.NavigateFocusSpatially(dir.Value);
                nextRepeatAt = now + RepeatInterval;
            }
        }

        SpatialDirection? ReadDirection(Gamepad pad) {
            // D-pad wins over stick when both are active (it's the
            // deliberate input).
            if (pad.dpad.up.isPressed) return SpatialDirection.Up;
            if (pad.dpad.down.isPressed) return SpatialDirection.Down;
            if (pad.dpad.left.isPressed) return SpatialDirection.Left;
            if (pad.dpad.right.isPressed) return SpatialDirection.Right;

            var stick = pad.leftStick.ReadValue();
            float ax = Math.Abs(stick.x), ay = Math.Abs(stick.y);
            if (ax < StickDeadzone && ay < StickDeadzone) return null;
            // Dominant axis decides — diagonal drift shouldn't zig-zag focus.
            if (ax >= ay) return stick.x > 0 ? SpatialDirection.Right : SpatialDirection.Left;
            return stick.y > 0 ? SpatialDirection.Up : SpatialDirection.Down;
        }
    }
}
#endif
