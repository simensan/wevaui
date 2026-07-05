namespace Weva.Events {
    public delegate void EventListener(UIEvent evt);

    public readonly struct EventListenerRegistration {
        public readonly EventKind Kind;
        public readonly EventListener Handler;
        public readonly bool UseCapture;

        public EventListenerRegistration(EventKind kind, EventListener handler, bool useCapture) {
            Kind = kind;
            Handler = handler;
            UseCapture = useCapture;
        }
    }
}
