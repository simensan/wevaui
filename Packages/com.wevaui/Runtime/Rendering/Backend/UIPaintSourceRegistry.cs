using System;
using System.Collections.Generic;

namespace Weva.Rendering {
    public static class UIPaintSourceRegistry {
        static readonly List<IUIPaintSource> sources = new List<IUIPaintSource>();
        static readonly object gate = new object();
        static int version;

        public static void Register(IUIPaintSource source) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            lock (gate) {
                if (!sources.Contains(source)) {
                    sources.Add(source);
                    version++;
                }
            }
        }

        public static void Unregister(IUIPaintSource source) {
            if (source == null) return;
            lock (gate) {
                if (sources.Remove(source)) version++;
            }
        }

        public static IReadOnlyList<IUIPaintSource> Snapshot() {
            lock (gate) {
                var copy = new List<IUIPaintSource>(sources.Count);
                copy.AddRange(sources);
                copy.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                return copy;
            }
        }

        // Allocation-free overload. Fills `output` with the current source set
        // (cleared first) sorted by Order. Used by the per-frame render path
        // (UIRenderGraphPass.EmitAllPaintSources) which previously allocated
        // a fresh List<> every Snapshot() call.
        public static void SnapshotInto(List<IUIPaintSource> output) {
            if (output == null) return;
            lock (gate) {
                output.Clear();
                for (int i = 0; i < sources.Count; i++) output.Add(sources[i]);
                output.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            }
        }

        public static int Count {
            get {
                lock (gate) {
                    return sources.Count;
                }
            }
        }

        public static int Version {
            get {
                lock (gate) {
                    return version;
                }
            }
        }

        public static void Clear() {
            lock (gate) {
                if (sources.Count > 0) version++;
                sources.Clear();
            }
        }
    }
}
