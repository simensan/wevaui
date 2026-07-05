using System;
using Weva.Dom;

namespace Weva.Events.Manipulators {
    // Manipulator — UI Toolkit-style gesture abstraction over the raw event
    // surface. A manipulator wraps a small bundle of related event listeners
    // (pointer-down + move + up; pointer + key for keyboard equivalents,
    // etc.) and presents a single high-level callback for the gesture
    // (drag delta, context-menu request, long-press hit). Lets authors
    // attach interactions like:
    //
    //     new PanManipulator(elem, dispatcher).OnPan += (dx, dy) => ...;
    //
    // without re-implementing the down→capture→move→up plumbing each time.
    //
    // Manipulators own their own subscriptions; call Wire() once after
    // construction (or pass `autoWire: true`) and Unwire() during teardown.
    // Wire is idempotent.
    public abstract class Manipulator {
        public Element Target { get; }
        public EventDispatcher Dispatcher { get; }

        protected Manipulator(Element target, EventDispatcher dispatcher) {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        bool wired;
        public bool IsWired => wired;

        public void Wire() {
            if (wired) return;
            OnWire();
            wired = true;
        }

        public void Unwire() {
            if (!wired) return;
            OnUnwire();
            wired = false;
        }

        protected abstract void OnWire();
        protected abstract void OnUnwire();
    }
}
