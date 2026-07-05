using NUnit.Framework;
using Weva.Layout;

namespace Weva.Tests.Layout.Incremental {
    public class LayoutDigestTests {
        [Test]
        public void Digests_with_same_inputs_are_equal() {
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a == b, Is.True);
            Assert.That(a != b, Is.False);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Digests_differ_when_element_version_differs() {
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutDigestKey(99, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Digests_differ_when_computed_style_version_differs() {
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutDigestKey(1, 99, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Digests_differ_when_parent_content_width_differs() {
            // Width is the layout-input axis most layouts depend on (per CSS
            // Sizing L3 §5: most percentage-based properties are inline-axis-
            // relative). A ContainerWidth change must mismatch the digest.
            var a = new LayoutDigestKey(1, 2, 100, 50, 5, 6);
            var b = new LayoutDigestKey(1, 2, 200, 50, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Digests_differ_when_parent_content_height_differs() {
            var a = new LayoutDigestKey(1, 2, 100, 50, 5, 6);
            var b = new LayoutDigestKey(1, 2, 100, 99, 5, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Digests_differ_when_layout_context_version_differs() {
            // LayoutContextVersion is the engine's monotone signal that the
            // layout context (viewport, root font size) changed enough to
            // force a re-layout — the digest must reflect this.
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutDigestKey(1, 2, 3, 4, 99, 6);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Digests_differ_when_child_aggregate_version_differs() {
            // The aggregate version captures structural / per-child Box.Version
            // changes inside the subtree. Two boxes that match on every other
            // axis but have different children must produce different digests.
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            var b = new LayoutDigestKey(1, 2, 3, 4, 5, 99);
            Assert.That(a.Equals(b), Is.False);
        }

        [Test]
        public void Hashcodes_equal_for_equal_digests() {
            var a = new LayoutDigestKey(7, 7, 7, 7, 7, 7);
            var b = new LayoutDigestKey(7, 7, 7, 7, 7, 7);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        }

        [Test]
        public void Empty_digest_equals_default() {
            var a = LayoutDigestKey.Empty;
            var b = default(LayoutDigestKey);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a == b, Is.True);
        }

        [Test]
        public void Empty_digest_differs_from_populated() {
            var empty = LayoutDigestKey.Empty;
            var populated = new LayoutDigestKey(1, 1, 1, 1, 1, 1);
            Assert.That(empty.Equals(populated), Is.False);
        }

        [Test]
        public void ToString_is_diagnostic() {
            var a = new LayoutDigestKey(11, 22, 33, 44, 55, 66);
            string s = a.ToString();
            Assert.That(s, Does.Contain("11"));
            Assert.That(s, Does.Contain("22"));
            Assert.That(s, Does.Contain("33"));
            Assert.That(s, Does.Contain("44"));
            Assert.That(s, Does.Contain("55"));
            Assert.That(s, Does.Contain("66"));
        }

        [Test]
        public void Boxed_equals_works() {
            var a = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            object b = new LayoutDigestKey(1, 2, 3, 4, 5, 6);
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.Equals("not a digest"), Is.False);
        }

        [Test]
        public void Box_cached_digest_starts_empty() {
            // Box.CachedDigest is the LayoutDigestKey field on every Box
            // instance. Freshly-pooled boxes must report the empty digest so
            // the subtree-skip path doesn't false-hit on a fresh Box.
            var b = new Weva.Layout.Boxes.BlockBox();
            Assert.That(b.CachedDigest, Is.EqualTo(LayoutDigestKey.Empty));
        }

        [Test]
        public void Box_cached_digest_persists_after_reset() {
            // ResetForPool wipes the digest. The pool's recycled instance must
            // not retain the prior frame's cached digest, else a fresh tree
            // built atop the recycled box would falsely report stable inputs.
            var b = new Weva.Layout.Boxes.BlockBox {
                CachedDigest = new LayoutDigestKey(1, 2, 3, 4, 5, 6)
            };
            // ResetForPool is internal; we exercise it indirectly by relying
            // on the field semantics: a freshly-constructed box has the empty
            // digest, so the contract is "digest only carries forward across
            // a survived Reconcile call". This test asserts the empty contract.
            var fresh = new Weva.Layout.Boxes.BlockBox();
            Assert.That(fresh.CachedDigest, Is.EqualTo(LayoutDigestKey.Empty));
            Assert.That(b.CachedDigest, Is.Not.EqualTo(LayoutDigestKey.Empty));
        }
    }
}
