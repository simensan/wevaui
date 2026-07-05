using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // NG5 — MaskResolver.Resolve null-guards style and box. The
    // imageRegistry-must-be-non-null guard only fires when mask-image is
    // actually declared; otherwise the resolver returns null cleanly so
    // every painted box on a document without masks doesn't throw when the
    // host (WevaDocument) never wired an image registry.
    public class MaskResolverNullGuardTests {
        static (ComputedStyle style, BlockBox box) MakeStyledBox() {
            var style = new ComputedStyle(new Element("div"));
            var box = new BlockBox { Width = 100, Height = 100 };
            return (style, box);
        }

        static ComputedStyle WithMaskImage(ComputedStyle style) {
            style.Set(CssProperties.MaskImageId, "url(\"glyph.png\")");
            return style;
        }

        [Test]
        public void Resolve_box_overload_with_null_imageRegistry_and_declared_mask_throws_NG5() {
            var (style, box) = MakeStyledBox();
            WithMaskImage(style);
            Assert.Throws<ArgumentNullException>(() =>
                MaskResolver.Resolve(style, box, 0, 0, LengthContext.Default, null));
        }

        [Test]
        public void Resolve_bounds_overload_with_null_imageRegistry_and_declared_mask_throws_NG5() {
            var (style, _) = MakeStyledBox();
            WithMaskImage(style);
            var bounds = new Rect(0, 0, 100, 100);
            Assert.Throws<ArgumentNullException>(() =>
                MaskResolver.Resolve(style, bounds, LengthContext.Default, null));
        }

        [Test]
        public void Resolve_clipBounds_originBounds_overload_with_null_imageRegistry_and_declared_mask_throws_NG5() {
            var (style, _) = MakeStyledBox();
            WithMaskImage(style);
            var clip = new Rect(0, 0, 100, 100);
            var origin = new Rect(0, 0, 50, 50);
            Assert.Throws<ArgumentNullException>(() =>
                MaskResolver.Resolve(style, clip, origin, LengthContext.Default, null));
        }

        [Test]
        public void Resolve_box_overload_happy_path_returns_null_for_no_mask_image_NG5() {
            // No mask-image set -> result is null but no throw — regression pin
            // that the guard hasn't broken the no-op path.
            var (style, box) = MakeStyledBox();
            var reg = new InMemoryImageRegistry();
            var result = MaskResolver.Resolve(style, box, 0, 0, LengthContext.Default, reg);
            Assert.That(result, Is.Null);
        }

        // The bug that triggered this rewrite: every painted box on a document
        // without masks called MaskResolver.Resolve with a null registry and
        // threw. Pin that the no-mask + null-registry combo returns null
        // cleanly across ALL three overloads.

        [Test]
        public void Resolve_box_overload_no_mask_null_registry_returns_null_no_throw() {
            var (style, box) = MakeStyledBox();
            MaskDefinition result = null;
            Assert.DoesNotThrow(() => {
                result = MaskResolver.Resolve(style, box, 0, 0, LengthContext.Default, null);
            });
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Resolve_bounds_overload_no_mask_null_registry_returns_null_no_throw() {
            var (style, _) = MakeStyledBox();
            var bounds = new Rect(0, 0, 100, 100);
            MaskDefinition result = null;
            Assert.DoesNotThrow(() => {
                result = MaskResolver.Resolve(style, bounds, LengthContext.Default, null);
            });
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Resolve_clipBounds_originBounds_overload_no_mask_null_registry_returns_null_no_throw() {
            var (style, _) = MakeStyledBox();
            var clip = new Rect(0, 0, 100, 100);
            var origin = new Rect(0, 0, 50, 50);
            MaskDefinition result = null;
            Assert.DoesNotThrow(() => {
                result = MaskResolver.Resolve(style, clip, origin, LengthContext.Default, null);
            });
            Assert.That(result, Is.Null);
        }

        [Test]
        public void Resolve_box_overload_with_mask_none_null_registry_returns_null_no_throw() {
            // Explicit `mask-image: none` should also be treated as "no mask"
            // and not trigger the guard, matching the HasNonNoneValue convention
            // already used by IsPaintEligibleForMask elsewhere in the pipeline.
            var (style, box) = MakeStyledBox();
            style.Set(CssProperties.MaskImageId, "none");
            MaskDefinition result = null;
            Assert.DoesNotThrow(() => {
                result = MaskResolver.Resolve(style, box, 0, 0, LengthContext.Default, null);
            });
            Assert.That(result, Is.Null);
        }
    }
}
