using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;

namespace Weva.Tests.Binding {
    // The resolver's registration/member-lookup maps are copy-on-write:
    // readers take immutable snapshots without locking (the per-frame poll
    // path), writers clone-and-swap. These tests pin that concurrent reads
    // during registration churn never throw or observe torn state.
    public class BindingResolverConcurrencyTests {
        sealed class CtxA { public string Name = "A"; }
        sealed class CtxB { public string Name = "B"; }

        sealed class FixedAccessor : IBindingAccessor {
            readonly string value;
            public FixedAccessor(string value) { this.value = value; }

            static readonly string[] members = { "Name" };
            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => members;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings =>
                Array.Empty<ElementBindingDescriptor>();

            bool IBindingAccessor.TryGet(string memberName, out object v) {
                if (memberName == "Name") { v = value; return true; }
                v = null;
                return false;
            }

            bool IBindingAccessor.TrySet(string memberName, object value) => false;
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        [TearDown]
        public void TearDown() {
            BindingResolver.UnregisterAccessor(typeof(CtxA));
            BindingResolver.UnregisterAccessor(typeof(CtxB));
        }

        [Test]
        public void Concurrent_resolves_during_registration_churn_do_not_throw() {
            var path = BindingPath.Parse("Name");
            var ctxA = new CtxA();
            var ctxB = new CtxB();
            Exception readerError = null;
            using var stop = new ManualResetEventSlim(false);

            var readers = new Thread[4];
            for (int t = 0; t < readers.Length; t++) {
                readers[t] = new Thread(() => {
                    try {
                        while (!stop.IsSet) {
                            BindingResolver.TryResolve(ctxA, path, out var a);
                            // Reflection fallback says "A"; a registered
                            // accessor may say "A-reg". Both are valid mid-churn.
                            if (!Equals(a, "A") && !Equals(a, "A-reg")) {
                                throw new InvalidOperationException($"torn read: {a ?? "null"}");
                            }
                            BindingResolver.TryResolve(ctxB, path, out var b);
                            if (!Equals(b, "B")) {
                                throw new InvalidOperationException($"torn read: {b ?? "null"}");
                            }
                        }
                    } catch (Exception ex) {
                        Interlocked.CompareExchange(ref readerError, ex, null);
                    }
                });
                readers[t].Start();
            }

            // Churn the accessor registration for CtxA while readers resolve.
            var accessor = new FixedAccessor("A-reg");
            for (int i = 0; i < 2000; i++) {
                BindingResolver.RegisterAccessor(typeof(CtxA), accessor);
                BindingResolver.UnregisterAccessor(typeof(CtxA));
            }

            stop.Set();
            foreach (var r in readers) r.Join();
            Assert.That(readerError, Is.Null,
                $"reader observed an error: {readerError}");
        }

        [Test]
        public void Registration_is_visible_to_subsequent_resolves() {
            var path = BindingPath.Parse("Name");
            BindingResolver.RegisterAccessor(typeof(CtxA), new FixedAccessor("registered"));
            BindingResolver.TryResolve(new CtxA(), path, out var value);
            Assert.That(value, Is.EqualTo("registered"));

            Assert.That(BindingResolver.UnregisterAccessor(typeof(CtxA)), Is.True);
            Assert.That(BindingResolver.UnregisterAccessor(typeof(CtxA)), Is.False,
                "second unregister must report nothing was removed");

            BindingResolver.TryResolve(new CtxA(), path, out var fallback);
            Assert.That(fallback, Is.EqualTo("A"), "reflection fallback after unregister");
        }
    }
}
