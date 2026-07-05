using System;
using System.Collections;
using System.Collections.Generic;
using Weva.Components;
using Weva.Dom;
using Weva.Events;
using Weva.Reactive;

namespace Weva.Binding {
    public sealed class RepeatBinding : IDisposable {
        sealed class Instance : IDisposable {
            public object Key;
            public object Item;
            public int Index;
            public List<Node> Nodes;
            public BindingSet Bindings;
            // Reused per-frame; re-pointed via Reset instead of allocating a
            // fresh scope on every Update.
            public BindingScope Scope;

            public void Dispose() {
                Bindings?.Dispose();
                Bindings = null;
            }
        }

        // Boxed-int cache for index-based repeat keys. Index keys are the
        // default (no key= attribute), and boxing a fresh int per item per
        // frame is pure idle-frame garbage.
        static readonly object[] boxedIndexCache = BuildBoxedIndexCache(256);

        static object[] BuildBoxedIndexCache(int n) {
            var arr = new object[n];
            for (int i = 0; i < n; i++) arr[i] = i;
            return arr;
        }

        static object BoxIndex(int index) =>
            (uint)index < (uint)boxedIndexCache.Length ? boxedIndexCache[index] : index;

        readonly List<Instance> instances = new();
        readonly List<object> desiredItems = new();
        readonly Dictionary<object, Instance> oldByKey = new();
        readonly List<Instance> nextInstances = new();
        readonly HashSet<Instance> usedInstances = new();
        // Scratch scope for keyed ResolveKey lookups; reused across items.
        BindingScope keyScope;
        EventDispatcher dispatcher;
        bool disposed;

        public Element Template { get; }
        public BindingPath ItemsPath { get; }
        public string Alias { get; }
        public BindingPath? KeyPath { get; }

        public RepeatBinding(Element template, BindingPath itemsPath, string alias, BindingPath? keyPath = null) {
            Template = template ?? throw new ArgumentNullException(nameof(template));
            if (string.IsNullOrWhiteSpace(alias)) throw new ArgumentException("Repeat alias is required.", nameof(alias));
            ItemsPath = itemsPath;
            Alias = alias.Trim();
            KeyPath = keyPath;
        }

        public int InstanceCount => instances.Count;

        public void Wire(EventDispatcher eventDispatcher) {
            dispatcher = eventDispatcher;
            for (int i = 0; i < instances.Count; i++) {
                instances[i].Bindings?.Wire(dispatcher);
            }
        }

        public bool Owns(Node node) {
            if (node == null) return false;
            for (int i = 0; i < instances.Count; i++) {
                var roots = instances[i].Nodes;
                if (roots == null) continue;
                for (int r = 0; r < roots.Count; r++) {
                    if (IsDescendantOrSelf(node, roots[r])) return true;
                }
            }
            return false;
        }

        public bool Update(object context, InvalidationTracker tracker = null) {
            if (disposed) throw new ObjectDisposedException(nameof(RepeatBinding));
            var parent = Template.Parent;
            if (parent == null) return false;

            ReadItems(context, desiredItems);
            if (IsSameOrder(context)) {
                bool changed = false;
                for (int i = 0; i < instances.Count; i++) {
                    var inst = instances[i];
                    var item = desiredItems[i];
                    if (!ReferenceEquals(inst.Item, item)) inst.Item = item;
                    inst.Index = i;
                    var scope = ScopeFor(inst, context);
                    if (inst.Bindings != null && inst.Bindings.Update(scope, tracker)) {
                        changed = true;
                    }
                }
                return changed;
            }

            oldByKey.Clear();
            for (int i = 0; i < instances.Count; i++) {
                if (!oldByKey.ContainsKey(instances[i].Key)) oldByKey.Add(instances[i].Key, instances[i]);
            }

            nextInstances.Clear();
            usedInstances.Clear();
            bool structureChanged = desiredItems.Count != instances.Count;
            for (int i = 0; i < desiredItems.Count; i++) {
                object item = desiredItems[i];
                object key = ResolveKey(item, context, i);
                Instance inst = null;
                if (oldByKey.TryGetValue(key, out var existing) && !usedInstances.Contains(existing)) {
                    inst = existing;
                    usedInstances.Add(existing);
                } else {
                    inst = CreateInstance(item, i, context);
                    structureChanged = true;
                }
                if (!Equals(inst.Key, key) || !ReferenceEquals(inst.Item, item) || inst.Index != i) {
                    inst.Key = key;
                    inst.Item = item;
                    inst.Index = i;
                }
                if (i >= instances.Count || !ReferenceEquals(instances[i], inst)) structureChanged = true;
                nextInstances.Add(inst);
            }

            for (int i = instances.Count - 1; i >= 0; i--) {
                var old = instances[i];
                if (usedInstances.Contains(old)) continue;
                RemoveInstance(old);
                structureChanged = true;
            }

            instances.Clear();
            instances.AddRange(nextInstances);
            if (structureChanged) ReorderIntoDom(parent);

            bool bindingsChanged = false;
            for (int i = 0; i < instances.Count; i++) {
                var inst = instances[i];
                var scope = ScopeFor(inst, context);
                if (inst.Bindings != null && inst.Bindings.Update(scope, tracker)) {
                    bindingsChanged = true;
                }
            }

            if (structureChanged && tracker != null && Template.Parent is Element owner) {
                tracker.MarkDirty(owner,
                    InvalidationKind.Structure
                    | InvalidationKind.Style
                    | InvalidationKind.Layout
                    | InvalidationKind.Paint);
            }
            return structureChanged || bindingsChanged;
        }

