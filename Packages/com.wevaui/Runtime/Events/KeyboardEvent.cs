namespace Weva.Events {
    public sealed class KeyboardEvent : UIEvent {
        public string Key { get; internal set; }
        public string Code { get; internal set; }
        public bool ShiftKey { get; internal set; }
        public bool CtrlKey { get; internal set; }
        public bool AltKey { get; internal set; }
        public bool MetaKey { get; internal set; }
        public bool Repeat { get; internal set; }

        public KeyboardEvent() {
            Bubbles = true;
        }
    }
}
