using System;
using System.Collections.Generic;
using System.Reflection;
using Weva.Binding.Generated;

namespace Weva.Binding {
    public static class BindingResolver {
        readonly struct CacheKey : IEquatable<CacheKey> {
            public readonly Type Type;
            public readonly string Segment;

            public CacheKey(Type t, string s) {
                Type = t;
                Segment = s;
            }

            public bool Equals(CacheKey other) =>
                Type == other.Type && string.Equals(Segment, other.Segment, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is CacheKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Type != null ? Type.GetHashCode() : 0;
                    h = h * 31 + (Segment != null ? Segment.GetHashCode() : 0);
                    return h;
                }
            }
        }

        // Copy-on-write maps: the read side is the per-frame binding poll
        // (every segment resolution consults these), so reads are lock-free
        // against immutable snapshots; writers clone-and-swap under a lock.
        // Registration happens at module-init / test setup, lookups happen
        // millions of times — optimize for the reader.
        static volatile Dictionary<CacheKey, MemberInfo> cache = new();
        static readonly object cacheLock = new();
        // Sentinel meaning "we looked, found nothing" so we don't re-scan.
        static readonly MemberInfo MissingSentinel = typeof(BindingResolver).GetMethod(nameof(MissingMarker), BindingFlags.Static | BindingFlags.NonPublic);

        // Sealed-class fallback: the generator emits a [ModuleInitializer] that
        // registers a factory that wraps an instance in an IBindingAccessor
        // implementation, for types that cannot be made partial.
        static volatile Dictionary<Type, Func<object, IBindingAccessor>> registeredFactories = new();
        // Direct-accessor variant: the registered IBindingAccessor IS the
        // accessor (used when callers register a singleton accessor for a
        // type whose data lives elsewhere; useful in tests).
        static volatile Dictionary<Type, IBindingAccessor> registeredAccessors = new();
        static readonly object registeredLock = new();

        static void MissingMarker() { }

        // Direct-accessor registration entry point. Used by tests and by
        // callers that want a singleton accessor instance.
        public static void RegisterAccessor(Type controllerType, IBindingAccessor accessor) {
            if (controllerType == null) throw new ArgumentNullException(nameof(controllerType));
            if (accessor == null) throw new ArgumentNullException(nameof(accessor));
            lock (registeredLock) {
                var next = new Dictionary<Type, IBindingAccessor>(registeredAccessors) {
                    [controllerType] = accessor
                };
                registeredAccessors = next;
            }
        }

        // Factory registration entry point. The generator emits a
        // [ModuleInitializer] that calls this once per non-partial type with
        // [UIBind] members, so the runtime can produce per-instance accessors.
        public static void RegisterAccessorFactory(Type controllerType, Func<object, IBindingAccessor> factory) {
            if (controllerType == null) throw new ArgumentNullException(nameof(controllerType));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (registeredLock) {
                var next = new Dictionary<Type, Func<object, IBindingAccessor>>(registeredFactories) {
                    [controllerType] = factory
                };
                registeredFactories = next;
            }
        }

        // Remove a previously-registered direct accessor for `controllerType`.
        // Returns true if an entry was removed, false if none existed.
        // Intended for test teardown, hot-reload, and dynamic plugin systems
        // that load and unload controller types at runtime.
        public static bool UnregisterAccessor(Type controllerType) {
            if (controllerType == null) throw new ArgumentNullException(nameof(controllerType));
            lock (registeredLock) {
                if (!registeredAccessors.ContainsKey(controllerType)) return false;
                var next = new Dictionary<Type, IBindingAccessor>(registeredAccessors);
                next.Remove(controllerType);
                registeredAccessors = next;
                return true;
            }
        }

        // Remove a previously-registered accessor factory for `controllerType`.
        // Returns true if an entry was removed, false if none existed.
        // Intended for test teardown, hot-reload, and dynamic plugin systems
        // that load and unload controller types at runtime.
        public static bool UnregisterAccessorFactory(Type controllerType) {
            if (controllerType == null) throw new ArgumentNullException(nameof(controllerType));
            lock (registeredLock) {
                if (!registeredFactories.ContainsKey(controllerType)) return false;
                var next = new Dictionary<Type, Func<object, IBindingAccessor>>(registeredFactories);
                next.Remove(controllerType);
                registeredFactories = next;
                return true;
            }
        }

