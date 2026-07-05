using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Weva.HotReload {
    // Thread-safe queue of pending CSS file paths whose contents have changed
    // on disk and need to be re-parsed and re-applied to the cascade.
    //
    // Producers: CssWatcher's FileSystemWatcher.Changed callback (off-thread).
    // Consumer:  HotReloadCoordinator.Drain on the main thread, once per frame.
    //
    // Paths are deduplicated within a drain window: FileSystemWatcher emits
    // multiple events per save on Windows (the editor writes the file twice
    // — once with size, once with timestamp), and we only need to re-parse
    // once per frame. The internal HashSet tracks "already-queued" without
    // unbounded growth — Drain() clears it.
    public sealed class CssReloadQueue {
        readonly ConcurrentQueue<string> queue = new();
        readonly HashSet<string> seen = new();
        readonly object seenLock = new();

        public int Count => queue.Count;

        public void Enqueue(string path) {
            if (string.IsNullOrEmpty(path)) return;
            // Hold the lock across BOTH `seen.Add` AND `queue.Enqueue` so a
            // concurrent Drain() can't slip in between, clear `seen`, and
            // then let a subsequent Enqueue duplicate the same path. The
            // queue is also serialized inside the lock — it's still a
            // ConcurrentQueue underneath, but the lock guarantees the
            // (seen, queue) pair stays atomic against Drain.
            lock (seenLock) {
                if (!seen.Add(path)) return;
                queue.Enqueue(path);
            }
        }

        // Drains all currently pending paths into a list, clearing the
        // dedup set so subsequent saves of the same file re-enqueue. Returns
        // an empty list if the queue is empty (allocation-free fast path).
        // Held under `seenLock` to atomically swap (queue, seen) against
        // any concurrent Enqueue producers.
        public List<string> Drain() {
            lock (seenLock) {
                if (queue.IsEmpty) {
                    if (seen.Count > 0) seen.Clear();
                    return new List<string>();
                }
                var result = new List<string>();
                while (queue.TryDequeue(out var p)) {
                    result.Add(p);
                }
                seen.Clear();
                return result;
            }
        }

        public void Clear() {
            lock (seenLock) {
                while (queue.TryDequeue(out _)) { }
                seen.Clear();
            }
        }

    }
}
