using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Weva.HotReload {
    // Thread-safe queue of pending HTML file paths whose contents have changed
    // on disk and need to be re-parsed and diffed against the live DOM.
    //
    // Mirrors CssReloadQueue exactly. Producers: HtmlWatcher's FileSystemWatcher
    // callback (off-thread). Consumer: HtmlReloadCoordinator.Drain on the main
    // thread, once per frame.
    //
    // Paths are deduplicated within a drain window so atomic-save double-fires
    // collapse to a single reparse.
    public sealed class HtmlReloadQueue {
        readonly ConcurrentQueue<string> queue = new();
        readonly HashSet<string> seen = new();
        readonly object seenLock = new();

        public int Count => queue.Count;

        public void Enqueue(string path) {
            if (string.IsNullOrEmpty(path)) return;
            lock (seenLock) {
                if (!seen.Add(path)) return;
            }
            queue.Enqueue(path);
        }

        public List<string> Drain() {
            if (queue.IsEmpty) {
                lock (seenLock) {
                    if (seen.Count > 0) seen.Clear();
                }
                return new List<string>();
            }
            var result = new List<string>();
            while (queue.TryDequeue(out var p)) {
                result.Add(p);
            }
            lock (seenLock) {
                seen.Clear();
            }
            return result;
        }

        public void Clear() {
            while (queue.TryDequeue(out _)) { }
            lock (seenLock) {
                seen.Clear();
            }
        }
    }
}
