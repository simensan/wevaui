using Weva.Dom;
using Weva.Events;

namespace Weva.Forms {
    // Text-input dispatch extension for EventDispatcher.
    //
    // The base EventDispatcher exposes DispatchKeyDown/DispatchKeyUp; those carry
    // a single Key/Code pair and are fine for control keys (arrows, Enter, Tab,
    // Backspace) but not for characters that arrive from a system text source
    // (the new Input System's onTextInput, the IMGUI Event.character path, or
    // committed IME composition).
    //
    // Rather than modify EventDispatcher (which would require extending UIEvent
    // with a TextInputEvent and adding state), we route text input through the
    // existing KeyDown machinery using a dedicated key string "TextInput". The
    // KeyboardEvent.Code field carries the literal character payload. This keeps
    // EventDispatcher unchanged and lets InputController distinguish text input
    // from control keystrokes by inspecting the Key value.
    //
    // Consumers that want to register for text input subscribe to EventKind.KeyDown
    // and check evt.Key == "TextInput".
    public static class EventDispatcherExtensions {
        public const string TextInputKey = "TextInput";

        public static void DispatchTextInput(this EventDispatcher dispatcher, string text, KeyModifiers mods = KeyModifiers.None) {
            if (dispatcher == null) return;
            if (string.IsNullOrEmpty(text)) return;
            dispatcher.DispatchKeyDown(TextInputKey, text, mods, repeat: false);
        }

        public static bool IsTextInput(this KeyboardEvent evt) {
            return evt != null && evt.Key == TextInputKey;
        }

        public static string TextInputPayload(this KeyboardEvent evt) {
            return evt != null && evt.Key == TextInputKey ? evt.Code : null;
        }
    }
}
