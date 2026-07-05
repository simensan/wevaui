#if WEVA_URP
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Diagnostics;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering {
    // CSS Compositing 1 §6 / §11 — value-tracking coverage for B3b/B3c.
    // These tests prove the cascade -> resolver -> paint command -> batcher
    // pipeline carries the author's `mix-blend-mode` keyword to the per-
    // instance UIQuadInstance data slot the shader dispatches on, including
    // the four HSL-based modes (hue/saturation/color/luminosity) that B3c
    // lifted out of the resolver fallback path. They do NOT exercise the
    // GPU shader compile path; visual validation of the composited result
    // against Chrome is a separate manual step the engineer signs off on
    // (see tracker).
    public class MixBlendModeTests {
        const float Eps = 1e-4f;

        // BoxToPaintConverter -> BatchedURPRenderBackend roundtrip helper.
        // Builds a single BlockBox carrying the given style, paints it
        // through the converter, and replays the resulting PaintList into
        // a fresh BatchedURPRenderBackend so the emitted batches/instances
        // can be inspected.
        static BatchedURPRenderBackend ConvertAndSubmit(ComputedStyle style) {
            var bb = new BlockBox { Style = style, X = 0, Y = 0, Width = 64, Height = 32 };
            var converter = new BoxToPaintConverter();
            var paintList = converter.Convert(bb);
            var backend = new BatchedURPRenderBackend();
            backend.BeginFrame();
            foreach (var cmd in paintList.Commands) cmd.Submit(backend);
            backend.EndFrame();
            return backend;
        }

        static ComputedStyle MakeStyle(string blendMode) {
            var s = new ComputedStyle(new Element("div"));
            // Background-color is required: without a paintable decoration
            // the converter emits no FillRect and there's no instance to
            // inspect. The blend mode lives on the wrapper, not the fill.
            s.Set("background-color", "rgb(255, 0, 0)");
            s.Set("mix-blend-mode", blendMode);
            return s;
        }

        [SetUp]
        public void Reset() {
            // Clear the one-shot diagnostics dedupe so the no-longer-fires
            // HSL-fallback assertion below isn't polluted by a stale state
            // from an earlier test run.
            UICssDiagnostics.ResetForTests();
            UICssDiagnostics.Enabled = true;
        }

        [Test]
        public void Multiply_packs_to_TransformRow0_z_as_enum_ordinal() {
            var backend = ConvertAndSubmit(MakeStyle("multiply"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Multiply).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Multiply));
        }

        [Test]
        public void Difference_packs_to_TransformRow0_z_as_enum_ordinal() {
            var backend = ConvertAndSubmit(MakeStyle("difference"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Difference).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Difference));
        }

        [Test]
        public void Normal_default_packs_as_zero_ordinal() {
            // No mix-blend-mode declaration — the cascade default is `normal`
            // (MixBlendMode.Normal = 0). The shader dispatcher's `if (mode <= 0)`
            // pass-through means a zero value cleanly skips compositing.
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-color", "rgb(0, 0, 255)");
            var backend = ConvertAndSubmit(s);
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo(0f).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Normal));
        }

        [Test]
        public void Explicit_normal_keyword_packs_as_zero_ordinal() {
            // Authors who explicitly write `mix-blend-mode: normal` should
            // see the same zero ordinal — the converter MUST short-circuit
            // the Push when the resolved mode is Normal to keep batches
            // alloc-free, but the per-instance slot still reads zero
            // either way.
            var backend = ConvertAndSubmit(MakeStyle("normal"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Hue_packs_ordinal_and_emits_no_hsl_fallback_warning_B3c() {
            // CSS Compositing 1 §11.5 — hue is now first-class via the
            // HLSL SetLum/SetSat helpers (B3c). The cascade must therefore
            // carry MixBlendMode.Hue all the way to the per-instance slot,
            // and the legacy HSL-fallback diagnostic must NOT fire.
            var backend = ConvertAndSubmit(MakeStyle("hue"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Hue).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Hue));
            Assert.That(
                UICssDiagnostics.HasEmittedForTests("MixBlendMode",
                    "mix-blend-mode: hue is HSL-based and not yet implemented in the URP shader (B3c); falling back to normal"),
                Is.False,
                "B3c removed the HSL fallback path — the diagnostic must not fire any more.");
        }

        [Test]
        public void Saturation_packs_ordinal_B3c() {
            // CSS Compositing 1 §11.6 — saturation(Cb, Cs) takes the
            // SOURCE saturation and keeps the BACKDROP hue/luminosity.
            var backend = ConvertAndSubmit(MakeStyle("saturation"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Saturation).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Saturation));
        }

        [Test]
        public void Color_packs_ordinal_B3c() {
            // CSS Compositing 1 §11.7 — color(Cb, Cs) = SetLum(Cs, Lum(Cb)):
            // source hue+sat with the backdrop's luminosity.
            var backend = ConvertAndSubmit(MakeStyle("color"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Color).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Color));
        }

        [Test]
        public void Luminosity_packs_ordinal_B3c() {
            // CSS Compositing 1 §11.8 — luminosity(Cb, Cs) = SetLum(Cb, Lum(Cs)):
            // backdrop hue+sat with the source's luminosity (the inverse of
            // color, used for re-toning monochrome overlays).
            var backend = ConvertAndSubmit(MakeStyle("luminosity"));
            Assert.That(backend.Batcher.Batches.Count, Is.GreaterThan(0));
            var inst = backend.Batcher.Batches[0].Instances[0];
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Luminosity).Within(Eps));
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Luminosity));
        }

        [Test]
        public void Resolver_returns_typed_enum_for_each_mode() {
            // Direct C# coverage for the resolver. Mirrors the shader's
            // dispatcher ordinals — if anyone reorders the enum without
            // updating the shader, this catches it.
            Assert.That(ResolveOf("normal"), Is.EqualTo(MixBlendMode.Normal));
            Assert.That(ResolveOf("multiply"), Is.EqualTo(MixBlendMode.Multiply));
            Assert.That(ResolveOf("screen"), Is.EqualTo(MixBlendMode.Screen));
            Assert.That(ResolveOf("overlay"), Is.EqualTo(MixBlendMode.Overlay));
            Assert.That(ResolveOf("darken"), Is.EqualTo(MixBlendMode.Darken));
            Assert.That(ResolveOf("lighten"), Is.EqualTo(MixBlendMode.Lighten));
            Assert.That(ResolveOf("color-dodge"), Is.EqualTo(MixBlendMode.ColorDodge));
            Assert.That(ResolveOf("color-burn"), Is.EqualTo(MixBlendMode.ColorBurn));
            Assert.That(ResolveOf("hard-light"), Is.EqualTo(MixBlendMode.HardLight));
            Assert.That(ResolveOf("soft-light"), Is.EqualTo(MixBlendMode.SoftLight));
            Assert.That(ResolveOf("difference"), Is.EqualTo(MixBlendMode.Difference));
            Assert.That(ResolveOf("exclusion"), Is.EqualTo(MixBlendMode.Exclusion));
            Assert.That(ResolveOf("plus-lighter"), Is.EqualTo(MixBlendMode.PlusLighter));
            // HSL-based — newly first-class in B3c.
            Assert.That(ResolveOf("hue"), Is.EqualTo(MixBlendMode.Hue));
            Assert.That(ResolveOf("saturation"), Is.EqualTo(MixBlendMode.Saturation));
            Assert.That(ResolveOf("color"), Is.EqualTo(MixBlendMode.Color));
            Assert.That(ResolveOf("luminosity"), Is.EqualTo(MixBlendMode.Luminosity));
        }

        static MixBlendMode ResolveOf(string keyword) {
            var s = new ComputedStyle(new Element("div"));
            s.Set("mix-blend-mode", keyword);
            return MixBlendModeResolver.Resolve(s);
        }
    }
}
#endif
