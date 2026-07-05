using System;
using NUnit.Framework;
using Weva.Animation;
using Weva.Css;
using Weva.Css.Animation;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;

namespace Weva.Tests.Css.Animation {
    public class PropertyKindRegistryTests {
        [Test]
        public void Length_properties_are_classified_as_length() {
            Assert.That(PropertyKindRegistry.Of("width"), Is.EqualTo(PropertyKind.Length));
            Assert.That(PropertyKindRegistry.Of("height"), Is.EqualTo(PropertyKind.Length));
            Assert.That(PropertyKindRegistry.Of("padding-top"), Is.EqualTo(PropertyKind.Length));
            Assert.That(PropertyKindRegistry.Of("font-size"), Is.EqualTo(PropertyKind.Length));
            Assert.That(PropertyKindRegistry.Of("border-radius"), Is.EqualTo(PropertyKind.Length));
        }

        [Test]
        public void Color_properties_are_classified_as_color() {
            Assert.That(PropertyKindRegistry.Of("color"), Is.EqualTo(PropertyKind.Color));
            Assert.That(PropertyKindRegistry.Of("background-color"), Is.EqualTo(PropertyKind.Color));
            Assert.That(PropertyKindRegistry.Of("border-top-color"), Is.EqualTo(PropertyKind.Color));
        }

        [Test]
        public void Opacity_is_number() {
            Assert.That(PropertyKindRegistry.Of("opacity"), Is.EqualTo(PropertyKind.Number));
        }

        [Test]
        public void Display_is_discrete() {
            Assert.That(PropertyKindRegistry.Of("display"), Is.EqualTo(PropertyKind.Discrete));
            Assert.That(PropertyKindRegistry.Of("visibility"), Is.EqualTo(PropertyKind.Discrete));
        }

        [Test]
        public void Transform_is_transform() {
            Assert.That(PropertyKindRegistry.Of("transform"), Is.EqualTo(PropertyKind.Transform));
        }

        [Test]
        public void Unknown_property_falls_back_to_discrete() {
            Assert.That(PropertyKindRegistry.Of("zzz-unknown"), Is.EqualTo(PropertyKind.Discrete));
            Assert.That(PropertyKindRegistry.IsAnimatable("zzz-unknown"), Is.False);
        }

        [Test]
        public void Null_input_safe() {
            Assert.That(PropertyKindRegistry.Of(null), Is.EqualTo(PropertyKind.Discrete));
            Assert.That(PropertyKindRegistry.IsAnimatable(null), Is.False);
        }

        // H18: outline-* / scroll-margin-* / scroll-padding-* / vertical-align
        // were missing from the animatable map, so `transition-property: all`
        // silently skipped them.
        [Test]
        public void Newly_registered_length_properties_are_animatable() {
            string[] expected = new[] {
                "outline-width", "outline-offset",
                "scroll-margin", "scroll-margin-top", "scroll-margin-right",
                "scroll-margin-bottom", "scroll-margin-left",
                "scroll-padding", "scroll-padding-top", "scroll-padding-right",
                "scroll-padding-bottom", "scroll-padding-left",
                "vertical-align",
            };
            foreach (var p in expected) {
                Assert.That(PropertyKindRegistry.IsAnimatable(p), Is.True,
                    $"{p} should be animatable");
                Assert.That(PropertyKindRegistry.Of(p), Is.EqualTo(PropertyKind.Length),
                    $"{p} should be classified as Length");
            }
        }

        [Test]
        public void Existing_animatable_properties_still_registered() {
            Assert.That(PropertyKindRegistry.IsAnimatable("width"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("opacity"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("color"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("transform"), Is.True);
            Assert.That(PropertyKindRegistry.IsAnimatable("background-color"), Is.True);
            Assert.That(PropertyKindRegistry.Of("width"), Is.EqualTo(PropertyKind.Length));
            Assert.That(PropertyKindRegistry.Of("opacity"), Is.EqualTo(PropertyKind.Number));
            Assert.That(PropertyKindRegistry.Of("color"), Is.EqualTo(PropertyKind.Color));
        }

        [Test]
        public void Outline_width_transitions_to_midpoint_under_linear_easing() {
            var clock = new FakeUIClock();
            var cascade = new CascadeEngine(Array.Empty<OriginatedStylesheet>());
            var runner = new CssAnimationRunner(cascade, Array.Empty<Stylesheet>(), clock);
            var e = new Element("div");
            var prev = new ComputedStyle(e);
            prev.Set("outline-width", "0px");
            prev.Set("transition", "outline-width 1s linear");
            var next = new ComputedStyle(e);
            next.Set("outline-width", "10px");
            next.Set("transition", "outline-width 1s linear");
            runner.OnStyleChange(e, prev, next);
            Assert.That(runner.HasRunningAnimations(e), Is.True,
                "outline-width must be eligible to transition");
            clock.Set(0.5);
            runner.Tick(0.5);
            var composed = runner.Compose(e, next);
            Assert.That(composed.Get("outline-width"), Is.EqualTo("5px"));
        }
    }
}
