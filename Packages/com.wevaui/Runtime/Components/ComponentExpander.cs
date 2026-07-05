using System;
using System.Collections.Generic;
using Weva.Components.Scoping;
using Weva.Dom;

namespace Weva.Components {
    public sealed class ComponentExpander {
        public const string ExpandedAttribute = ScopeMarkers.ExpandedAttribute;
        public const int DefaultMaxDepth = 32;

        readonly ComponentRegistry registry;
        readonly int maxDepth;

        public ComponentExpander(ComponentRegistry registry)
            : this(registry, DefaultMaxDepth) { }

        public ComponentExpander(ComponentRegistry registry, int maxDepth) {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));
            this.registry = registry;
            this.maxDepth = maxDepth;
        }

        public void Expand(Document doc) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            ExpandChildren(doc, 0);
        }

        public void ExpandSubtree(Element root) {
            if (root == null) throw new ArgumentNullException(nameof(root));
            ExpandNode(root, 0);
        }

        void ExpandNode(Node node, int depth) {
            if (node is Element e) {
                if (string.Equals(e.TagName, "template", StringComparison.OrdinalIgnoreCase)) return;

                if (registry.TryGet(e.TagName, out var template) && !IsExpanded(e)) {
                    ExpandHost(e, template, depth);
                    return;
                }
            }
            ExpandChildren(node, depth);
        }

        void ExpandChildren(Node node, int depth) {
            // Iterate over a snapshot because expansion mutates child collections.
            var snapshot = new List<Node>(node.Children);
            for (int i = 0; i < snapshot.Count; i++) {
                ExpandNode(snapshot[i], depth);
            }
        }

        void ExpandHost(Element host, Element template, int depth) {
            if (depth >= maxDepth) {
                throw new ComponentExpansionException(
                    $"Component expansion depth exceeded ({depth}) at <{host.TagName}>; possible template cycle.",
                    depth, host.TagName);
            }

            var lightDom = new List<Node>(host.Children);
            foreach (var child in lightDom) host.RemoveChild(child);

            var clonedRoots = TemplateInstantiator.CloneTemplateBody(template);

            // Decide BEFORE StampScope/SlotProjection mutates the clones: a
            // template whose body root has the SAME tag name as the host
            // (e.g. <template id="button"><button class="btn"><slot></slot></button></template>)
            // describes a "render output" — the cloned <button class="btn">
            // IS the rendered HTML element, not another component instance to
            // recurse into. Recursing would re-clone the same template inside
            // itself, hit the depth limit, and throw on legitimate nested use
            // (e.g. <app><card><button>…). We mark such cloned roots as
            // already-expanded so the recursion below skips them.
            //
            // Only flag cloned roots that carry actual render content (any
            // pre-stamp attribute OR any child node). A bare same-tag clone
            // like <template id="loop"><loop></loop></template> is a genuine
            // self-cycle: the maxDepth guard must still fire for those, and
            // the <loop> cycle test asserts exactly that. Two-template cycles
            // (a -> b -> a -> ...) don't match here because the cloned root's
            // tag differs from the host's tag, so depth still catches them.
            var selfReferentialRoots = new List<Element>();
            for (int i = 0; i < clonedRoots.Count; i++) {
                if (clonedRoots[i] is Element cloneRoot
                    && string.Equals(cloneRoot.TagName, host.TagName, StringComparison.OrdinalIgnoreCase)
                    && (cloneRoot.Attributes.Count > 0 || cloneRoot.Children.Count > 0)) {
                    selfReferentialRoots.Add(cloneRoot);
                }
            }

            // Stamp the cloned template subtree with the scope marker BEFORE projecting
            // light-dom into slots; this way slot-projected children retain their
            // original (unstamped) attribute set and the component's scoped rules
            // do not match them.
            registry.TryGetScopeId(host.TagName, out var scopeId);
            if (!scopeId.IsEmpty) {
                for (int i = 0; i < clonedRoots.Count; i++) {
                    StampScope(clonedRoots[i], scopeId.Value);
                }
            }

            SlotProjection.Project(clonedRoots, lightDom);

            foreach (var n in clonedRoots) host.AppendChild(n);

            host.SetAttribute(ScopeMarkers.ExpandedAttribute, "1");
            if (!scopeId.IsEmpty) {
                host.SetAttribute(ScopeMarkers.HostAttribute, scopeId.Value);
            }

            // Apply the self-referential-render marker now that the clones are
            // attached. Skipped above for bare self-cycles so depth-limit fires.
            for (int i = 0; i < selfReferentialRoots.Count; i++) {
                var cloneRoot = selfReferentialRoots[i];
                if (!cloneRoot.HasAttribute(ScopeMarkers.ExpandedAttribute)) {
                    cloneRoot.SetAttribute(ScopeMarkers.ExpandedAttribute, "1");
                }
            }

            // Recurse into the freshly inserted subtree so nested component
            // instances expand too. Same-tag render outputs marked above are
            // skipped via IsExpanded; bare self-cycles and cross-template
            // cycles fall through and trip the depth guard.
            for (int i = 0; i < clonedRoots.Count; i++) {
                ExpandNode(clonedRoots[i], depth + 1);
            }
        }

        static void StampScope(Node node, string scopeId) {
            if (node is Element e) {
                e.SetAttribute(ScopeMarkers.ScopeAttribute, scopeId);
            }
            for (int i = 0; i < node.Children.Count; i++) {
                StampScope(node.Children[i], scopeId);
            }
        }

        static bool IsExpanded(Element e) {
            return e.HasAttribute(ScopeMarkers.ExpandedAttribute);
        }
    }
}
