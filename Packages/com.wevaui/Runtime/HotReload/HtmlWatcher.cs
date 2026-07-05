using System;
using System.Collections.Generic;
using System.IO;

namespace Weva.HotReload {
    // Watches a set of HTML file paths via FileSystemWatcher. On any
    // Changed/Created/Renamed event the affected path is enqueued onto the
    // shared HtmlReloadQueue for the main-thread coordinator to drain.
    //
    // Mirrors CssWatcher line for line. The two are separate types so the
    // queue identities cannot get crossed at the call site (a CssReloadQueue
    // can never accidentally receive an HTML path or vice versa).
    public sealed class HtmlWatcher : IDisposable {
        readonly HtmlReloadQueue queue;
        readonly Dictionary<string, FileSystemWatcher> watchersByDirectory = new();
        readonly HashSet<string> watchedFiles = new(StringComparer.OrdinalIgnoreCase);
        readonly object lockObj = new();
        bool disposed;

        public HtmlWatcher(HtmlReloadQueue queue) {
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
            if (disposed) throw new ObjectDisposedException(nameof(HtmlWatcher));
            var normalized = NormalizePath(fullPath);
            string dir = Path.GetDirectoryName(normalized);
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) return;

            lock (lockObj) {
                if (!watchedFiles.Add(normalized)) return;
                if (!watchersByDirectory.ContainsKey(dir)) {
                    var w = new FileSystemWatcher(dir) {
                        IncludeSubdirectories = false,
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
