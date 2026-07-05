namespace Weva.Events {
    public sealed class WheelEvent : UIEvent {
        public double X { get; internal set; }
        public double Y { get; internal set; }
        public double DeltaX { get; internal set; }
        public double DeltaY { get; internal set; }
        public WheelDeltaMode DeltaMode { get; internal set; }
        public bool ShiftKey { get; internal set; }
        public bool CtrlKey { get; internal set; }
        public bool AltKey { get; internal set; }
        public bool MetaKey { get; internal set; }

        public WheelEvent() {
            Bubbles = true;
        }
    }

    public enum WheelDeltaMode {
        Pixel = 0,
        Line = 1,
        Page = 2
    }
}
