using System;
using System.Runtime.CompilerServices;
using Weva.Dom;

namespace Weva.ViewTransitions {
    // We cannot add an instance method to the sealed Dom.Document type, but the
    // user-facing API the spec asks for is `Document.StartViewTransition(mutate)`.
    // We approximate it with an extension method that looks up the engine via a
    // ConditionalWeakTable populated by UIDocumentBuilder.
    public static class DocumentViewTransitionExtensions {
        static readonly ConditionalWeakTable<Document, ViewTransitionEngine> registry = new();

        public static void AttachViewTransitionEngine(Document doc, ViewTransitionEngine engine) {
            if (doc == null || engine == null) return;
            registry.Remove(doc);
            registry.Add(doc, engine);
        }

        public static ViewTransitionEngine GetViewTransitionEngine(this Document doc) {
            if (doc == null) return null;
            return registry.TryGetValue(doc, out var e) ? e : null;
        }

        public static ViewTransition StartViewTransition(this Document doc, Action mutate) {
            var engine = doc.GetViewTransitionEngine();
            if (engine == null) {
                mutate?.Invoke();
                return null;
            }
            return engine.Start(mutate);
        }
    }
}
