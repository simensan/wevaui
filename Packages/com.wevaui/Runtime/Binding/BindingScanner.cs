using System;
using System.Collections.Generic;
using System.Reflection;
using Weva.Dom;
using Weva.Events;

namespace Weva.Binding {
    public static class BindingScanner {
        public static BindingSet Scan(Document doc, object controller) {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var set = new BindingSet();
            ScanNode(doc, controller, set);
            return set;
        }

        public static BindingSet ScanSubtree(Node root, object controller) {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var set = new BindingSet();
            ScanNode(root, controller, set);
            return set;
        }

        // Append bindings discovered under `root` into an existing set,
        // wiring any new event bindings to `dispatcher` if it's non-null.
        // Used by BindingSet.AttachLive() to incrementally bind subtrees
        // that appear via DOM mutation, so `on-click` attributes on
        // controller-inserted nodes start firing without an explicit
        // SetController() reseat.
        public static void ScanInto(Node root, object controller, BindingSet set, EventDispatcher dispatcher) {
            if (root == null) return;
            if (set == null) throw new ArgumentNullException(nameof(set));
            int evBefore = set.EventBindings.Count;
            ScanNode(root, controller, set);
            if (dispatcher == null) return;
            var ebs = set.EventBindings;
            for (int i = evBefore; i < ebs.Count; i++) ebs[i].Wire(dispatcher);
        }

        static void ScanNode(Node node, object controller, BindingSet set) {
            if (node is TextNode tn) {
                ScanTextNode(tn, set);
                return;
            }
            if (node is Element el) {
                // <template> elements are component-definition source code, not
                // live UI. Their bodies are cloned by ComponentExpander into host
                // sites; scanning them too would double-bind every {{...}} and
                // on-* attribute (once in the source, once in each clone).
                if (string.Equals(el.TagName, "template", StringComparison.OrdinalIgnoreCase)) {
                    if (TryAddRepeatBinding(el, set)) return;
                    return;
                }
                ScanElement(el, controller, set);
            }
            for (int i = 0; i < node.Children.Count; i++) {
                ScanNode(node.Children[i], controller, set);
            }
        }

        static void ScanTextNode(TextNode tn, BindingSet set) {
            var source = tn.Source;
            if (string.IsNullOrEmpty(source)) return;
            if (source.IndexOf("{{", StringComparison.Ordinal) < 0) return;
            var template = BindingTemplate.Parse(source);
            if (!template.HasBinding) return;
            set.Add(new TextBinding(tn, template));
        }

        static void ScanElement(Element el, object controller, BindingSet set) {
            // Snapshot attributes; event-attribute removal would otherwise mutate during iteration.
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in el.Attributes) pairs.Add(kv);

            for (int i = 0; i < pairs.Count; i++) {
                var name = pairs[i].Key;
                var value = pairs[i].Value;
                var source = el.GetAttributeSource(name) ?? value;

                if (TryAddClassBinding(el, name, source, set)) {
                    continue;
                }

                if (EventAttributeMap.TryGet(name, out var kind)) {
                    if (string.IsNullOrEmpty(value)) {
                        set.AddWarning($"Empty handler name on attribute '{name}' of <{el.TagName}>.");
                        continue;
                    }
                    if (controller == null) {
                        set.AddWarning($"Event attribute '{name}=\"{value}\"' on <{el.TagName}> ignored: no controller supplied.");
                        continue;
                    }
                    var method = ResolveMethod(controller, value);
                    if (method == null) {
                        set.AddWarning(
                            $"Event attribute '{name}=\"{value}\"' on <{el.TagName}> ignored: no compatible method named '{value}' found on controller of type {controller.GetType().FullName}.");
                        continue;
                    }
                    var parameters = method.GetParameters();
                    if (parameters.Length > 1) {
                        set.AddWarning(
                            $"Event attribute '{name}=\"{value}\"' on <{el.TagName}> ignored: method '{value}' on {controller.GetType().FullName} has {parameters.Length} parameters; event handlers must have 0 or 1 parameter.");
                        continue;
                    }
                    set.Add(new EventBinding(el, kind, method, controller));
                    continue;
                }

                if (string.IsNullOrEmpty(source)) continue;
                if (source.IndexOf("{{", StringComparison.Ordinal) < 0) continue;
                var template = BindingTemplate.Parse(source);
                if (!template.HasBinding) continue;
                set.Add(new AttributeBinding(el, name, template));
            }
        }

        static bool TryAddRepeatBinding(Element template, BindingSet set) {
            string each = template.GetAttribute("data-each");
            if (string.IsNullOrWhiteSpace(each)) return false;
            if (!TryParseEach(each, out var itemsPath, out var alias)) {
                throw new BindingException(
                    $"Invalid data-each value '{each}'. Expected '<items> as <alias>'.");
            }
            BindingPath? keyPath = null;
            string key = template.GetAttribute("data-key");
            if (!string.IsNullOrWhiteSpace(key)) {
                keyPath = BindingPath.Parse(key);
            }
            set.Add(new RepeatBinding(template, itemsPath, alias, keyPath));
            return true;
        }

        static bool TryParseEach(string raw, out BindingPath itemsPath, out string alias) {
            itemsPath = default;
            alias = null;
            var parts = raw.Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !string.Equals(parts[1], "as", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            itemsPath = BindingPath.Parse(parts[0]);
            alias = parts[2];
            BindingPath.Parse(alias);
            return true;
        }

        static bool TryAddClassBinding(Element el, string name, string value, BindingSet set) {
            const string Prefix = "data-class-";
            if (string.IsNullOrEmpty(name) || !name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            string className = name.Substring(Prefix.Length);
            if (string.IsNullOrWhiteSpace(className)) {
                throw new BindingException($"Invalid class binding attribute '{name}': class name is empty.");
            }
            var path = ParseBindingPathAttribute(value, name);
            set.Add(new ClassBinding(el, className, path));
            return true;
        }

        static BindingPath ParseBindingPathAttribute(string value, string attributeName) {
            if (string.IsNullOrWhiteSpace(value)) {
                throw new BindingException($"Invalid binding attribute '{attributeName}': value is empty.");
            }
            string trimmed = value.Trim();
            if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)) {
                trimmed = trimmed.Substring(2, trimmed.Length - 4).Trim();
            }
            return BindingPath.Parse(trimmed);
        }

        static MethodInfo ResolveMethod(object controller, string name) {
            var t = controller.GetType();
            MethodInfo best = null;
            int bestParamCount = int.MaxValue;
            for (var cur = t; cur != null; cur = cur.BaseType) {
                var ms = cur.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < ms.Length; i++) {
                    if (ms[i].Name != name) continue;
                    var pc = ms[i].GetParameters().Length;
                    if (pc > 1) continue;
                    if (best == null || pc > bestParamCount) {
                        best = ms[i];
                        bestParamCount = pc;
                    }
                }
                if (best != null) return best;
            }
            return best;
        }
    }
}
