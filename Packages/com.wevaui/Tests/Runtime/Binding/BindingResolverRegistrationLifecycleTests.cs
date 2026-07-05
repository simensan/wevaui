using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;

namespace Weva.Tests.Binding {
    // Regression coverage for MC3 (CODE_AUDIT_FINDINGS.md): BindingResolver
    // exposes Register* entry points for direct accessors and accessor
    // factories, but historically had no Unregister* API at all -- so
    // test scaffolding that registered an accessor inherited the entry
    // into every subsequent case, and dynamic plugin / hot-reload systems
    // had no way to release entries for unloaded types.
    //
    // These tests pin the new public Unregister APIs plus the test-only
    // bulk Reset that mirrors ColorResolver.ResetWarnings_TestOnly().
    public class BindingResolverRegistrationLifecycleTests {
        // Minimal IBindingAccessor stub the lifecycle tests can wrap around
        // an arbitrary controller instance. We don't exercise the generated
        // fast path here -- we just need a non-null sentinel so GetAccessor
        // can return SOMETHING distinguishable from the reflection fallback.
        sealed class StubAccessor : IBindingAccessor {
            public string Tag;
            public int TryGetCallCount;
            readonly object value;

            public StubAccessor(string tag, object value) {
                Tag = tag;
                this.value = value;
            }

            public bool TryGet(string memberName, out object result) {
                TryGetCallCount++;
                if (memberName == "Tag") { result = Tag; return true; }
                if (memberName == "Value") { result = value; return true; }
                result = null;
                return false;
            }
            public bool TrySet(string memberName, object newValue) => false;
            public IReadOnlyList<string> BoundMemberNames => new[] { "Tag", "Value" };
            public IReadOnlyList<ElementBindingDescriptor> ElementBindings => Array.Empty<ElementBindingDescriptor>();
            public bool TrySetElement(string id, object element) => false;
        }

        // A plain CLR class deliberately without IBindingAccessor. Without a
        // registered factory/accessor, BindingResolver falls back to the
        // reflection LookupMember path -- which CAN see the public field.
        // So "factory is gone" is verified by checking the factory itself
        // is not invoked anymore, not by checking the resolve fails.
        public class PlainController {
            public string Reflected = "reflected-value";
        }

        [SetUp]
        public void SetUp() {
            BindingResolver.ClearCacheForTests();
            BindingResolver.ResetRegistrations_TestOnly();
        }

        [TearDown]
        public void TearDown() {
            BindingResolver.ResetRegistrations_TestOnly();
            BindingResolver.ClearCacheForTests();
        }

        // -- UnregisterAccessorFactory ---------------------------------------

        [Test]
        public void UnregisterAccessorFactory_removes_registered_factory() {
            int factoryCalls = 0;
            Func<object, IBindingAccessor> factory = inst => {
                factoryCalls++;
                return new StubAccessor("from-factory", "factory-value");
            };

            BindingResolver.RegisterAccessorFactory(typeof(PlainController), factory);

            // Sanity: factory IS invoked while registered.
            var resolved1 = BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Value"));
            Assert.That(resolved1, Is.EqualTo("factory-value"));
            Assert.That(factoryCalls, Is.EqualTo(1), "factory should have been invoked once while registered");

            // Unregister and confirm the return value reports removal.
            bool removed = BindingResolver.UnregisterAccessorFactory(typeof(PlainController));
            Assert.That(removed, Is.True, "UnregisterAccessorFactory should return true when an entry was removed");

            // After unregister, resolving falls back to reflection -- the
            // factory MUST NOT be invoked again. Use a field the reflection
            // path can see so we can verify that's what fired.
            int beforeReflectionResolve = factoryCalls;
            var resolved2 = BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Reflected"));
            Assert.That(resolved2, Is.EqualTo("reflected-value"), "reflection fallback should fire after unregister");
            Assert.That(factoryCalls, Is.EqualTo(beforeReflectionResolve), "factory MUST NOT be invoked after unregister");
        }

        [Test]
        public void UnregisterAccessorFactory_is_safe_noop_when_no_registration_exists() {
            // No RegisterAccessorFactory call -- the dictionary entry doesn't exist.
            Assert.DoesNotThrow(() => BindingResolver.UnregisterAccessorFactory(typeof(PlainController)));
            bool removed = BindingResolver.UnregisterAccessorFactory(typeof(PlainController));
            Assert.That(removed, Is.False, "UnregisterAccessorFactory should return false when nothing was registered");

            // Calling it twice in a row is also a no-op.
            Assert.DoesNotThrow(() => BindingResolver.UnregisterAccessorFactory(typeof(PlainController)));
        }

