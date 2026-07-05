using System;
using System.Collections.Generic;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Binding {
    public sealed class BindingSet : IDisposable {
        readonly List<TextBinding> textBindings = new();
        readonly List<AttributeBinding> attributeBindings = new();
        readonly List<ClassBinding> classBindings = new();
        readonly List<RepeatBinding> repeatBindings = new();
        readonly List<EventBinding> eventBindings = new();
        readonly List<string> warnings = new();

        EventDispatcher wiredDispatcher;
        bool disposed;

        // Version gate (IBindingVersion contexts): when the same controller
        // reports the same version and no bindings were added/removed since
        // the last pass, Update returns without polling anything.
        object lastContext;
        int lastVersion;
        bool versionGateValid;
        bool structureDirty;

        // Live-mutation observation: when set, mutations on this document
        // trigger an incremental BindingScanner.ScanInto on inserted
        // subtrees and unwire-then-drop on removed ones. Lets DOM-mutating
        // controllers (todo lists, dynamic forms) avoid manually re-seating
        // the controller after every AppendChild.
        Document liveDoc;
        object liveController;
        Action<DomMutation> mutationListener;

        public IReadOnlyList<TextBinding> TextBindings => textBindings;
        public IReadOnlyList<AttributeBinding> AttributeBindings => attributeBindings;
        public IReadOnlyList<ClassBinding> ClassBindings => classBindings;
        public IReadOnlyList<RepeatBinding> RepeatBindings => repeatBindings;
        public IReadOnlyList<EventBinding> EventBindings => eventBindings;
        public IReadOnlyList<string> Warnings => warnings;

        internal void Add(TextBinding b) { textBindings.Add(b); structureDirty = true; }
        internal void Add(AttributeBinding b) { attributeBindings.Add(b); structureDirty = true; }
        internal void Add(ClassBinding b) { classBindings.Add(b); structureDirty = true; }
        internal void Add(RepeatBinding b) { repeatBindings.Add(b); structureDirty = true; }
        internal void Add(EventBinding b) { eventBindings.Add(b); structureDirty = true; }
        internal void AddWarning(string msg) { warnings.Add(msg); }

        public void Wire(EventDispatcher dispatcher) {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (disposed) throw new ObjectDisposedException(nameof(BindingSet));
            for (int i = 0; i < eventBindings.Count; i++) {
                eventBindings[i].Wire(dispatcher);
            }
            for (int i = 0; i < repeatBindings.Count; i++) {
                repeatBindings[i].Wire(dispatcher);
            }
            wiredDispatcher = dispatcher;
        }

        public void UpdateAll(object context) {
            Update(context, null);
        }

        public bool Update(object context, InvalidationTracker tracker = null,
                           Func<Element, Weva.Css.Cascade.ComputedStyle> styleOf = null) {
            // Version gate: an IBindingVersion controller that hasn't bumped
            // since the last pass (and no bindings were added/removed) means
            // nothing reachable from a binding expression changed — skip the
            // whole poll. The version is captured BEFORE the pass so a bump
            // that lands mid-update re-runs on the next frame instead of
            // being swallowed.
            bool hasVersion = false;
            int version = 0;
            if (context is IBindingVersion versioned) {
                hasVersion = true;
                version = versioned.BindingVersion;
            }
            if (versionGateValid && !structureDirty && hasVersion &&
                ReferenceEquals(context, lastContext) && version == lastVersion) {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < repeatBindings.Count; i++) {
                if (repeatBindings[i].Update(context, tracker)) changed = true;
            }
            for (int i = 0; i < textBindings.Count; i++) {
                if (textBindings[i].Update(context, tracker, styleOf)) changed = true;
            }
            for (int i = 0; i < attributeBindings.Count; i++) {
                if (attributeBindings[i].Update(context, tracker)) changed = true;
            }
            for (int i = 0; i < classBindings.Count; i++) {
                if (classBindings[i].Update(context, tracker)) changed = true;
            }

            lastContext = context;
            lastVersion = version;
            versionGateValid = hasVersion;
            structureDirty = false;
            return changed;
        }

        // Wire incremental binding for dynamically inserted subtrees. Idempotent.
        // Must be called after Wire() so the dispatcher is known. Pairs with
        // Dispose() — the mutation subscription is released there.
        public void AttachLive(Document doc, object controller) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (disposed) throw new ObjectDisposedException(nameof(BindingSet));
            if (liveDoc == doc && ReferenceEquals(liveController, controller)) return;
            DetachLive();
            liveDoc = doc;
            liveController = controller;
            mutationListener = OnMutation;
            doc.Mutated += mutationListener;
        }

        void DetachLive() {
            if (liveDoc != null && mutationListener != null) {
                liveDoc.Mutated -= mutationListener;
            }
            liveDoc = null;
            liveController = null;
            mutationListener = null;
        }

        void OnMutation(DomMutation m) {
            if (disposed) return;
            switch (m.Kind) {
                case DomMutationKind.ChildAdded:
                    // m.Subject is the newly-added child; scan its subtree
                    // for on-* attributes and {{...}} templates and wire
                    // event bindings to the dispatcher we're already
                    // attached to.
                    if (IsOwnedByRepeat(m.Subject)) break;
                    BindingScanner.ScanInto(m.Subject, liveController, this, wiredDispatcher);
                    break;
                case DomMutationKind.ChildRemoved:
                    if (IsOwnedByRepeat(m.Subject)) break;
                    // Drop bindings whose Target lives in the removed
                    // subtree. EventBinding.Unwire detaches the dispatcher
                    // listener so stale handlers don't fire if the parent
                    // hands the same Element back in via another path.
                    PurgeBindingsUnder(m.Subject);
                    break;
            }
        }

        void PurgeBindingsUnder(Node removed) {
            if (removed == null) return;
            // Conservative: a structural purge releases the version gate so
            // the next Update re-polls the survivors.
            structureDirty = true;
            for (int i = eventBindings.Count - 1; i >= 0; i--) {
                if (IsDescendantOrSelf(eventBindings[i].Target, removed)) {
                    eventBindings[i].Unwire();
                    eventBindings.RemoveAt(i);
                }
            }
            for (int i = textBindings.Count - 1; i >= 0; i--) {
                if (IsDescendantOrSelf(textBindings[i].Target, removed)) {
                    textBindings.RemoveAt(i);
                }
            }
            for (int i = attributeBindings.Count - 1; i >= 0; i--) {
                if (IsDescendantOrSelf(attributeBindings[i].Target, removed)) {
                    attributeBindings.RemoveAt(i);
                }
            }
            for (int i = classBindings.Count - 1; i >= 0; i--) {
                if (IsDescendantOrSelf(classBindings[i].Target, removed)) {
                    classBindings.RemoveAt(i);
                }
            }
            for (int i = repeatBindings.Count - 1; i >= 0; i--) {
                if (IsDescendantOrSelf(repeatBindings[i].Template, removed) || repeatBindings[i].Owns(removed)) {
                    repeatBindings[i].Dispose();
                    repeatBindings.RemoveAt(i);
                }
            }
        }

        bool IsOwnedByRepeat(Node node) {
            for (int i = 0; i < repeatBindings.Count; i++) {
                if (repeatBindings[i].Owns(node)) return true;
            }
            return false;
        }

        static bool IsDescendantOrSelf(Node node, Node ancestorOrSelf) {
            for (var n = node; n != null; n = n.Parent) {
                if (n == ancestorOrSelf) return true;
            }
            return false;
        }

        public void Dispose() {
            if (disposed) return;
            for (int i = 0; i < eventBindings.Count; i++) {
                eventBindings[i].Unwire();
            }
            for (int i = 0; i < repeatBindings.Count; i++) {
                repeatBindings[i].Dispose();
            }
            DetachLive();
            wiredDispatcher = null;
            lastContext = null;
            disposed = true;
        }
    }
}
