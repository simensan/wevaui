using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Binding;
using Weva.Binding.Generated;
using Weva.Dom;

namespace Weva.Tests.Binding.Generated {
    // These tests exercise the IBindingAccessor contract using hand-rolled
    // accessors that mirror exactly what UIBindGenerator emits. Once the
    // generator DLL is built and dropped into Editor/Generators these
    // controllers' attributes will produce equivalent code automatically.
    public class IBindingAccessorTests {
        public struct Pair {
            public int A;
            public int B;
            public Pair(int a, int b) { A = a; B = b; }
            public override string ToString() => $"{A},{B}";
        }

        public partial class SimpleController {
            [UIBind] public int CoinCount = 100;
            [UIBind] public string Name = "alice";
            [UIBind] public bool Active = true;
            [UIBind] Pair Coords = new Pair(3, 4);
            [UIElement("start-button")] public Element StartButton;

            public Pair ReadCoords() => Coords;
            public void WriteCoords(Pair p) => Coords = p;
        }

        public sealed partial class SimpleController : IBindingAccessor {
            static readonly string[] __Weva_BoundMemberNames = new[] { "CoinCount", "Name", "Active", "Coords" };
            static readonly ElementBindingDescriptor[] __Weva_ElementBindings = new[] {
                new ElementBindingDescriptor("start-button", typeof(Element)),
            };

            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => __Weva_BoundMemberNames;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings => __Weva_ElementBindings;

            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "CoinCount": value = (object)CoinCount; return true;
                    case "Name": value = (object)Name; return true;
                    case "Active": value = (object)Active; return true;
                    case "Coords": value = (object)Coords; return true;
                    default: value = null; return false;
                }
            }

            bool IBindingAccessor.TrySet(string memberName, object value) {
                switch (memberName) {
                    case "CoinCount": if (value is int v0) { CoinCount = v0; return true; } return false;
                    case "Name": if (value is null || value is string) { Name = (string)value; return true; } return false;
                    case "Active": if (value is bool v2) { Active = v2; return true; } return false;
                    case "Coords": if (value is Pair v3) { Coords = v3; return true; } return false;
                    default: return false;
                }
            }

