using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;

namespace Weva.Tests.Binding.Generated {
    // These tests verify BindingResolver picks the generated IBindingAccessor
    // path when available, and falls back to reflection otherwise. We don't
    // run the actual Roslyn generator here -- we hand-roll the partial class
    // exactly as the generator would emit it. Once the generator DLL is in
    // place these classes' [UIBind] attributes alone would produce
    // equivalent code.
    public class HandwrittenAccessorIntegrationTests {
        public partial class GeneratedController {
            [UIBind] public int CoinCount = 100;
            [UIBind] public string Name = "alice";
        }

        public sealed partial class GeneratedController : IBindingAccessor {
            static readonly string[] __Names = new[] { "CoinCount", "Name" };
            static readonly ElementBindingDescriptor[] __Elements = Array.Empty<ElementBindingDescriptor>();

            public int TryGetCallCount;

            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => __Names;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings => __Elements;

            bool IBindingAccessor.TryGet(string memberName, out object value) {
                TryGetCallCount++;
                switch (memberName) {
                    case "CoinCount": value = (object)CoinCount; return true;
                    case "Name": value = (object)Name; return true;
                    default: value = null; return false;
                }
            }
            bool IBindingAccessor.TrySet(string memberName, object value) {
                switch (memberName) {
                    case "CoinCount": if (value is int v0) { CoinCount = v0; return true; } return false;
                    case "Name": if (value is null || value is string) { Name = (string)value; return true; } return false;
                    default: return false;
                }
            }
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        // Plain reflection-only controller; deliberately does not implement
        // IBindingAccessor.
        public class ReflectedController {
            public int Health = 50;
            public string Title = "default";
        }

        [SetUp]
        public void SetUp() {
            BindingResolver.ClearCacheForTests();
            BindingResolver.ClearRegisteredAccessorsForTests();
        }

        [Test]
        public void BindingResolver_uses_generated_path_when_available() {
            var c = new GeneratedController { CoinCount = 999 };
            var v = BindingResolver.Resolve(c, BindingPath.Parse("CoinCount"));
            Assert.That(v, Is.EqualTo(999));
            // Generated path increments TryGetCallCount; reflection path would not.
            Assert.That(c.TryGetCallCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void BindingResolver_falls_back_to_reflection_when_no_accessor() {
            var c = new ReflectedController { Health = 77 };
            var v = BindingResolver.Resolve(c, BindingPath.Parse("Health"));
            Assert.That(v, Is.EqualTo(77));
            // Reflection path populates the member cache; generated path does not.
            Assert.That(BindingResolver.CacheCount, Is.GreaterThan(0));
        }

        [Test]
        public void Mixed_controllers_each_use_their_own_path() {
            var gen = new GeneratedController { CoinCount = 1, Name = "g" };
            var refl = new ReflectedController { Health = 2, Title = "r" };

            Assert.That(BindingResolver.Resolve(gen, BindingPath.Parse("CoinCount")), Is.EqualTo(1));
            int genCalls = gen.TryGetCallCount;
            Assert.That(BindingResolver.Resolve(refl, BindingPath.Parse("Health")), Is.EqualTo(2));
            Assert.That(BindingResolver.Resolve(refl, BindingPath.Parse("Title")), Is.EqualTo("r"));
            // Generated controller TryGet count unchanged by resolving the reflected one.
            Assert.That(gen.TryGetCallCount, Is.EqualTo(genCalls));
        }

        [Test]
        public void Switching_controller_instance_switches_accessor_targets() {
            // Simulates WevaDocument.SetController swapping the bound controller.
            // Each instance carries its own generated accessor state, so
            // resolving against a new instance reads the new instance's data.
            var first = new GeneratedController { CoinCount = 11 };
            var second = new GeneratedController { CoinCount = 22 };

            Assert.That(BindingResolver.Resolve(first, BindingPath.Parse("CoinCount")), Is.EqualTo(11));
            Assert.That(BindingResolver.Resolve(second, BindingPath.Parse("CoinCount")), Is.EqualTo(22));

            // Mutating second must not alter first.
            second.CoinCount = 99;
            Assert.That(BindingResolver.Resolve(first, BindingPath.Parse("CoinCount")), Is.EqualTo(11));
            Assert.That(BindingResolver.Resolve(second, BindingPath.Parse("CoinCount")), Is.EqualTo(99));
        }

        [Test]
        public void GetAccessor_returns_self_when_implements_interface() {
            var c = new GeneratedController();
            var a = BindingResolver.GetAccessor(c);
            Assert.That(a, Is.SameAs(c));
        }

        [Test]
        public void GetAccessor_returns_null_for_reflected_controller() {
            var c = new ReflectedController();
            var a = BindingResolver.GetAccessor(c);
            Assert.That(a, Is.Null);
        }

        [Test]
        public void RegisterAccessor_for_sealed_nonpartial_type() {
            // Simulates the standalone (non-partial) emission path: the
            // generator's [ModuleInitializer] would call RegisterAccessor
            // with a wrapper accessor instance. Here we register a stand-in
            // and verify the runtime picks it up.
            var sealedInstance = new SealedNonPartialController { Score = 7 };
            BindingResolver.RegisterAccessor(typeof(SealedNonPartialController), new SealedNonPartialAccessor(sealedInstance));

            var v = BindingResolver.Resolve(sealedInstance, BindingPath.Parse("Score"));
            Assert.That(v, Is.EqualTo(7));
        }

        public sealed class SealedNonPartialController {
            [UIBind] public int Score;
        }

        sealed class SealedNonPartialAccessor : IBindingAccessor {
            readonly SealedNonPartialController target;
            public SealedNonPartialAccessor(SealedNonPartialController t) { target = t; }
            public bool TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "Score": value = (object)target.Score; return true;
                    default: value = null; return false;
                }
            }
            public bool TrySet(string memberName, object value) {
                switch (memberName) {
                    case "Score": if (value is int v0) { target.Score = v0; return true; } return false;
                    default: return false;
                }
            }
            public IReadOnlyList<string> BoundMemberNames => new[] { "Score" };
            public IReadOnlyList<ElementBindingDescriptor> ElementBindings => Array.Empty<ElementBindingDescriptor>();
            public bool TrySetElement(string id, object element) => false;
        }
    }
}
