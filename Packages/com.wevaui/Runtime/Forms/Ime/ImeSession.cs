using System;

namespace Weva.Forms.Ime {
    // Pure C# state machine for an IME composition session. The Unity-side
    // bridge polls Input.compositionString each frame and drives the calls below.
    // Renderers that want to draw the in-flight composition with an underline
    // read CompositionString and CompositionCaret directly while State == Composing.
    public sealed class ImeSession {
        public ImeState State { get; private set; } = ImeState.Inactive;
        public string CompositionString { get; private set; } = "";
        public int CompositionCaret { get; private set; }

        public event Action<string> CompositionStarted;
        public event Action<string> CompositionUpdated;
        public event Action<string> CompositionCommitted;
        public event Action CompositionCancelled;

        public void BeginComposition() {
            // Beginning while already composing is treated as a fresh session —
            // any in-flight string is dropped. The Unity IME dispatcher can call
            // BeginComposition again whenever Input.compositionString transitions
            // from "" to non-empty after a previous commit.
            CompositionString = "";
            CompositionCaret = 0;
            State = ImeState.Composing;
            CompositionStarted?.Invoke("");
        }

        public void UpdateCompositionString(string text) {
            if (State != ImeState.Composing) {
                BeginComposition();
            }
            CompositionString = text ?? "";
            CompositionCaret = CompositionString.Length;
            CompositionUpdated?.Invoke(CompositionString);
        }

        public void CommitComposition(string finalText) {
            if (State != ImeState.Composing) {
                // Allow direct commit (some IME flows skip an explicit begin).
                if (string.IsNullOrEmpty(finalText)) return;
            }
            // Empty commit collapses back to Inactive without firing a commit event —
            // mirrors what the OS IME does when a user backspaces away the composition.
            if (string.IsNullOrEmpty(finalText)) {
                State = ImeState.Inactive;
                CompositionString = "";
                CompositionCaret = 0;
                CompositionCancelled?.Invoke();
                return;
            }
            State = ImeState.Confirmed;
            CompositionString = "";
            CompositionCaret = 0;
            CompositionCommitted?.Invoke(finalText);
            State = ImeState.Inactive;
        }

        public void CancelComposition() {
            if (State != ImeState.Composing) return;
            State = ImeState.Inactive;
            CompositionString = "";
            CompositionCaret = 0;
            CompositionCancelled?.Invoke();
        }
    }
}
