using NUnit.Framework;
using Weva.Binding;

namespace Weva.Tests.Binding {
    public class BindingResolverTests {
        class Inner {
            public string Name = "inner-name";
            int Hidden = 42;
            public int GetHidden() => Hidden;
        }

        class Root {
            public string PublicField = "public-field";
            string PrivateField = "private-field";
            public int PublicProp { get; set; } = 7;
            int PrivateProp { get; set; } = 9;
            public Inner Child = new Inner();
            public Inner NullChild = null;
            public int CoinCount = 100;
            public bool Active = true;

            public string ReadPrivateField() => PrivateField;
            public int ReadPrivateProp() => PrivateProp;
        }

        class Child : Root {
            public string ChildField = "child-only";
        }

        [Test]
        public void Resolves_public_field() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("PublicField"));
            Assert.That(v, Is.EqualTo("public-field"));
        }

        [Test]
        public void Resolves_private_field() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("PrivateField"));
            Assert.That(v, Is.EqualTo("private-field"));
        }

        [Test]
        public void Resolves_public_property() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("PublicProp"));
            Assert.That(v, Is.EqualTo(7));
        }

        [Test]
        public void Resolves_private_property() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("PrivateProp"));
            Assert.That(v, Is.EqualTo(9));
        }

        [Test]
        public void Resolves_nested_path() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("Child.Name"));
            Assert.That(v, Is.EqualTo("inner-name"));
        }

        [Test]
        public void Null_root_returns_null() {
            var v = BindingResolver.Resolve(null, BindingPath.Parse("Anything"));
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Null_intermediate_returns_null() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("NullChild.Name"));
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Missing_member_returns_null() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("DoesNotExist"));
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Missing_member_in_middle_returns_null() {
            var r = new Root();
            var v = BindingResolver.Resolve(r, BindingPath.Parse("Child.Missing"));
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Cached_lookup_does_not_re_scan() {
            BindingResolver.ClearCacheForTests();
            var r = new Root();
            BindingResolver.Resolve(r, BindingPath.Parse("PublicField"));
            int afterFirst = BindingResolver.CacheCount;
            BindingResolver.Resolve(r, BindingPath.Parse("PublicField"));
            BindingResolver.Resolve(r, BindingPath.Parse("PublicField"));
            int afterRepeats = BindingResolver.CacheCount;
            Assert.That(afterRepeats, Is.EqualTo(afterFirst));
        }

        [Test]
        public void Boxed_value_type_int() {
            var r = new Root { CoinCount = 1234 };
            var v = BindingResolver.Resolve(r, BindingPath.Parse("CoinCount"));
            Assert.That(v, Is.EqualTo(1234));
            Assert.That(v, Is.TypeOf<int>());
        }

        [Test]
        public void Boxed_value_type_bool() {
            var r = new Root { Active = false };
            var v = BindingResolver.Resolve(r, BindingPath.Parse("Active"));
            Assert.That(v, Is.EqualTo(false));
        }

        [Test]
        public void Inherited_field_is_resolved() {
            var c = new Child();
            var v = BindingResolver.Resolve(c, BindingPath.Parse("PublicField"));
            Assert.That(v, Is.EqualTo("public-field"));
        }

        [Test]
        public void Subclass_field_is_resolved() {
            var c = new Child();
            var v = BindingResolver.Resolve(c, BindingPath.Parse("ChildField"));
            Assert.That(v, Is.EqualTo("child-only"));
        }

        [Test]
        public void TryResolve_succeeds_with_value() {
            var r = new Root();
            bool ok = BindingResolver.TryResolve(r, BindingPath.Parse("PublicField"), out var v);
            Assert.That(ok, Is.True);
            Assert.That(v, Is.EqualTo("public-field"));
        }

        [Test]
        public void TryResolve_returns_false_on_null_intermediate() {
            var r = new Root();
            bool ok = BindingResolver.TryResolve(r, BindingPath.Parse("NullChild.Name"), out var v);
            Assert.That(ok, Is.False);
            Assert.That(v, Is.Null);
        }

        [Test]
        public void TryResolve_returns_false_on_null_root() {
            bool ok = BindingResolver.TryResolve(null, BindingPath.Parse("X"), out var v);
            Assert.That(ok, Is.False);
            Assert.That(v, Is.Null);
        }

        [Test]
        public void TryResolve_returns_false_on_missing_member() {
            var r = new Root();
            bool ok = BindingResolver.TryResolve(r, BindingPath.Parse("Missing"), out var v);
            Assert.That(ok, Is.False);
            Assert.That(v, Is.Null);
        }
    }
}