        // Returns the IBindingAccessor for `instance` or null if none exists.
        // Preference order: instance-implements-interface (the partial-class
        // path) > registered direct accessor > registered factory > none.
        public static IBindingAccessor GetAccessor(object instance) {
            if (instance == null) return null;
            if (instance is IBindingAccessor direct) return direct;

            var t = instance.GetType();
            // Lock-free reads against the copy-on-write snapshots — this runs
            // per segment per frame in the binding poll.
            if (registeredAccessors.TryGetValue(t, out var reg)) return reg;
            if (registeredFactories.TryGetValue(t, out var factory)) return factory(instance);
            return null;
        }

        public static object Resolve(object root, BindingPath path) {
            if (root == null) return null;
            var current = root;
            var segs = path.Segments;
            for (int i = 0; i < segs.Length; i++) {
                if (current == null) return null;
                current = ResolveSegment(current, segs[i]);
            }
            return current;
        }

        // Resolve a single, already-validated path segment against `root`.
        // Equivalent to TryResolve with a one-segment path but without
        // allocating a parsed BindingPath — BindingScope's parent-context
        // fallback runs this per bound segment per frame.
        internal static bool TryResolveSegment(object root, string segment, out object value) {
            if (root == null) {
                value = null;
                return false;
            }
            value = ResolveSegment(root, segment);
            return value != null;
        }

        public static bool TryResolve(object root, BindingPath path, out object value) {
            if (root == null) {
                value = null;
                return false;
            }
            var current = root;
            var segs = path.Segments;
            for (int i = 0; i < segs.Length; i++) {
                if (current == null) {
                    value = null;
                    return false;
                }
                current = ResolveSegment(current, segs[i]);
            }
            value = current;
            return current != null;
        }

        static object ResolveSegment(object instance, string segment) {
            if (instance is IBindingScope scope && scope.TryResolveLocal(segment, out var scoped)) {
                return scoped;
            }

            // Generated fast path. Only declared [UIBind] members are visible
            // here; if TryGet returns false the runtime falls back to
            // reflection so paths that hit non-bound members (or inherited
            // members the generator skipped) keep working.
            var accessor = GetAccessor(instance);
            if (accessor != null && accessor.TryGet(segment, out var generated)) {
                return generated;
            }

            var t = instance.GetType();
            var member = LookupMember(t, segment);
            if (member == null) return null;
            switch (member) {
                case FieldInfo f: return f.GetValue(instance);
                case PropertyInfo p:
                    if (!p.CanRead) return null;
                    var idx = p.GetIndexParameters();
                    if (idx != null && idx.Length > 0) return null;
                    return p.GetValue(instance);
                default: return null;
            }
        }

        static MemberInfo LookupMember(Type type, string segment) {
            var key = new CacheKey(type, segment);
            // Lock-free hit path against the copy-on-write snapshot.
            if (cache.TryGetValue(key, out var cached)) {
                return cached == MissingSentinel ? null : cached;
            }

            MemberInfo found = null;
            for (var t = type; t != null && found == null; t = t.BaseType) {
                var f = t.GetField(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { found = f; break; }
            }
            if (found == null) {
                for (var t = type; t != null && found == null; t = t.BaseType) {
                    var p = t.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (p != null) { found = p; break; }
                }
            }

            lock (cacheLock) {
                var next = new Dictionary<CacheKey, MemberInfo>(cache) {
                    [key] = found ?? MissingSentinel
                };
                cache = next;
            }
            return found;
        }

        internal static int CacheCount => cache.Count;

        internal static void ClearCacheForTests() {
            lock (cacheLock) {
                cache = new Dictionary<CacheKey, MemberInfo>();
            }
        }

        // Test-only: wipe every direct-accessor and factory registration.
        // Mirrors the ColorResolver.ResetWarnings_TestOnly() convention so
        // test [SetUp] / [TearDown] can return the resolver to a known
        // empty registration state without per-type Unregister churn.
        // Production code should NOT call this — the ModuleInitializer-emitted
        // factory registrations are the only path the generated binding fast
        // path has, so clearing them at runtime breaks data binding.
        internal static void ResetRegistrations_TestOnly() {
            lock (registeredLock) {
                registeredAccessors = new Dictionary<Type, IBindingAccessor>();
                registeredFactories = new Dictionary<Type, Func<object, IBindingAccessor>>();
            }
        }

        // Back-compat alias for ResetRegistrations_TestOnly. Prefer the new
        // name in new tests.
        internal static void ClearRegisteredAccessorsForTests() => ResetRegistrations_TestOnly();
    }
}
