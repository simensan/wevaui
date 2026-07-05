#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_EDITOR
using System;
using UnityEngine;
using Weva.Dom;
using Weva.Events;
using Weva.Forms.Ime;

namespace Weva.Forms.Bridge {
    // Drives an ImeSession from Unity's legacy Input.compositionString.
    //
    // Call Tick() each frame (LateUpdate or per the host's update loop). It
    // observes transitions of compositionString and Input.inputString to drive
    // begin/update/commit on the session.
    //
    // Why legacy IMGUI input?
    //   - The new Input System didn't expose a stable IME callback that
    //     surfaces *committed* text on the same callback as version-of-record
    //     v1.7. Mixing two paths (composition from Input System, commit from
    //     IMGUI) would interleave events. Legacy is one consistent stream.
    //   - Input.compositionString returns the in-flight pre-edit string and
    //     transitions empty -> non-empty -> empty over a composition session.
    //     When it goes back to empty after being non-empty, the committed
    //     characters arrive via Input.inputString in the *same* frame.
    public sealed class UnityImeBridge {
        readonly ImeSession session;
        Element focused;
        string lastComposition = "";

        public UnityImeBridge(ImeSession session) {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public void OnFocusChanged(Element next) {
            focused = next;
            // Only enable IME for elements that accept text. checkboxes/radios
            // don't and we don't want a candidate window to appear.
            bool wantsIme = false;
            if (next != null) {
                if (next.TagName == "textarea") wantsIme = true;
                else if (next.TagName == "input") {
                    var t = next.GetAttribute("type");
                    wantsIme = t == null || t == "" || t == "text" || t == "search" ||
                               t == "email" || t == "tel" || t == "url" || t == "password";
                }
            }
            Input.imeCompositionMode = wantsIme ? IMECompositionMode.On : IMECompositionMode.Off;
        }

        public void SetCompositionCursorPos(Vector2 screenPos) {
            Input.compositionCursorPos = screenPos;
        }

        public void Tick() {
            if (focused == null) return;
            string current = Input.compositionString ?? "";

            if (current.Length > 0) {
                if (lastComposition.Length == 0) {
                    session.BeginComposition();
                }
                if (current != lastComposition) {
                    session.UpdateCompositionString(current);
                }
                lastComposition = current;
                return;
            }

            // current is empty. If we were composing, the user committed (or cancelled).
            if (lastComposition.Length > 0) {
                string committed = Input.inputString ?? "";
                if (!string.IsNullOrEmpty(committed)) {
                    session.CommitComposition(committed);
                } else {
                    session.CancelComposition();
                }
                lastComposition = "";
            }
        }

        public void Reset() {
            lastComposition = "";
            Input.imeCompositionMode = IMECompositionMode.Off;
        }
    }
}
#endif
