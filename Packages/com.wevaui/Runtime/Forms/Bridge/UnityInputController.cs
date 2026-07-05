using UnityEngine;
using Weva.Events;

namespace Weva.Forms.Bridge {
    // MonoBehaviour glue. Sits next to a WevaDocument and pumps the pointer +
    // keyboard sources into doc.Events every frame. Decoupled from WevaDocument
    // itself so non-Input-System projects (or Edit Mode tooling) can compose
    // their own driver without dragging this in.
    [AddComponentMenu("Weva/Unity Input Controller")]
    [DisallowMultipleComponent]
    public sealed class UnityInputController : MonoBehaviour {
        // Runtime toggle for diagnostic logging. Flip via inspector or
        // `UnityInputController.LogEvents = true` at runtime. Logs each
        // tick's pointer state + every Dispatch* call so you can see
        // whether events reach the dispatcher at all. Logs only happen
        // when Awake/Update bind a non-null source, so a stale build
        // with `pointer=null` shows up as the "no pointer source" log
        // once per session.
        public static bool LogEvents = false;

        // Per-instance toggle: when true, sets the static LogEvents flag at
        // Awake so logs fire for every Tick + Dispatch. Useful for diagnosing
        // pointer-event delivery issues. Off by default — verbose per-frame
        // logs hurt performance.
        [SerializeField] bool logEventsAtStart = false;

        WevaDocument doc;
        IUIPointerSource pointer;
#if WEVA_INPUTSYSTEM
        InputSystemKeyboardSource keyboard;
        InputSystemGamepadNavSource gamepadNav;
#endif
        int tickCount;
        bool loggedSourceMissing;
        bool loggedDocMissing;
        bool loggedDispatcherMissing;

        public IUIPointerSource Pointer {
            get => pointer;
            set => pointer = value;
        }

        void Awake() {
            doc = GetComponent<WevaDocument>();
            if (logEventsAtStart) LogEvents = true;
#if WEVA_INPUTSYSTEM
            pointer = new UnityPointerSource();
#else
            // Fallback when the new Input System package isn't installed:
            // use UnityEngine.Input (legacy Input Manager). Otherwise the
            // dispatcher receives no pointer events and hover / click /
            // wheel never fire. Authors can still override via the public
            // Pointer setter to plug in a custom source.
            pointer = new LegacyInputPointerSource();
#endif
        }

        void OnEnable() {
#if WEVA_INPUTSYSTEM
            // LIVE resolver, not a captured reference (the focused=NULL bug):
            // WevaDocument rebuilds its pipeline after OnEnable
            // (DelayedRebuild / hot reload / document swaps), replacing
            // doc.Events. A keyboard source holding the OnEnable-time
            // dispatcher delivered every keystroke to the DEAD dispatcher —
            // clicks focused fields on the live one, so text fell on an
            // eternally-unfocused root and typing never edited anything.
            if (doc != null) {
                keyboard = new InputSystemKeyboardSource(LiveDispatcher);
                keyboard.Enable();
                gamepadNav = new InputSystemGamepadNavSource(LiveDispatcher);
            }
#endif
        }

#if WEVA_INPUTSYSTEM
        Weva.Events.EventDispatcher LiveDispatcher() => doc != null ? doc.Events : null;
#endif

        void OnDisable() {
#if WEVA_INPUTSYSTEM
            keyboard?.Dispose();
            keyboard = null;
            gamepadNav = null;
#endif
        }

        void Update() {
            if (doc == null) {
                if (LogEvents && !loggedDocMissing) { Debug.LogWarning("[UnityInputController] Update: doc is null — no WevaDocument on this GameObject."); loggedDocMissing = true; }
                return;
            }
            // Skip Edit Mode entirely — the bridge only runs while the game
            // simulation is live, so editor input never pollutes the
            // dispatcher.
            if (!Application.isPlaying) return;
            var dispatcher = doc.Events;
            if (dispatcher == null) {
                if (LogEvents && !loggedDispatcherMissing) { Debug.LogWarning("[UnityInputController] Update: doc.Events is null — WevaDocument's pipeline not built yet."); loggedDispatcherMissing = true; }
                return;
            }
            if (pointer == null) {
                if (LogEvents && !loggedSourceMissing) { Debug.LogWarning("[UnityInputController] Update: pointer source is null — Awake didn't run or build defines don't match. WEVA_INPUTSYSTEM=" +
#if WEVA_INPUTSYSTEM
                    "DEFINED"
#else
                    "NOT DEFINED"
#endif
                    ); loggedSourceMissing = true; }
            } else {
                pointer.Tick(dispatcher, Time.unscaledTimeAsDouble);
            }
#if WEVA_INPUTSYSTEM
            // Lazy fallbacks also use the LIVE resolver — capturing the
            // current dispatcher here recreates the staleness bug on the
            // next pipeline rebuild.
            if (keyboard == null) {
                keyboard = new InputSystemKeyboardSource(LiveDispatcher);
                keyboard.Enable();
            }
            keyboard.Tick(Time.unscaledTimeAsDouble);
            // W3: gamepad d-pad/left-stick -> spatial focus navigation +
            // south-button activation. Created lazily alongside keyboard.
            if (gamepadNav == null) gamepadNav = new InputSystemGamepadNavSource(LiveDispatcher);
            gamepadNav.Tick(Time.unscaledTimeAsDouble);
#endif
        }
    }
}
