using System;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Binding {
    public sealed class AttributeBinding {
        public Element Target { get; }
        public string AttributeName { get; }
        public BindingTemplate Template { get; }

        string lastRendered;
        bool hasRendered;

        public AttributeBinding(Element target, string attributeName, BindingTemplate template) {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (attributeName == null) throw new ArgumentNullException(nameof(attributeName));
            if (template == null) throw new ArgumentNullException(nameof(template));
            Target = target;
            AttributeName = attributeName;
            Template = template;
        }

        public bool Update(object context) {
            return Update(context, null);
        }

        public bool Update(object context, InvalidationTracker tracker) {
            // Pass the current value so an unchanged render returns the same
            // reference without allocating — the idle-frame poll is alloc-free.
            var current = Target.GetAttribute(AttributeName);
            var rendered = Template.Render(context, current);
            if (hasRendered && rendered == lastRendered && rendered == current) return false;
            if (rendered == current) {
                lastRendered = rendered;
                hasRendered = true;
                return false;
            }
            Target.SetAttributePreservingSource(AttributeName, rendered);
            lastRendered = rendered;
            hasRendered = true;
            if (tracker != null) {
                tracker.MarkDirty(Target, InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
            }
            return true;
        }
    }
}
