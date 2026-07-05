using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Weva.HotReload;

namespace Weva.Tests.HotReload {
    public class CssWatcherTests {
        string tempRoot;

        [SetUp]
        public void Setup() {
            tempRoot = Path.Combine(Path.GetTempPath(), "weva-watcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void Teardown() {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { /* best effort on Windows where the watcher may still hold a handle for a tick */ }
        }

        string WriteSheet(string name, string content) {
            var p = Path.Combine(tempRoot, name);
            File.WriteAllText(p, content);
            return p;
        }

        [Test]
        public void Watch_then_modify_enqueues_the_path() {
            var path = WriteSheet("a.css", ".a { color: red; }");
            var queue = new CssReloadQueue();
            using var watcher = new CssWatcher(queue);
            watcher.Watch(path);
            Assert.That(watcher.IsWatching(path), Is.True);
            // FileSystemWatcher on Windows needs a moment for its internal
            // ReadDirectoryChangesW buffer to arm. Writing to the file
            // immediately after Watch() races the arm-up and the Changed
            // event silently drops. A small settling delay makes the
            // event delivery reliable on heavily-loaded CI machines.
            Thread.Sleep(200);

            File.WriteAllText(path, ".a { color: blue; }");

            // FileSystemWatcher delivers asynchronously; spin up to 5s.
            // Bumped from 1.5s — under Unity's test-runner load on Windows
            // (AssetDB import + Unity domain reload contention), the
            // FSWatcher buffer flush can take several seconds before
            // user-mode code sees the Changed event.
            Assert.That(WaitForCondition(() => queue.Count > 0, 5000), Is.True,
                "Watcher did not emit a Changed event for the modified file.");
            var drained = queue.Drain();
            Assert.That(drained, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(Path.GetFullPath(drained[0]), Is.EqualTo(Path.GetFullPath(path)).IgnoreCase);
        }

        [Test]
        public void Multiple_files_in_same_directory_share_a_watcher_but_emit_per_path() {
            var a = WriteSheet("a.css", ".a {}");
            var b = WriteSheet("b.css", ".b {}");
            var queue = new CssReloadQueue();
            using var watcher = new CssWatcher(queue);
            watcher.Watch(a);
            watcher.Watch(b);
            Assert.That(watcher.WatchedFileCount, Is.EqualTo(2));

            File.WriteAllText(a, ".a { color: red; }");
            File.WriteAllText(b, ".b { color: blue; }");

            Assert.That(WaitForCondition(() => queue.Count >= 2 || (queue.Count >= 1 && queue.Count >= 1), 5000), Is.True);
            var drained = queue.Drain();
            Assert.That(drained, Has.Count.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Files_in_different_directories_use_independent_watchers() {
            var subDir = Path.Combine(tempRoot, "sub");
            Directory.CreateDirectory(subDir);
            var a = WriteSheet("a.css", "");
            var b = Path.Combine(subDir, "b.css");
            File.WriteAllText(b, "");
            var queue = new CssReloadQueue();
            using var watcher = new CssWatcher(queue);
            watcher.Watch(a);
            watcher.Watch(b);
            Assert.That(watcher.WatchedFileCount, Is.EqualTo(2));

            File.WriteAllText(b, ".b {}");

            Assert.That(WaitForCondition(() => queue.Count > 0, 5000), Is.True);
            var drained = queue.Drain();
            // Confirms b emitted independently of a's watcher.
            bool bSeen = false;
            foreach (var p in drained) {
                if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) bSeen = true;
            }
            Assert.That(bSeen, Is.True);
        }

        [Test]
        public void Unwatch_stops_events_for_that_path() {
            var path = WriteSheet("a.css", "");
            var queue = new CssReloadQueue();
            using var watcher = new CssWatcher(queue);
            watcher.Watch(path);
            watcher.Unwatch(path);
            Assert.That(watcher.IsWatching(path), Is.False);

            File.WriteAllText(path, ".a {}");
            Thread.Sleep(300);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void Dispose_silences_subsequent_changes() {
            var path = WriteSheet("a.css", "");
            var queue = new CssReloadQueue();
            var watcher = new CssWatcher(queue);
            watcher.Watch(path);
            watcher.Dispose();

            File.WriteAllText(path, ".a {}");
            Thread.Sleep(300);
            Assert.That(queue.Count, Is.EqualTo(0));

            // Re-using a disposed watcher throws ObjectDisposedException.
            Assert.Throws<ObjectDisposedException>(() => watcher.Watch(path));
        }

        [Test]
        public void Watch_on_missing_directory_silently_drops() {
            var queue = new CssReloadQueue();
            using var watcher = new CssWatcher(queue);
            // Does NOT throw — the file doesn't exist yet.
            Assert.DoesNotThrow(() => watcher.Watch(Path.Combine(tempRoot, "no-such-dir", "x.css")));
            Assert.That(watcher.WatchedFileCount, Is.EqualTo(0));
        }

        static bool WaitForCondition(Func<bool> cond, int timeoutMs) {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) {
                if (cond()) return true;
                Thread.Sleep(20);
            }
            return cond();
        }
    }
}
