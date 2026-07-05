using NUnit.Framework;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Images {
    // ImageNineSlice is a small readonly value struct: four edge inset
    // doubles plus an IsEmpty predicate. The interface IImageNineSliceSource
    // is the only contract beyond that. These tests pin construction order
    // (top/right/bottom/left -- CSS shorthand convention) and the IsEmpty
    // predicate that gates whether the paint pipeline draws a 9-slice or a
    // plain stretched image.
    public class ImageNineSliceTests {
        [Test]
        public void Constructor_assigns_TRBL_order() {
            var slice = new ImageNineSlice(1, 2, 3, 4);
            Assert.That(slice.Top, Is.EqualTo(1));
            Assert.That(slice.Right, Is.EqualTo(2));
            Assert.That(slice.Bottom, Is.EqualTo(3));
            Assert.That(slice.Left, Is.EqualTo(4));
        }

        [Test]
        public void Default_struct_is_empty() {
            // default(ImageNineSlice) has all-zero edges -> IsEmpty true.
            var slice = default(ImageNineSlice);
            Assert.That(slice.IsEmpty, Is.True);
        }

        [Test]
        public void All_zero_edges_is_empty() {
            var slice = new ImageNineSlice(0, 0, 0, 0);
            Assert.That(slice.IsEmpty, Is.True);
        }

        [Test]
        public void Any_positive_edge_makes_slice_non_empty() {
            Assert.That(new ImageNineSlice(1, 0, 0, 0).IsEmpty, Is.False, "top");
            Assert.That(new ImageNineSlice(0, 1, 0, 0).IsEmpty, Is.False, "right");
            Assert.That(new ImageNineSlice(0, 0, 1, 0).IsEmpty, Is.False, "bottom");
            Assert.That(new ImageNineSlice(0, 0, 0, 1).IsEmpty, Is.False, "left");
        }

        [Test]
        public void Negative_edges_count_as_empty() {
            // IsEmpty uses `<= 0` on every edge. Pin that a slice with no
            // positive insets (negative slices are nonsensical for CSS
            // border-image-slice) is treated as empty, so the paint pipeline
            // skips the 9-slice path.
            var slice = new ImageNineSlice(-1, -2, -3, -4);
            Assert.That(slice.IsEmpty, Is.True);
        }

        [Test]
        public void Mixed_positive_and_negative_is_non_empty() {
            var slice = new ImageNineSlice(-1, 0, 5, 0);
            Assert.That(slice.IsEmpty, Is.False);
        }

        sealed class StubSource : IImageNineSliceSource {
            readonly bool ok;
            readonly ImageNineSlice slice;
            public StubSource(bool ok, ImageNineSlice slice) { this.ok = ok; this.slice = slice; }
            public bool TryGetNineSlice(out ImageNineSlice s) {
                s = ok ? slice : default;
                return ok;
            }
        }

        [Test]
        public void IImageNineSliceSource_contract_returns_slice_when_present() {
            // Regression: the interface returns a slice via out-param; pin the
            // boolean/out-param contract so any future refactor (e.g. ref
            // return, nullable struct) is a deliberate breaking change.
            IImageNineSliceSource src = new StubSource(true, new ImageNineSlice(2, 4, 6, 8));
            Assert.That(src.TryGetNineSlice(out var s), Is.True);
            Assert.That(s.Top, Is.EqualTo(2));
            Assert.That(s.Left, Is.EqualTo(8));
        }

        [Test]
        public void IImageNineSliceSource_contract_returns_false_when_absent() {
            IImageNineSliceSource src = new StubSource(false, default);
            Assert.That(src.TryGetNineSlice(out _), Is.False);
        }
    }
}
