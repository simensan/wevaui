namespace Weva.Forms.Bridge {
    // Clipboard abstraction. Headless tests stub this; in a real Unity app the
    // implementation calls GUIUtility.systemCopyBuffer.
    public interface IClipboardBridge {
        string GetText();
        void SetText(string text);
    }

#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
    public sealed class UnityClipboardBridge : IClipboardBridge {
        public string GetText() {
            // GUIUtility lives in UnityEngine; reading systemCopyBuffer is safe
            // on desktop standalone + editor. On other platforms it returns "".
            return UnityEngine.GUIUtility.systemCopyBuffer ?? "";
        }

        public void SetText(string text) {
            UnityEngine.GUIUtility.systemCopyBuffer = text ?? "";
        }
    }
#endif

    public sealed class InMemoryClipboardBridge : IClipboardBridge {
        string buffer = "";
        public string GetText() => buffer;
        public void SetText(string text) { buffer = text ?? ""; }
    }
}