            bool IBindingAccessor.TrySetElement(string id, object element) {
                switch (id) {
                    case "start-button": if (element is null || element is Element) { StartButton = (Element)element; return true; } return false;
                    default: return false;
                }
            }
        }

        // Controller with a private [UIBind] field, exercising the "private
        // member accessible" contract.
        public sealed partial class PrivateBindController : IBindingAccessor {
            [UIBind] int hidden = 42;

            public int ReadHidden() => hidden;
            public void WriteHidden(int v) { hidden = v; }

            static readonly string[] __Weva_BoundMemberNames = new[] { "hidden" };
            static readonly ElementBindingDescriptor[] __Weva_ElementBindings = Array.Empty<ElementBindingDescriptor>();
            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => __Weva_BoundMemberNames;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings => __Weva_ElementBindings;
            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "hidden": value = (object)hidden; return true;
                    default: value = null; return false;
                }
            }
            bool IBindingAccessor.TrySet(string memberName, object value) {
                switch (memberName) {
                    case "hidden": if (value is int v0) { hidden = v0; return true; } return false;
                    default: return false;
                }
            }
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        // Controller exercising inheritance: a [UIBind] declared in BASE.
        // Per design the generator only emits for the declaring type, so the
        // derived class' generated accessor lists *only* its own members and
        // BindingResolver's reflection fallback covers inherited ones.
        public class BaseWithBind {
            [UIBind] public int FromBase = 7;
        }

        public sealed partial class DerivedController : BaseWithBind, IBindingAccessor {
            [UIBind] public string FromDerived = "derived";

            static readonly string[] __Weva_BoundMemberNames = new[] { "FromDerived" };
            static readonly ElementBindingDescriptor[] __Weva_ElementBindings = Array.Empty<ElementBindingDescriptor>();
            IReadOnlyList<string> IBindingAccessor.BoundMemberNames => __Weva_BoundMemberNames;
            IReadOnlyList<ElementBindingDescriptor> IBindingAccessor.ElementBindings => __Weva_ElementBindings;
            bool IBindingAccessor.TryGet(string memberName, out object value) {
                switch (memberName) {
                    case "FromDerived": value = (object)FromDerived; return true;
                    default: value = null; return false;
                }
            }
            bool IBindingAccessor.TrySet(string memberName, object value) {
                switch (memberName) {
                    case "FromDerived": if (value is null || value is string) { FromDerived = (string)value; return true; } return false;
                    default: return false;
                }
            }
            bool IBindingAccessor.TrySetElement(string id, object element) => false;
        }

        IBindingAccessor As(SimpleController c) => c;
        IBindingAccessor As(PrivateBindController c) => c;
        IBindingAccessor As(DerivedController c) => c;

        [Test]
        public void TryGet_returns_boxed_int() {
            var c = new SimpleController { CoinCount = 1234 };
            var ok = As(c).TryGet("CoinCount", out var v);
            Assert.That(ok, Is.True);
            Assert.That(v, Is.EqualTo(1234));
            Assert.That(v, Is.TypeOf<int>());
        }

        [Test]
        public void TryGet_returns_false_for_unknown_name() {
            var c = new SimpleController();
            var ok = As(c).TryGet("Nope", out var v);
            Assert.That(ok, Is.False);
            Assert.That(v, Is.Null);
        }

        [Test]
        public void TrySet_writes_int() {
            var c = new SimpleController();
            var ok = As(c).TrySet("CoinCount", 7);
            Assert.That(ok, Is.True);
            Assert.That(c.CoinCount, Is.EqualTo(7));
        }

        [Test]
        public void TrySet_returns_false_for_type_mismatch() {
            var c = new SimpleController { CoinCount = 9 };
            var ok = As(c).TrySet("CoinCount", "not-an-int");
            Assert.That(ok, Is.False);
            Assert.That(c.CoinCount, Is.EqualTo(9));
        }

        [Test]
        public void TrySet_returns_false_for_unknown_name() {
            var c = new SimpleController();
            var ok = As(c).TrySet("Nope", 1);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void BoundMemberNames_lists_every_UIBind_member() {
            var c = new SimpleController();
            CollectionAssert.AreEquivalent(
                new[] { "CoinCount", "Name", "Active", "Coords" },
                As(c).BoundMemberNames);
        }

        [Test]
        public void ElementBindings_lists_every_UIElement() {
            var c = new SimpleController();
            var bindings = As(c).ElementBindings;
            Assert.That(bindings.Count, Is.EqualTo(1));
            Assert.That(bindings[0].Id, Is.EqualTo("start-button"));
            Assert.That(bindings[0].Expected, Is.EqualTo(typeof(Element)));
        }

        [Test]
        public void TrySetElement_assigns_field() {
            var c = new SimpleController();
            var btn = new Element("button");
            btn.SetAttribute("id", "start-button");
            var ok = As(c).TrySetElement("start-button", btn);
            Assert.That(ok, Is.True);
            Assert.That(c.StartButton, Is.SameAs(btn));
        }

        [Test]
        public void TrySetElement_returns_false_for_unknown_id() {
            var c = new SimpleController();
            var ok = As(c).TrySetElement("nope", new Element("div"));
            Assert.That(ok, Is.False);
        }

        [Test]
        public void RoundTrip_string() {
            var c = new SimpleController();
            Assert.That(As(c).TrySet("Name", "bob"), Is.True);
            Assert.That(As(c).TryGet("Name", out var v), Is.True);
            Assert.That(v, Is.EqualTo("bob"));
        }

        [Test]
        public void RoundTrip_bool() {
            var c = new SimpleController { Active = true };
            Assert.That(As(c).TrySet("Active", false), Is.True);
            Assert.That(As(c).TryGet("Active", out var v), Is.True);
            Assert.That(v, Is.EqualTo(false));
        }

        [Test]
        public void RoundTrip_custom_struct() {
            var c = new SimpleController();
            Assert.That(As(c).TrySet("Coords", new Pair(11, 22)), Is.True);
            Assert.That(As(c).TryGet("Coords", out var v), Is.True);
            Assert.That(v, Is.EqualTo(new Pair(11, 22)));
            Assert.That(c.ReadCoords(), Is.EqualTo(new Pair(11, 22)));
        }

        [Test]
        public void Private_field_accessible_via_accessor() {
            var c = new PrivateBindController();
            Assert.That(As(c).TryGet("hidden", out var v), Is.True);
            Assert.That(v, Is.EqualTo(42));
            Assert.That(As(c).TrySet("hidden", 99), Is.True);
            Assert.That(c.ReadHidden(), Is.EqualTo(99));
        }

        [Test]
        public void Inherited_UIBind_falls_back_to_reflection_via_resolver() {
            // The generator emits accessor only for the declaring (derived) class.
            // BindingResolver uses generated TryGet first, then reflection on miss.
            BindingResolver.ClearCacheForTests();
            var c = new DerivedController();
            // Generated path covers FromDerived.
            var derived = BindingResolver.Resolve(c, BindingPath.Parse("FromDerived"));
            Assert.That(derived, Is.EqualTo("derived"));
            // Reflection path covers FromBase since it's declared on BaseWithBind.
            var fromBase = BindingResolver.Resolve(c, BindingPath.Parse("FromBase"));
            Assert.That(fromBase, Is.EqualTo(7));
        }

        [Test]
        public void TrySet_null_to_reference_type_succeeds() {
            var c = new SimpleController();
            Assert.That(As(c).TrySet("Name", null), Is.True);
            Assert.That(c.Name, Is.Null);
        }
    }
}
