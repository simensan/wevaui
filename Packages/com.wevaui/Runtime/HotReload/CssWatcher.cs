using System;
using System.Collections.Generic;
using System.IO;

namespace Weva.HotReload {
    // Watches a set of stylesheet file paths via FileSystemWatcher. On any
    // Changed/Created/Renamed event the affected path is enqueued onto the
    // shared CssReloadQueue for the main-thread coordinator to drain.
    //
    // FileSystemWatcher quirks worth knowing:
    //   - On Windows the watcher fires per-directory, not per-file. We
    //     create one watcher per unique parent directory and filter by
    //     filename in the event handler.
    //   - Saves frequently emit two Changed events (size + timestamp) and
    //     editors that write atomically (write tmp + rename) emit Renamed
    //     instead of Changed. We listen for both and rely on the queue's
    //     dedup set to collapse duplicates within a drain window.
    //   - The 50ms debounce in HotReloadCoordinator.Tick covers the
    //     short-window double-fire that the dedup set alone cannot handle
    //     when drains happen between the two events.
    //   - The watcher is disposable; calling Dispose stops events
    //     immediately and the underlying native handle is released. The
    //     queue may still contain paths enqueued before disposal — that is
    //     intentional, the next drain will process them.
    public sealed class CssWatcher : IDisposable {
        readonly CssReloadQueue queue;
        readonly Dictionary<string, FileSystemWatcher> watchersByDirectory = new();
        readonly HashSet<string> watchedFiles = new(StringComparer.OrdinalIgnoreCase);
        readonly object lockObj = new();
        bool disposed;

        public CssWatcher(CssReloadQueue queue) {
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        }

        public int WatchedFileCount {
            get {
                lock (lockObj) {
                    return watchedFiles.Count;
                }
            }
        }

        public bool IsWatching(string fullPath) {
            if (string.IsNullOrEmpty(fullPath)) return false;
            var n = NormalizePath(fullPath);
            lock (lockObj) {
                return watchedFiles.Contains(n);
            }
        }

        public void Watch(string fullPath) {
            if (string.IsNullOrEmpty(fullPath)) return;
            if (disposed) throw new ObjectDisposedException(nameof(CssWatcher));
            var normalized = NormalizePath(fullPath);
            string dir = Path.GetDirectoryName(normalized);
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) return;

            lock (lockObj) {
                if (!watchedFiles.Add(normalized)) return;
                if (!watchersByDirectory.ContainsKey(dir)) {
                    var w = new FileSystemWatcher(dir) {
                        IncludeSubdirectories = false,
                        // We listen for write/rename/create. Filtering by
                        // filename happens inside the handler so multiple
                        // files in the same directory share a watcher.
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    w.Changed += OnFsEvent;
                    w.Created += OnFsEvent;
                    w.Renamed += OnFsRenamed;
                    watchersByDirectory.Add(dir, w);
                }
            }
        }

        public void Unwatch(string fullPath) {
            if (string.IsNullOrEmpty(fullPath)) return;
            var normalized = NormalizePath(fullPath);
            string dir = Path.GetDirectoryName(normalized);
            lock (lockObj) {
                if (!watchedFiles.Remove(normalized)) return;
                if (string.IsNullOrEmpty(dir)) return;
                bool stillUsed = false;
                foreach (var f in watchedFiles) {
                    if (string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)) {
                        stillUsed = true;
                        break;
                    }
                }
                if (!stillUsed && watchersByDirectory.TryGetValue(dir, out var w)) {
                    w.EnableRaisingEvents = false;
                    w.Changed -= OnFsEvent;
                    w.Created -= OnFsEvent;
                    w.Renamed -= OnFsRenamed;
                    w.Dispose();
                    watchersByDirectory.Remove(dir);
                }
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            lock (lockObj) {
                foreach (var w in watchersByDirectory.Values) {
                    w.EnableRaisingEvents = false;
                    w.Changed -= OnFsEvent;
                    w.Created -= OnFsEvent;
                    w.Renamed -= OnFsRenamed;
                    w.Dispose();
                }
                watchersByDirectory.Clear();
                watchedFiles.Clear();
            }
        }

        void OnFsEvent(object sender, FileSystemEventArgs e) {
            // Off-thread; only safe operations are reads of immutable state
            // and queue.Enqueue.
            if (e == null || string.IsNullOrEmpty(e.FullPath)) return;
            var normalized = NormalizePath(e.FullPath);
            bool matches;
            lock (lockObj) {
                matches = watchedFiles.Contains(normalized);
            }
            if (matches) {
                queue.Enqueue(normalized);
            }
        }

        void OnFsRenamed(object sender, RenamedEventArgs e) {
            if (e == null) return;
            // Atomic-save pattern: editor writes a temp file then renames it
            // to overwrite the target. The "new" full path is what we care
            // about — that's what now contains the new content.
            if (!string.IsNullOrEmpty(e.FullPath)) {
                var normalized = NormalizePath(e.FullPath);
                bool matches;
                lock (lockObj) {
                    matches = watchedFiles.Contains(normalized);
                }
                if (matches) queue.Enqueue(normalized);
            }
        }

        static string NormalizePath(string p) {
            try {
                return Path.GetFullPath(p);
            } catch {
                return p;
            }
        }
    }
}
