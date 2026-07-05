using System;
using System.Reflection;
using Weva.Dom;
using Weva.Events;

namespace Weva.Binding {
    public sealed class EventBinding {
        public Element Target { get; }
        public EventKind Kind { get; }
        public MethodInfo Handler { get; }
        public object Controller { get; }
        public string MethodName { get; }

        EventListener listener;
        EventDispatcher boundDispatcher;
        bool subscribed;

        public EventBinding(Element target, EventKind kind, MethodInfo handler, object controller) {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Target = target;
            Kind = kind;
            Handler = handler;
            Controller = controller;
            MethodName = handler.Name;
        }

        public void Wire(EventDispatcher dispatcher) {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (subscribed) return;
            listener = Invoke;
            dispatcher.AddEventListener(Target, Kind, listener);
            boundDispatcher = dispatcher;
            subscribed = true;
        }

        public void Unwire() {
            if (!subscribed) return;
            boundDispatcher.RemoveEventListener(Target, Kind, listener);
            boundDispatcher = null;
            listener = null;
            subscribed = false;
        }

        void Invoke(UIEvent evt) {
            var parameters = Handler.GetParameters();
            object[] args;
            if (parameters.Length == 0) {
                args = Array.Empty<object>();
            } else if (parameters.Length == 1) {
                var pType = parameters[0].ParameterType;
                if (pType.IsInstanceOfType(evt)) {
                    args = new object[] { evt };
                } else if (pType == typeof(UIEvent) || pType.IsAssignableFrom(typeof(UIEvent))) {
                    args = new object[] { evt };
                } else {
                    args = new object[] { null };
                }
            } else {
                throw new BindingException(
                    $"Event handler '{Handler.Name}' has unsupported signature: only 0 or 1 parameter is allowed.");
            }
            Handler.Invoke(Handler.IsStatic ? null : Controller, args);
        }
    }
}
