using System;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Binding {
    public sealed class TextBinding {
        public TextNode Target { get; }
        public BindingTemplate Template { get; }

        string lastRendered;
        bool hasRendered;

        public TextBinding(TextNode target, BindingTemplate template) {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (template == null) throw new ArgumentNullException(nameof(template));
            Target = target;
            Template = template;
        }

        public bool Update(object context) {
            return Update(context, null);
        }

        public bool Update(object context, InvalidationTracker tracker) {
            return Update(context, tracker, null);
        }

        public bool Update(object context, InvalidationTracker tracker,
                           Func<Element, Weva.Css.Cascade.ComputedStyle> styleOf) {
            // Pass the current Data so an unchanged render returns the same
            // reference without allocating — the idle-frame poll is alloc-free.
            var rendered = Template.Render(context, Target.Data);
            if (rendered == Target.Data) {
                lastRendered = rendered;
                hasRendered = true;
                return false;
            }
            Target.Data = rendered;
            lastRendered = rendered;
            hasRendered = true;
            if (tracker != null && Target.Parent is Element parent) {
                tracker.MarkLayoutForElement(parent, styleOf);
            }
            return true;
        }
    }
}
