using NUnit.Framework;
using Weva.Binding;

namespace Weva.Tests.Binding {
    // BindingScope is internal; InternalsVisibleTo from the runtime asmdef
    // (Runtime/Css/Selectors/AssemblyInfo.cs) exposes it to this assembly.
    // These tests pin the scope's local-resolution behaviour: the alias
    // segment resolves to `item`, `$index` resolves to the integer index,
    // and unrecognised segments fall back to the parent context via
    // BindingResolver. RepeatBinding is the only production caller, so any
    // behavioural drift here breaks the for-each binding contract.
    public class BindingScopeTests {
        class Parent {
            public string Title = "parent-title";
            public int Counter = 42;
        }

        [Test]
        public void Alias_segment_resolves_to_bound_item() {
            var scope = new BindingScope(new Parent(), "row", "ITEM", 7);
            Assert.That(scope.TryResolveLocal("row", out var value), Is.True);
            Assert.That(value, Is.EqualTo("ITEM"));
        }

        [Test]
        public void Dollar_index_segment_resolves_to_integer_index() {
            var scope = new BindingScope(new Parent(), "row", "ITEM", 7);
            Assert.That(scope.TryResolveLocal("$index", out var value), Is.True);
            Assert.That(value, Is.EqualTo(7));
        }

        [Test]
        public void Unknown_segment_falls_back_to_parent_resolution() {
            var scope = new BindingScope(new Parent(), "row", "ITEM", 0);
            Assert.That(scope.TryResolveLocal("Title", out var value), Is.True);
            Assert.That(value, Is.EqualTo("parent-title"));
        }

        [Test]
        public void Unknown_segment_with_null_parent_returns_false_and_null() {
            var scope = new BindingScope(null, "row", "ITEM", 0);
            Assert.That(scope.TryResolveLocal("Missing", out var value), Is.False);
            Assert.That(value, Is.Null);
        }

        [Test]
        public void Alias_takes_precedence_over_a_homonymous_parent_member() {
            // If the parent has a field named the same as the alias, the
            // alias wins. This is the load-bearing contract for RepeatBinding:
            // inside a `for x in items` template, `x` must NEVER fall through
            // to a parent.x member of the same name.
            var parent = new Parent();
            var scope = new BindingScope(parent, "Title", "scoped-value", 0);
            Assert.That(scope.TryResolveLocal("Title", out var value), Is.True);
            Assert.That(value, Is.EqualTo("scoped-value"));
        }

        [Test]
        public void Index_zero_is_resolved_as_value_not_treated_as_missing() {
            // Regression: $index returning false at index 0 would be a
            // classic falsy-int bug. Pin that the scope reports success and
            // hands back the int 0 (not null).
            var scope = new BindingScope(new Parent(), "row", "ITEM", 0);
            Assert.That(scope.TryResolveLocal("$index", out var value), Is.True);
            Assert.That(value, Is.EqualTo(0));
        }

        [Test]
        public void BindingResolver_dispatches_through_IBindingScope_local_path_first() {
            // Higher-level regression: BindingResolver.ResolveSegment checks
            // IBindingScope before reflection. Passing a BindingScope as the
            // root must let `row` resolve via the scope's alias mapping.
            var scope = new BindingScope(new Parent(), "row", "ITEM", 3);
            var ok = BindingResolver.TryResolve(scope, BindingPath.Parse("row"), out var value);
            Assert.That(ok, Is.True);
            Assert.That(value, Is.EqualTo("ITEM"));
        }

        [Test]
        public void Reset_repoints_parent_item_and_index() {
            var scope = new BindingScope(new Parent(), "row", "OLD", 1);
            var newParent = new Parent { Title = "second-parent" };
            scope.Reset(newParent, "NEW", 9);

            Assert.That(scope.TryResolveLocal("row", out var item), Is.True);
            Assert.That(item, Is.EqualTo("NEW"));
            Assert.That(scope.TryResolveLocal("$index", out var index), Is.True);
            Assert.That(index, Is.EqualTo(9));
            Assert.That(scope.TryResolveLocal("Title", out var title), Is.True);
            Assert.That(title, Is.EqualTo("second-parent"));
        }

        [Test]
        public void Dollar_index_box_is_cached_across_resolutions() {
            // The scope is reused across frames; resolving $index repeatedly
            // must hand back the same boxed int, not box a fresh one per poll.
            var scope = new BindingScope(new Parent(), "row", "ITEM", 7);
            scope.TryResolveLocal("$index", out var first);
            scope.TryResolveLocal("$index", out var second);
            Assert.That(ReferenceEquals(first, second), Is.True);
        }

        [Test]
        public void Reset_with_new_index_invalidates_the_cached_box() {
            var scope = new BindingScope(new Parent(), "row", "ITEM", 1);
            scope.TryResolveLocal("$index", out var before);
            Assert.That(before, Is.EqualTo(1));
            scope.Reset(new Parent(), "ITEM", 2);
            scope.TryResolveLocal("$index", out var after);
            Assert.That(after, Is.EqualTo(2));
        }

        [Test]
        public void Parent_fallback_resolves_through_a_nested_scope() {
            // Repeat-in-repeat: the parent context of an inner scope is the
            // outer scope. The single-segment fallback must still dispatch
            // through IBindingScope on the parent.
            var outer = new BindingScope(new Parent(), "outerRow", "OUTER-ITEM", 0);
            var inner = new BindingScope(outer, "innerRow", "INNER-ITEM", 1);
            Assert.That(inner.TryResolveLocal("outerRow", out var value), Is.True);
            Assert.That(value, Is.EqualTo("OUTER-ITEM"));
            Assert.That(inner.TryResolveLocal("Title", out var title), Is.True);
            Assert.That(title, Is.EqualTo("parent-title"));
        }
    }
}
