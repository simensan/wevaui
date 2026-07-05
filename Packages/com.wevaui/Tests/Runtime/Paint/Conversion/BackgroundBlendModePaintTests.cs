using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // CSS Compositing 1 §9 — background-blend-mode paint-command structure.
    //
    // background-blend-mode blends each background layer with the element's OWN
    // background layers below it and the element's background-color. It does NOT
    // involve the page backdrop — blending is element-local and fully determined
    // at paint time.
    //
    // Spec correction applied (previously wrong):
    //   The old implementation used PushMixBlendModeCommand (page-backdrop blend)
    //   for background image layers. This was spec-wrong: it blended gradient tiles
    //   against _WevaBackdrop (the camera clear color), visibly different from
    //   Chrome. The correct approach uses PushBackgroundBlendCommand (element-local
    //   blend), which bakes the element's background-color as the compositing base
    //   into the UIQuadInstance spare channels.
    //
    // What these tests verify (CPU-side, PaintCommand structure):
    //   1. FAST PATH: normal-only `background-blend-mode` (or absent) emits the
    //      EXACT same commands as today — no blend wrappers at all.
    //   2. SINGLE-LAYER non-normal: a single gradient + non-normal blend emits
    //      PushBackgroundBlend → FillRect → PopBackgroundBlend (element-local).
    //   3. MULTI-LAYER: a two-layer background wraps only the non-normal layer;
    //      the normal background-color layer is unwrapped.
    //   4. LIST-CYCLING: fewer modes than layers → modes cycle per CSS Backgrounds
    //      3 §3.10 (same rule as background-repeat).
    //   5. ALL-LAYERS non-normal: every layer wrapped with its own mode.
    //   6. MODE MAPPING: all 16 CSS <blend-mode> keywords produce the correct
    //      MixBlendMode enum value on the PushBackgroundBlend command.
    //   7. OCCLUSION SKIP DISABLED: with any non-normal mode the occluder-skip
    //      is suppressed and all layers emit.
    //   8. UNKNOWN MODE: unknown keywords fall back to Normal (no wrapper emitted).
    //   9. BASE COLOR: the baked base color matches the element's background-color.
    //  10. NO PAGE-BACKDROP LATCH: element-local pushes must NOT emit any
    //      PushMixBlendModeCommand (that would incorrectly sample _WevaBackdrop).
    //  11. BG-COLOR UNWRAPPED: the background-color fill itself never gets a
    //      background-blend wrapper (it IS the compositing base).
    //
    // NUnit constraint notes:
    //   - NEVER chain .Within() off Is.LessThan/GreaterThan.
    //   - Does.Not.Contain is substring-only; use Has.None.EqualTo for collections.
    //   - Avoid Is.AnyOf.
    public class BackgroundBlendModePaintTests {
        // ── helpers ──────────────────────────────────────────────────────────

        static List<PaintCommand> Paint(string html, string css) {
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            return new BoxToPaintConverter().Convert(root).Commands;
        }

        // Element-local background-blend pushes (spec-correct §9 path).
        static List<PushBackgroundBlendCommand> BgBlendPushes(List<PaintCommand> cmds)
            => cmds.OfType<PushBackgroundBlendCommand>().ToList();

        // Element-local background-blend pops.
        static List<PopBackgroundBlendCommand> BgBlendPops(List<PaintCommand> cmds)
            => cmds.OfType<PopBackgroundBlendCommand>().ToList();

        // Page-backdrop blend pushes (spec-wrong for background layers; must be zero).
        static List<PushMixBlendModeCommand> PageBlendPushes(List<PaintCommand> cmds)
            => cmds.OfType<PushMixBlendModeCommand>().ToList();

        static List<FillRectCommand> FillRects(List<PaintCommand> cmds)
            => cmds.OfType<FillRectCommand>().ToList();

        // Returns the command index in `cmds` of the nth occurrence of T,
        // or -1 if fewer than n+1 occurrences exist.
        static int IndexOf<T>(List<PaintCommand> cmds, int occurrence = 0) where T : PaintCommand {
            int found = 0;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is T) {
                    if (found == occurrence) return i;
                    found++;
                }
            }
            return -1;
        }

        // ── 1. Normal fast path ───────────────────────────────────────────────

        [Test]
        public void Normal_blend_mode_emits_no_blend_wrappers() {
            // With background-blend-mode: normal (initial value) or absent, the
            // paint command list must be identical to no background-blend-mode at
            // all — no PushBackgroundBlend / PopBackgroundBlend commands.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue), white;
                     background-blend-mode: normal; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(BgBlendPushes(cmds), Has.Count.EqualTo(0),
                "normal blend-mode must not emit PushBackgroundBlend");
            Assert.That(BgBlendPops(cmds), Has.Count.EqualTo(0),
                "normal blend-mode must not emit PopBackgroundBlend");
            // Also must not emit the page-backdrop path (spec-wrong for §9).
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "normal blend-mode must not emit PushMixBlendMode");
        }

        [Test]
        public void Absent_blend_mode_emits_no_blend_wrappers() {
            // Same expectation when the property is not authored at all.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue), white; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(BgBlendPushes(cmds), Has.Count.EqualTo(0));
            Assert.That(BgBlendPops(cmds), Has.Count.EqualTo(0));
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0));
        }

        // ── 2. Single layer with non-normal blend mode ────────────────────────

        [Test]
        public void Single_gradient_with_multiply_emits_one_push_fill_pop_sequence() {
            // One gradient layer, background-blend-mode: multiply.
            // Spec-correct command order (CSS Compositing 1 §9, element-local path):
            //   … PushBackgroundBlend(Multiply, baseColor) → FillRect → PopBackgroundBlend …
            // The base color is the element's background-color (transparent black when
            // only a gradient is declared with no explicit background-color).
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue);
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1),
                "exactly one PushBackgroundBlend for the single non-normal layer");
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Multiply));

            // The FillRect must appear between the push and pop.
            int pushIdx = IndexOf<PushBackgroundBlendCommand>(cmds, 0);
            int popIdx  = IndexOf<PopBackgroundBlendCommand>(cmds, 0);
            int fillIdx = -1;
            for (int i = pushIdx + 1; i < popIdx; i++) {
                if (cmds[i] is FillRectCommand) { fillIdx = i; break; }
            }
            Assert.That(fillIdx, Is.GreaterThan(pushIdx),
                "FillRect must appear after PushBackgroundBlend");
            Assert.That(fillIdx, Is.LessThan(popIdx),
                "FillRect must appear before PopBackgroundBlend");
        }

        // ── 2b. Element-local path does NOT emit page-backdrop commands ────────

        [Test]
        public void Single_gradient_with_multiply_emits_no_page_backdrop_push() {
            // CSS Compositing 1 §9 says background-blend-mode is element-local.
            // Using PushMixBlendModeCommand (page-backdrop path) for background
            // layers is spec-wrong: it would blend against _WevaBackdrop (the camera
            // clear color), not the element's background-color.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue);
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "background-blend-mode must NOT emit PushMixBlendModeCommand (spec-wrong page-backdrop path)");
        }

        // ── 3. Multi-layer: only non-normal layers wrapped ────────────────────

        [Test]
        public void Two_layer_background_wraps_only_the_non_normal_layer() {
            // Two layers: gradient (multiply) over white (no mode specified → normal).
            // The gradient is layer 0 (top, paints last), white is the background-color.
            // Layer 0 should be wrapped; the background-color (normal) should not be.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue), white;
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1),
                "only the gradient layer (non-normal) should have a PushBackgroundBlend wrapper");
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Multiply));
            Assert.That(BgBlendPops(cmds), Has.Count.EqualTo(1),
                "one PopBackgroundBlend matching the one push");
            Assert.That(FillRects(cmds), Has.Count.EqualTo(2),
                "both layers still emit FillRect (background-color is kept for the blend)");
        }

        // ── 3b. Background-color fill is not wrapped ──────────────────────────

        [Test]
        public void Background_color_fill_is_not_wrapped_in_blend_command() {
            // The background-color is the compositing BASE — it is never itself
            // blended. Only image layers (li < imageLayerCount) get wrappers.
            //
            // Paint order (bottom-to-top per CSS Backgrounds 3 §3.10):
            //   FillRect(white/background-color) — emitted first, BEFORE the blend scope
            //   PushBackgroundBlend(Multiply)
            //   FillRect(gradient/image layer 0)
            //   PopBackgroundBlend
            //
            // The background-color fill must appear BEFORE the push, confirming it
            // is NOT inside the blend scope (it is the compositing base, not a
            // blended layer — CSS Compositing 1 §9).
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue), white;
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            var pops   = BgBlendPops(cmds);
            // Exactly one push/pop wrapping only the gradient layer.
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pops, Has.Count.EqualTo(1));
            // The background-color FillRect must appear BEFORE the push (i.e.,
            // outside the blend scope, not wrapped).
            int pushIdx = IndexOf<PushBackgroundBlendCommand>(cmds, 0);
            bool foundBgColorFillBeforePush = false;
            for (int i = 0; i < pushIdx; i++) {
                if (cmds[i] is FillRectCommand) { foundBgColorFillBeforePush = true; break; }
            }
            Assert.That(foundBgColorFillBeforePush, Is.True,
                "background-color FillRect must appear BEFORE the blend scope — it is the compositing base, not a blended layer (CSS Compositing 1 §9)");
        }

        // ── 4. List cycling: shorter mode list repeats to cover all layers ────

        [Test]
        public void Mode_list_cycles_when_fewer_modes_than_layers() {
            // Three gradient layers, two modes: "screen, multiply". Per CSS
            // Backgrounds 3 §3.10 the list cycles: layer 0 = screen,
            // layer 1 = multiply, layer 2 = screen again.
            // All three are non-normal so all three should be wrapped.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background-image: linear-gradient(red, blue),
                                       linear-gradient(green, yellow),
                                       linear-gradient(blue, red);
                     background-blend-mode: screen, multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(3),
                "all three gradient layers are non-normal and should be wrapped");
            // Paint order is bottom-to-top: layer index 2, 1, 0.
            // After the occlusion skip is disabled, layers paint in reverse index order.
            // The cycling result:
            //   li=2 → modes[2 % 2] = modes[0] = screen
            //   li=1 → modes[1 % 2] = modes[1] = multiply
            //   li=0 → modes[0 % 2] = modes[0] = screen
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Screen),   "first painted: layer 2 → screen");
            Assert.That(pushes[1].Mode, Is.EqualTo(MixBlendMode.Multiply), "second painted: layer 1 → multiply");
            Assert.That(pushes[2].Mode, Is.EqualTo(MixBlendMode.Screen),   "third painted: layer 0 → screen (cycles)");
        }

        // ── 5. All-layers non-normal ──────────────────────────────────────────

        [Test]
        public void All_layers_non_normal_all_wrapped() {
            // Two gradient layers, two modes: each gradient gets its own wrapper.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background-image: linear-gradient(red, blue),
                                       linear-gradient(green, yellow);
                     background-blend-mode: screen, overlay; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(2),
                "both gradient layers get a wrapper");
            // Layer 1 (bottom gradient) paints first.
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Overlay), "bottom layer: overlay");
            Assert.That(pushes[1].Mode, Is.EqualTo(MixBlendMode.Screen),  "top layer: screen");
            Assert.That(BgBlendPops(cmds), Has.Count.EqualTo(2));
        }

        // ── 6. Mode mapping for all 16 CSS <blend-mode> keywords ─────────────
        // CSS Compositing 1 §9 accepts any <blend-mode> value. Unlike
        // mix-blend-mode, plus-lighter is NOT a <blend-mode> and is not valid.

        [Test]
        public void Screen_mode_maps_to_Screen_enum() {
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:screen; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Screen));
        }

        [Test]
        public void Overlay_mode_maps_to_Overlay_enum() {
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:overlay; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Overlay));
        }

        [Test]
        public void Darken_mode_maps_to_Darken_enum() {
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:darken; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Darken));
        }

        [Test]
        public void Lighten_mode_maps_to_Lighten_enum() {
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:lighten; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Lighten));
        }

        [Test]
        public void Multiply_mode_maps_to_Multiply_enum() {
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:multiply; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Multiply));
        }

        [Test]
        public void Difference_mode_maps_to_Difference_enum() {
            // CSS Compositing 1 §6 — difference was previously untested in this suite.
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:difference; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Difference));
        }

        [Test]
        public void Hue_mode_maps_to_Hue_enum() {
            // CSS Compositing 1 §11.5 — non-separable HSL modes now fully mapped
            // in BackgroundBlendModeResolver (previously fell back to Normal in v1).
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:hue; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Hue));
        }

        [Test]
        public void Saturation_mode_maps_to_Saturation_enum() {
            // CSS Compositing 1 §11.6.
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:saturation; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Saturation));
        }

        [Test]
        public void Color_mode_maps_to_Color_enum() {
            // CSS Compositing 1 §11.7.
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:color; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Color));
        }

        [Test]
        public void Luminosity_mode_maps_to_Luminosity_enum() {
            // CSS Compositing 1 §11.8.
            const string css = "#t { width:100px;height:80px; background:linear-gradient(red,blue); background-blend-mode:luminosity; }";
            var pushes = BgBlendPushes(Paint("<div id=\"t\"></div>", css));
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Luminosity));
        }

        // ── 7. Occlusion skip disabled under any non-normal blend ─────────────

        [Test]
        public void Opaque_gradient_with_blend_mode_keeps_background_color_layer() {
            // An opaque gradient normally occludes the white background-color.
            // With background-blend-mode:multiply the skip is disabled — both
            // layers must be emitted so the blend formula has its operands.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(#ffdd00, #ffaa00), white;
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            // gradient (multiply-wrapped) + white (normal, unwrapped)
            Assert.That(FillRects(cmds), Has.Count.EqualTo(2),
                "both layers must emit when blend mode is non-normal, even if the top layer is opaque");
        }

        // ── 8. Unknown mode keyword falls back to Normal ──────────────────────

        [Test]
        public void Unknown_blend_mode_keyword_falls_back_to_normal() {
            // CSS Compositing 1 §7: unknown values treated as if property not specified.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background: linear-gradient(red, blue);
                     background-blend-mode: dissolve-into-nothing-invalid; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(BgBlendPushes(cmds), Has.Count.EqualTo(0),
                "unknown mode keyword must resolve to Normal, emitting no PushBackgroundBlend");
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "unknown mode must not emit PushMixBlendMode either");
        }

        // ── 9. Base color baked into PushBackgroundBlend ──────────────────────

        [Test]
        public void Push_background_blend_carries_background_color_as_base() {
            // The element declares background-color:white. The PushBackgroundBlend
            // command bakes this as the compositing base (CSS Compositing 1 §9:
            // "the element's background painting area is the compositing backdrop").
            // White in sRGB = LinearColor(1,1,1,1) in linear space.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background-color: white;
                     background-image: linear-gradient(red, blue);
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Multiply));
            // White: linear R=G=B=1, A=1. Alpha > 0 confirms the base color was
            // resolved (transparent black means no background-color declared).
            Assert.That(pushes[0].BaseColor.A, Is.EqualTo(1f).Within(1e-4f),
                "base color alpha should be 1.0 for an opaque background-color:white");
        }

        [Test]
        public void Push_background_blend_has_transparent_base_when_no_background_color() {
            // No background-color declared → base is transparent black (0,0,0,0).
            // The shader's §9 formula: Cs' = (1−αb)·Cs + αb·B = Cs (since αb=0),
            // so the blend has no effect — consistent with having no base.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background-image: linear-gradient(red, blue);
                     background-blend-mode: multiply; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = BgBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1));
            Assert.That(pushes[0].BaseColor.A, Is.EqualTo(0f).Within(1e-4f),
                "no background-color → base color alpha must be 0 (transparent black)");
        }

        // ── 10. Stack balance ──────────────────────────────────────────────────

        [Test]
        public void Push_and_pop_counts_are_balanced_for_multi_layer() {
            // Balanced push/pop is a contract for every backend; verify it here
            // at the command-structure level.
            const string css = @"
                #t { width: 100px; height: 80px;
                     background-image: linear-gradient(red, blue),
                                       linear-gradient(green, yellow),
                                       linear-gradient(cyan, magenta);
                     background-blend-mode: screen, multiply, overlay; }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(BgBlendPushes(cmds).Count, Is.EqualTo(BgBlendPops(cmds).Count),
                "PushBackgroundBlend and PopBackgroundBlend counts must be equal (balanced stack)");
        }
    }
}