        [Test]
        public void UnregisterAccessorFactory_null_type_throws() {
            Assert.Throws<ArgumentNullException>(() => BindingResolver.UnregisterAccessorFactory(null));
        }

        // -- UnregisterAccessor ----------------------------------------------

        [Test]
        public void UnregisterAccessor_removes_registered_direct_accessor() {
            var accessor = new StubAccessor("singleton", "direct-value");
            BindingResolver.RegisterAccessor(typeof(PlainController), accessor);

            // Sanity: direct accessor wins over reflection while registered.
            var resolved1 = BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Tag"));
            Assert.That(resolved1, Is.EqualTo("singleton"));
            Assert.That(accessor.TryGetCallCount, Is.GreaterThanOrEqualTo(1));

            bool removed = BindingResolver.UnregisterAccessor(typeof(PlainController));
            Assert.That(removed, Is.True);

            // After unregister, the registered accessor is gone -- TryGet
            // count MUST NOT advance on subsequent resolves of fields the
            // reflection path handles on its own.
            int beforeReflectionResolve = accessor.TryGetCallCount;
            var resolved2 = BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Reflected"));
            Assert.That(resolved2, Is.EqualTo("reflected-value"));
            Assert.That(accessor.TryGetCallCount, Is.EqualTo(beforeReflectionResolve), "stub accessor must not be consulted after unregister");
        }

        [Test]
        public void UnregisterAccessor_is_safe_noop_when_no_registration_exists() {
            Assert.DoesNotThrow(() => BindingResolver.UnregisterAccessor(typeof(PlainController)));
            bool removed = BindingResolver.UnregisterAccessor(typeof(PlainController));
            Assert.That(removed, Is.False);
        }

        [Test]
        public void UnregisterAccessor_null_type_throws() {
            Assert.Throws<ArgumentNullException>(() => BindingResolver.UnregisterAccessor(null));
        }

        // -- ResetRegistrations_TestOnly -------------------------------------

        [Test]
        public void ResetRegistrations_TestOnly_clears_both_factories_and_direct_accessors() {
            int factoryCalls = 0;
            var directAccessor = new StubAccessor("direct", "x");

            BindingResolver.RegisterAccessor(typeof(PlainController), directAccessor);
            BindingResolver.RegisterAccessorFactory(typeof(string), inst => {
                factoryCalls++;
                return new StubAccessor("string-fac", inst);
            });

            // Sanity: both are live.
            Assert.That(BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Tag")), Is.EqualTo("direct"));
            Assert.That(BindingResolver.Resolve("hello", BindingPath.Parse("Tag")), Is.EqualTo("string-fac"));
            Assert.That(factoryCalls, Is.GreaterThanOrEqualTo(1));

            // Reset everything in one shot -- the canonical [SetUp] hook.
            BindingResolver.ResetRegistrations_TestOnly();

            // Direct accessor TryGet count must not advance.
            int beforeReset = directAccessor.TryGetCallCount;
            BindingResolver.Resolve(new PlainController(), BindingPath.Parse("Reflected"));
            Assert.That(directAccessor.TryGetCallCount, Is.EqualTo(beforeReset), "direct accessor must be gone after Reset");

            // Factory must not fire again either. Resolving a string against
            // a non-existent member exercises the GetAccessor path; with no
            // factory registered, GetAccessor returns null and no factory call happens.
            int factoryBeforeReset = factoryCalls;
            BindingResolver.Resolve("hello", BindingPath.Parse("Length"));
            Assert.That(factoryCalls, Is.EqualTo(factoryBeforeReset), "factory must be gone after Reset");
        }

        [Test]
        public void ResetRegistrations_TestOnly_is_idempotent_on_empty_state() {
            // Brand-new SetUp state -- calling Reset multiple times must not throw.
            Assert.DoesNotThrow(() => BindingResolver.ResetRegistrations_TestOnly());
            Assert.DoesNotThrow(() => BindingResolver.ResetRegistrations_TestOnly());
        }
    }
}
