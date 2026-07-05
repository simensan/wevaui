using System;
using System.Collections.Generic;
using Weva.Components.Scoping;
using Weva.Css;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Components {
    public sealed class ComponentRegistry {
        readonly Dictionary<string, Element> templates = new();
        readonly Dictionary<string, ScopeId> scopeIds = new();
        readonly Dictionary<string, ScopedStylesheet> stylesheets = new();

        public void Register(string tagName, Element template) {
            Register(tagName, template, null);
        }

        public void Register(string tagName, Element template, Stylesheet componentStylesheet) {
            if (string.IsNullOrEmpty(tagName)) throw new ArgumentException("tagName required", nameof(tagName));
            if (template == null) throw new ArgumentNullException(nameof(template));
            string key = Normalize(tagName);
            templates[key] = template;
            if (componentStylesheet != null) {
                var id = ScopeId.Generate(key);
                scopeIds[key] = id;
                stylesheets[key] = StylesheetScoper.Scope(componentStylesheet, id);
            } else {
                scopeIds.Remove(key);
                stylesheets.Remove(key);
            }
        }

        // Walks the document and registers every <template id="..."> as a component
        // named by its id. Templates without an id are silently skipped. Templates
        // declared inside another <template> are inert content and are not registered.
        //
        // After registering, top-level <template> definitions are reordered to the
        // end of their parent's children list. Templates are display:none in the
        // UA stylesheet, so reordering does not affect rendering — but it does mean
        // post-expansion DFS helpers (FindByTag, GetElementsByTagName) visit the
        // expanded clone subtree *before* the original template body. This matters
        // because the original body still contains literal "{{ ... }}" attribute
        // values (templates are immutable source); without the reorder, a naive
        // first-match search would return the un-rendered template span rather
        // than the rendered clone.
        public void RegisterAllFromDocument(Document doc) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var topLevelTemplates = new List<Element>();
            WalkAndRegister(doc, false, topLevelTemplates);
            // Move templates to the END of their parent's child list so DFS
            // helpers find expanded clones before the literal template body.
            // Re-attach in registration order so multi-template documents keep
            // a deterministic post-move ordering.
            for (int i = 0; i < topLevelTemplates.Count; i++) {
                var t = topLevelTemplates[i];
                var parent = t.Parent;
                if (parent == null) continue;
                parent.RemoveChild(t);
                parent.AppendChild(t);
            }
        }

        void WalkAndRegister(Node node, bool inTemplate, List<Element> topLevelTemplates) {
            for (int i = 0; i < node.Children.Count; i++) {
                var child = node.Children[i];
                if (child is Element e) {
                    bool isTemplate = string.Equals(e.TagName, "template", StringComparison.OrdinalIgnoreCase);
                    if (isTemplate && !inTemplate) {
                        var id = e.GetAttribute("id");
                        if (!string.IsNullOrEmpty(id)) {
                            templates[Normalize(id)] = e;
                        }
                        topLevelTemplates.Add(e);
                    }
                    WalkAndRegister(e, inTemplate || isTemplate, topLevelTemplates);
                } else {
                    WalkAndRegister(child, inTemplate, topLevelTemplates);
                }
            }
        }

        public bool TryGet(string tagName, out Element template) {
            if (string.IsNullOrEmpty(tagName)) {
                template = null;
                return false;
            }
            return templates.TryGetValue(Normalize(tagName), out template);
        }

        public bool TryGetScopeId(string tagName, out ScopeId scopeId) {
            if (string.IsNullOrEmpty(tagName)) {
                scopeId = ScopeId.None;
                return false;
            }
            return scopeIds.TryGetValue(Normalize(tagName), out scopeId);
        }

        public bool TryGetStylesheet(string tagName, out ScopedStylesheet stylesheet) {
            if (string.IsNullOrEmpty(tagName)) {
                stylesheet = null;
                return false;
            }
            return stylesheets.TryGetValue(Normalize(tagName), out stylesheet);
        }

        public IReadOnlyDictionary<string, ScopedStylesheet> AllStylesheets => stylesheets;

        public IEnumerable<string> RegisteredNames => templates.Keys;

        public int Count => templates.Count;

        public bool Contains(string tagName) {
            if (string.IsNullOrEmpty(tagName)) return false;
            return templates.ContainsKey(Normalize(tagName));
        }

        public void Clear() {
            templates.Clear();
            scopeIds.Clear();
            stylesheets.Clear();
        }

        static string Normalize(string s) => CssStringUtil.ToLowerInvariantOrSame(s);
    }
}
