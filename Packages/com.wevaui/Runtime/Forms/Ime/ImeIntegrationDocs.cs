// Where the Unity IME poll hooks in.
//
// Weva's IME story (v1, desktop only — Windows + macOS) is built on the
// legacy IMGUI input API:
//
//   * UnityEngine.Input.compositionString
//       The string currently being composed by the OS IME. It is empty while
//       no composition is active, transitions to non-empty when the user starts
//       typing in their IME, and is cleared on commit (the committed text then
//       arrives via Input.inputString as ordinary characters in the next frame).
//
//   * UnityEngine.Input.imeCompositionMode
//       Set to On while a text input has focus. When Off, the OS will not show
//       the IME candidate window. Auto leaves it to Unity, which is fine for
//       most desktop apps but is not always reliable inside IL2CPP builds.
//
//   * UnityEngine.Input.compositionCursorPos
//       Screen-space hint to the OS where the composition popup should appear.
//       The bridge sets this from the focused input's caret rectangle.
//
// Call sequence:
//
//   1. EventDispatcher reports a focus change to a text-capable element
//      (<input type=text|password|email|search|tel|url|number> or <textarea>).
//      The bridge records this element and sets imeCompositionMode = On.
//
//   2. Each frame (LateUpdate is fine; we only need to observe), the bridge
//      reads Input.compositionString:
//        - empty -> non-empty: ImeSession.BeginComposition + UpdateCompositionString
//        - non-empty -> non-empty (changed): ImeSession.UpdateCompositionString
//        - non-empty -> empty: ImeSession.CommitComposition with the
//          characters that arrived in Input.inputString this frame
//          (usually contains the committed text, possibly with extra keystrokes).
//
//   3. The InputController subscribed to ImeSession.CompositionCommitted
//      forwards the committed text into TextEditModel.Insert, which writes
//      back to <input>.value via Element.SetAttribute and the reactivity layer
//      then propagates the change.
//
// Mobile IME (Android/iOS) is intentionally deferred. On mobile, Unity routes
// text through TouchScreenKeyboard rather than Input.compositionString, and
// the surface is not yet in-game (it is a native edit field). Console IME
// is also out of scope for v1.
//
// The new Input System (com.unity.inputsystem) added Keyboard.current.onIMECompositionChange
// in 1.7+. We do NOT use that yet because it does not (as of writing) expose
// the committed text in the same callback — falling back to legacy is OK
// for v1 and avoids a second code path.

namespace Weva.Forms.Ime {
    // Marker type so this file participates in compilation; comment-only files
    // sometimes get pruned by tooling.
    internal static class ImeIntegrationDocs { }
}