        BindingScope ScopeFor(Instance inst, object context) {
            if (inst.Scope == null) {
                inst.Scope = new BindingScope(context, Alias, inst.Item, inst.Index);
            } else {
                inst.Scope.Reset(context, inst.Item, inst.Index);
            }
            return inst.Scope;
        }

        bool IsSameOrder(object context) {
            if (desiredItems.Count != instances.Count) return false;
            for (int i = 0; i < desiredItems.Count; i++) {
                object key = ResolveKey(desiredItems[i], context, i);
                if (!Equals(instances[i].Key, key)) return false;
            }
            return true;
        }

        Instance CreateInstance(object item, int index, object parentContext) {
            var nodes = TemplateInstantiator.CloneTemplateBody(Template);
            var bindings = new BindingSet();
            for (int i = 0; i < nodes.Count; i++) {
                BindingScanner.ScanInto(nodes[i], parentContext, bindings, null);
            }
            if (dispatcher != null) bindings.Wire(dispatcher);
            return new Instance {
                Item = item,
                Index = index,
                Nodes = nodes,
                Bindings = bindings
            };
        }

        void RemoveInstance(Instance inst) {
            if (inst?.Nodes != null) {
                for (int i = inst.Nodes.Count - 1; i >= 0; i--) {
                    inst.Nodes[i].Parent?.RemoveChild(inst.Nodes[i]);
                }
            }
            inst?.Dispose();
        }

        void ReorderIntoDom(Node parent) {
            Node anchor = Template;
            for (int i = instances.Count - 1; i >= 0; i--) {
                var nodes = instances[i].Nodes;
                if (nodes == null || nodes.Count == 0) continue;
                for (int n = nodes.Count - 1; n >= 0; n--) {
                    parent.InsertBefore(nodes[n], anchor);
                    anchor = nodes[n];
                }
            }
        }

        void ReadItems(object context, List<object> result) {
            result.Clear();
            if (!BindingResolver.TryResolve(context, ItemsPath, out var value) || value == null) return;
            if (value is string) {
                result.Add(value);
                return;
            }
            // IList fast path: indexed reads avoid the per-frame enumerator
            // allocation foreach makes on an IEnumerable-typed receiver.
            if (value is IList list) {
                for (int i = 0; i < list.Count; i++) result.Add(list[i]);
                return;
            }
            if (value is IEnumerable enumerable) {
                foreach (var item in enumerable) result.Add(item);
                return;
            }
            result.Add(value);
        }

        object ResolveKey(object item, object parentContext, int index) {
            if (!KeyPath.HasValue) return BoxIndex(index);
            var path = KeyPath.Value;
            object value = null;
            if (path.Count > 0 && path.Segments[0] == Alias) {
                if (keyScope == null) {
                    keyScope = new BindingScope(parentContext, Alias, item, index);
                } else {
                    keyScope.Reset(parentContext, item, index);
                }
                BindingResolver.TryResolve(keyScope, path, out value);
            } else {
                BindingResolver.TryResolve(item, path, out value);
            }
            return value ?? BoxIndex(index);
        }

        static bool IsDescendantOrSelf(Node node, Node ancestorOrSelf) {
            for (var n = node; n != null; n = n.Parent) {
                if (n == ancestorOrSelf) return true;
            }
            return false;
        }

        public void Dispose() {
            if (disposed) return;
            for (int i = instances.Count - 1; i >= 0; i--) RemoveInstance(instances[i]);
            instances.Clear();
            disposed = true;
        }
    }
}
