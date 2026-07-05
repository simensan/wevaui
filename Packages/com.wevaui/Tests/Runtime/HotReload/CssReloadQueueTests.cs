using System.Threading;
using NUnit.Framework;
using Weva.HotReload;

namespace Weva.Tests.HotReload {
    public class CssReloadQueueTests {
        [Test]
        public void Enqueue_then_drain_returns_paths_in_fifo_order() {
            var q = new CssReloadQueue();
            q.Enqueue("a");
            q.Enqueue("b");
            q.Enqueue("c");
            var drained = q.Drain();
            Assert.That(drained, Is.EqualTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void Enqueue_dedupes_within_a_drain_window() {
            var q = new CssReloadQueue();
            q.Enqueue("a");
            q.Enqueue("a");
            q.Enqueue("a");
            var drained = q.Drain();
            Assert.That(drained, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void Enqueue_after_drain_re_dedupes_independently() {
            var q = new CssReloadQueue();
            q.Enqueue("a");
            q.Drain();
            q.Enqueue("a");
            q.Enqueue("b");
            var drained = q.Drain();
            Assert.That(drained, Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Empty_drain_returns_empty_list_and_does_not_throw() {
            var q = new CssReloadQueue();
            Assert.That(q.Drain(), Is.Empty);
            Assert.That(q.Count, Is.EqualTo(0));
        }

        [Test]
        public void Concurrent_enqueue_does_not_lose_paths() {
            // The FileSystemWatcher fires from a worker thread; the
            // dedup logic must not lose entries under contention.
            var q = new CssReloadQueue();
            int n = 1000;
            var t1 = new Thread(() => { for (int i = 0; i < n; i++) q.Enqueue($"a{i}"); });
            var t2 = new Thread(() => { for (int i = 0; i < n; i++) q.Enqueue($"b{i}"); });
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();
            var drained = q.Drain();
            Assert.That(drained.Count, Is.EqualTo(n * 2));
        }

        [Test]
        public void Enqueue_null_or_empty_is_ignored() {
            var q = new CssReloadQueue();
            q.Enqueue(null);
            q.Enqueue(string.Empty);
            Assert.That(q.Count, Is.EqualTo(0));
        }

        [Test]
        public void Clear_resets_internal_state() {
            var q = new CssReloadQueue();
            q.Enqueue("a");
            q.Enqueue("b");
            q.Clear();
            Assert.That(q.Count, Is.EqualTo(0));
            // After clear, the same path can be re-enqueued.
            q.Enqueue("a");
            Assert.That(q.Drain(), Is.EqualTo(new[] { "a" }));
        }
    }
}
