using Weva.Dom;

namespace Weva.Events {
    public sealed class FocusEvent : UIEvent {
        public Element RelatedTarget { get; internal set; }

        public FocusEvent() {
            Bubbles = false;
        }
    }
}
