using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // ExactSrgbSourceOver (MixBlendMode ordinal 17) — converter-side tests.
    //
    // Mode 17 is an internal pseudo-mode that BoxToPaintConverter wraps around
    // the background-color FillRect of backdrop-filtered elements whose
    // background-color is translucent (0 < alpha < 1). It fixes the −17..−21
    // blue divergence vs Chrome on glass panels: Chrome composites in sRGB
    // space; the engine renders in linear space; the per-pixel backdrop read
    // from the B24 backdrop machinery lets the shader perform the exact
    // sRGB source-over on the GPU.
    //
    // Scope guard: ONLY backdrop-filtered elements' own background-color.
    //   - Opaque bg-color → NO wrap (opaque src-over is exact in both spaces).
    //   - Translucent bg WITHOUT backdrop-filter → NO wrap (cost: full-screen
    //     blit per batch — too expensive for general fills).
    //   - Gradient/image layers on the same element → NOT wrapped (v1: bg-color
    //     only, the dominant glass pattern).
    //
    // Command-sequence pin: DrawBackdropFilter must precede the wrapped fill
    // (ordering guarantee: the per-batch backdrop refresh captures all prior
    // batches' output, so the blur composited into the color target is visible
    // to the sRGB source-over when it samples _WevaBackdrop).
    //
    // NUnit constraint rules (per project memory):
    //   - NEVER chain .Within() off Is.LessThan / Is.GreaterThan.
    //   - Does.Not.Contain is substring-only; use Has.None.EqualTo for collections.
    //   - Avoid Is.AnyOf.
    public class ExactSrgbSourceOverConverterTests {

        // The feature ships DEFAULT ON (verified game-view-true at a
        // viewport-matched Chrome reference: off=7.8 mean abs err, on=5.0 on
        // deep-idle frames — see EnableExactSrgbGlassCompositing's comment
        // for the measurement-artifact history). SetUp/TearDown pin the flag
        // for isolation against tests that toggle it.
        [SetUp]
        public void EnableFeature() => BoxToPaintConverter.EnableExactSrgbGlassCompositing = true;

        [TearDown]
        public void RestoreFeature() => BoxToPaintConverter.EnableExactSrgbGlassCompositing = true;

        // ── helpers ──────────────────────────────────────────────────────────

        static List<PaintCommand> Paint(string html, string css) {
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            return new BoxToPaintConverter().Convert(root).Commands;
        }

        static List<PushMixBlendModeCommand> PageBlendPushes(List<PaintCommand> cmds)
            => cmds.OfType<PushMixBlendModeCommand>().ToList();

        static List<PopMixBlendModeCommand> PageBlendPops(List<PaintCommand> cmds)
            => cmds.OfType<PopMixBlendModeCommand>().ToList();

        static List<DrawBackdropFilterCommand> BackdropFilterDraws(List<PaintCommand> cmds)
            => cmds.OfType<DrawBackdropFilterCommand>().ToList();

        static List<FillRectCommand> FillRects(List<PaintCommand> cmds)
            => cmds.OfType<FillRectCommand>().ToList();

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

        // ── 0. Cache-replay survival (the live-pipeline bug) ─────────────────

        [Test]
        public void Mode17_wrap_survives_PaintBoxCache_replay_across_frames() {
            // Regression: ReplayTranslated's default arm appended the CACHE-OWNED
            // PushMixBlendModeCommand instance to the live list; the frame-end
            // Painter.Return(list) then Reset() the instance INSIDE the
            // PaintBoxCache, silently degrading the mode to Normal from the
            // second frame on. Live symptom (B3e): a direct converter run
            // emitted 18 mode-17 pushes on glass.html while the document
            // pipeline's batcher saw zero. The replay must RE-RENT a copy.
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var painter = new BoxToPaintConverter();
            for (int frame = 0; frame < 3; frame++) {
                var list = painter.Convert(root);
                var pushes = list.Commands.OfType<PushMixBlendModeCommand>().ToList();
                Assert.That(pushes, Has.Count.EqualTo(1),
                    $"frame {frame}: one mode-17 push expected (cache hit frames included)");
                Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                    $"frame {frame}: the push must still carry ordinal 17 — a Normal here means " +
                    "the frame-end Return reset the cached instance (pool-ownership bug)");
                painter.Return(list); // frame end — resets every pooled command in the list
            }
        }

        [Test]
        public void BackgroundBlend_wrap_survives_PaintBoxCache_replay_across_frames() {
            // Same pool-ownership hazard for B25's PushBackgroundBlendCommand
            // (element-local blend, CSS Compositing 1 §9): mode AND base color
            // must survive cache-hit replay frames.
            const string css = @"
                #t { width: 200px; height: 80px;
                     background: linear-gradient(to right, #ff0000, #0000ff), #ffcc00;
                     background-blend-mode: multiply; }
            ";
            var (root, _, _) = Build("<div id=\"t\"></div>", css, viewportWidth: 400, viewportHeight: 300);
            var painter = new BoxToPaintConverter();
            for (int frame = 0; frame < 3; frame++) {
                var list = painter.Convert(root);
                var pushes = list.Commands.OfType<PushBackgroundBlendCommand>().ToList();
                Assert.That(pushes, Has.Count.EqualTo(1),
                    $"frame {frame}: one background-blend push expected");
                Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.Multiply),
                    $"frame {frame}: blend mode must survive cached replay");
                Assert.That(pushes[0].BaseColor.R, Is.EqualTo(1f).Within(1e-3f),
                    $"frame {frame}: base color (white-ish #ffcc00 red channel) must survive cached replay");
                painter.Return(list);
            }
        }

        // ── 1. Backdrop-filter + translucent bg: mode-17 wrap emitted ────────

        [Test]
        public void Backdrop_filter_plus_translucent_bg_wraps_bg_fill_in_mode17() {
            // Glass-panel pattern: backdrop-filter with rgba background-color.
            // The bg-color fill must be wrapped in Push(ExactSrgbSourceOver) /
            // Pop so the shader performs sRGB source-over against the backdrop.
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = PageBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1),
                "One PushMixBlendMode expected for the bg-color ExactSrgbSourceOver wrap");
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "Push mode must be ExactSrgbSourceOver (ordinal 17)");
            Assert.That(PageBlendPops(cmds), Has.Count.EqualTo(1),
                "Matching PopMixBlendMode must follow the fill");
        }

        [Test]
        public void DrawBackdropFilter_precedes_wrapped_bg_fill_in_command_sequence() {
            // Ordering guarantee: DrawBackdropFilter must be emitted BEFORE the
            // PushMixBlendMode(ExactSrgbSourceOver) so DrainBatches' per-batch
            // backdrop refresh captures the blurred content before the sRGB
            // source-over samples it from _WevaBackdrop.
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            int bdIdx = IndexOf<DrawBackdropFilterCommand>(cmds);
            int pushIdx = IndexOf<PushMixBlendModeCommand>(cmds);
            Assert.That(bdIdx, Is.GreaterThanOrEqualTo(0),
                "DrawBackdropFilterCommand must be present");
            Assert.That(pushIdx, Is.GreaterThanOrEqualTo(0),
                "PushMixBlendModeCommand must be present");
            Assert.That(bdIdx, Is.LessThan(pushIdx),
                "DrawBackdropFilter must precede the PushMixBlendMode(ExactSrgbSourceOver) wrap");
        }

        [Test]
        public void Push_mode17_precedes_and_pop_follows_the_bg_fill() {
            // Structural: PushMixBlendMode → FillRect → PopMixBlendMode in order.
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            int pushIdx = IndexOf<PushMixBlendModeCommand>(cmds);
            int fillIdx = IndexOf<FillRectCommand>(cmds);
            int popIdx  = IndexOf<PopMixBlendModeCommand>(cmds);
            Assert.That(pushIdx, Is.GreaterThanOrEqualTo(0), "PushMixBlendMode must be present");
            Assert.That(fillIdx, Is.GreaterThanOrEqualTo(0), "FillRect must be present");
            Assert.That(popIdx,  Is.GreaterThanOrEqualTo(0), "PopMixBlendMode must be present");
            Assert.That(pushIdx, Is.LessThan(fillIdx),  "Push must precede FillRect");
            Assert.That(fillIdx, Is.LessThan(popIdx),   "FillRect must precede Pop");
        }

        // ── 2. Opaque bg-color → NO wrap ─────────────────────────────────────

        [Test]
        public void Opaque_bg_with_backdrop_filter_does_not_emit_mode17_wrap() {
            // Opaque source-over is identical in sRGB and linear spaces; the
            // extra blit cost is not justified for fully-opaque glass panels.
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-color: rgb(255, 255, 255); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            var pushes = PageBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(0),
                "Opaque bg-color must NOT trigger the mode-17 wrap (no sRGB divergence)");
        }

        // ── 3. Translucent bg WITHOUT backdrop-filter → NO wrap ───────────────

        [Test]
        public void Translucent_bg_without_backdrop_filter_does_not_emit_mode17() {
            // Mode 17 is only valid on the page-backdrop path (it reads
            // _WevaBackdrop). Without backdrop-filter the batcher never flags
            // NeedsBackdropRefresh for this element's batch, so the sample would
            // read a stale/uninitialized texture. Scope guard must prevent the wrap.
            const string css = @"
                #t { width: 200px; height: 80px;
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "Translucent bg WITHOUT backdrop-filter must NOT emit PushMixBlendMode");
        }

        // ── 3b. Translucent bg child-of-glass → mode-17 via counter ──────────

        [Test]
        public void Translucent_bg_child_of_glass_container_emits_mode17() {
            // Extended scope: a non-backdrop-filter element (e.g., .nav-link-on)
            // that follows a DrawBackdropFilter in paint order gets mode-17 because
            // backdropFilterSeenThisConvert > 0 — the RT is already allocated by
            // the glass parent. Fixes the ~11 sRGB count "white frame too dim" bug.
            const string css = @"
                #glass { width: 400px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                #child { width: 100px; height: 30px;
                         background-color: rgba(255, 255, 255, 0.14); }
            ";
            var cmds = Paint("<div id=\"glass\"><div id=\"child\"></div></div>", css);
            var pushes = PageBlendPushes(cmds);
            // Two mode-17 pushes: glass bg-color + child bg-color.
            Assert.That(pushes, Has.Count.EqualTo(2),
                "Both glass element and its translucent-bg child must emit mode-17 wraps");
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "First push (glass bg-color) must be ExactSrgbSourceOver");
            Assert.That(pushes[1].Mode, Is.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "Second push (child bg-color) must be ExactSrgbSourceOver");
        }

        [Test]
        public void Translucent_bg_child_of_glass_push_wraps_fill() {
            // Structural: the child's mode-17 scope must sandwich its FillRect.
            const string css = @"
                #glass { width: 400px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                #child { width: 100px; height: 30px;
                         background-color: rgba(255, 255, 255, 0.14); }
            ";
            var cmds = Paint("<div id=\"glass\"><div id=\"child\"></div></div>", css);
            // Second Push/Fill/Pop triplet belongs to #child.
            int childPushIdx = IndexOf<PushMixBlendModeCommand>(cmds, 1);
            int childFillIdx = IndexOf<FillRectCommand>(cmds, 1);
            int childPopIdx  = IndexOf<PopMixBlendModeCommand>(cmds, 1);
            Assert.That(childPushIdx, Is.GreaterThanOrEqualTo(0), "Child PushMixBlendMode must be present");
            Assert.That(childFillIdx, Is.GreaterThanOrEqualTo(0), "Child FillRect must be present");
            Assert.That(childPopIdx,  Is.GreaterThanOrEqualTo(0), "Child PopMixBlendMode must be present");
            Assert.That(childPushIdx, Is.LessThan(childFillIdx),  "Child Push must precede child FillRect");
            Assert.That(childFillIdx, Is.LessThan(childPopIdx),   "Child FillRect must precede child Pop");
        }

        // ── 4. Gradient layers NOT wrapped ───────────────────────────────────

        [Test]
        public void Gradient_layers_on_backdrop_filtered_element_not_wrapped_in_mode17() {
            // v1 scope: bg-color only (the dominant glass pattern). Gradient
            // layers keep the current behavior — wrapping them in mode 17 would
            // require a per-layer backdrop sample, which is a v2 feature.
            // Use explicit longhands so the cascade unambiguously sets both
            // background-image (gradient) and background-color (translucent solid).
            const string css = @"
                #t { width: 200px; height: 80px;
                     backdrop-filter: blur(8px);
                     background-image: linear-gradient(red, blue);
                     background-color: rgba(255, 255, 255, 0.4); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            // There must be exactly one mode-17 push (for bg-color only).
            var pushes = PageBlendPushes(cmds);
            Assert.That(pushes, Has.Count.EqualTo(1),
                "Only one mode-17 wrap (bg-color) — gradient layers must not be wrapped");
            Assert.That(pushes[0].Mode, Is.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "The single wrap must be ExactSrgbSourceOver");
            // There must be exactly two FillRects: bg-color (inside scope) and
            // gradient (outside scope).
            var fills = FillRects(cmds);
            Assert.That(fills, Has.Count.EqualTo(2),
                "Expected 2 FillRects: one bg-color (inside mode-17 scope) and one gradient");
            // Structural: the gradient FillRect must NOT be between the push and pop.
            // Emission order: bg-color fill is emitted first (lowestVisible = Count-1,
            // li visits highest index first), gradient fill is emitted second.
            // So: ... Push → FillRect(bgColor) → Pop → FillRect(gradient) ...
            int pushIdx = IndexOf<PushMixBlendModeCommand>(cmds, 0);
            int popIdx  = IndexOf<PopMixBlendModeCommand>(cmds, 0);
            int gradFillIdx = IndexOf<FillRectCommand>(cmds, 1); // second FillRect = gradient
            Assert.That(gradFillIdx, Is.GreaterThan(popIdx),
                "Gradient FillRect must be emitted AFTER the mode-17 Pop (not wrapped inside the scope)");
            // Sanity: bg-color fill is inside the scope.
            int bgFillIdx = IndexOf<FillRectCommand>(cmds, 0); // first FillRect = bg-color
            Assert.That(bgFillIdx, Is.GreaterThan(pushIdx),
                "Bg-color FillRect must be after the Push");
            Assert.That(bgFillIdx, Is.LessThan(popIdx),
                "Bg-color FillRect must be before the Pop");
        }

        // ── 5. Border mode-17 extension ──────────────────────────────────────

        [Test]
        public void Translucent_border_in_glass_document_emits_mode17_wrap() {
            // Borders like `border: 1px solid rgba(255,255,255,0.26)` on glass panels
            // are too bright in Unity's linear compositing vs Chrome's sRGB compositing.
            // When the document contains at least one backdrop-filter element, translucent
            // borders must be wrapped in ExactSrgbSourceOver (mode 17).
            const string css = @"
                #glass { width: 200px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                #child { width: 100px; height: 30px;
                         border: 1px solid rgba(255, 255, 255, 0.26); }
            ";
            var cmds = Paint("<div id=\"glass\"><div id=\"child\"></div></div>", css);
            var pushes = PageBlendPushes(cmds);
            // At least two mode-17 pushes: glass bg-color + child border.
            Assert.That(pushes, Has.Count.GreaterThanOrEqualTo(2),
                "Glass bg-color AND translucent border in glass document must both emit mode-17 wraps");
            Assert.That(pushes, Has.All.Property("Mode").EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "All mode-17 pushes must carry ExactSrgbSourceOver");
        }

        [Test]
        public void Opaque_border_in_glass_document_does_not_emit_mode17() {
            // Fully opaque borders are not affected by the linear→sRGB compositing
            // gap (opaque src-over is exact in both spaces). No wrap needed.
            const string css = @"
                #glass { width: 200px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                #child { width: 100px; height: 30px;
                         border: 1px solid rgb(255, 255, 255); }
            ";
            var cmds = Paint("<div id=\"glass\"><div id=\"child\"></div></div>", css);
            // Only the glass element's bg-color wrap should be present (child has opaque border).
            var pushes = PageBlendPushes(cmds);
            // Glass bg-color wrap = 1; child border = opaque = no wrap.
            // Child also has no bg-color, so total = 1.
            Assert.That(pushes, Has.Count.EqualTo(1),
                "Opaque border must NOT emit a mode-17 wrap; only the glass bg-color wrap expected");
        }

        [Test]
        public void Translucent_border_without_glass_does_not_emit_mode17() {
            // Translucent borders on non-glass elements (no backdrop-filter in
            // the document) must NOT get mode-17 — the backdrop RT is not allocated.
            const string css = @"
                #t { width: 100px; height: 30px;
                     border: 1px solid rgba(255, 255, 255, 0.26); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "Translucent border in non-glass document must NOT emit mode-17 wrap");
        }

        // ── 6. Inset shadow mode-17 extension ────────────────────────────────

        [Test]
        public void Translucent_inset_shadow_in_glass_document_emits_mode17() {
            // Inset highlights like `inset 0 1px 0 rgba(255,255,255,0.32)` are
            // too bright in linear compositing on glass panels. Wrap them in mode-17.
            const string css = @"
                #glass { width: 200px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10);
                         box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.32); }
            ";
            var cmds = Paint("<div id=\"glass\"></div>", css);
            var pushes = PageBlendPushes(cmds);
            // bg-color wrap + inset shadow wrap = at least 2.
            Assert.That(pushes, Has.Count.GreaterThanOrEqualTo(2),
                "Translucent inset shadow in glass document must emit mode-17 wrap");
            Assert.That(pushes, Has.All.Property("Mode").EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "All mode-17 pushes must carry ExactSrgbSourceOver");
        }

        [Test]
        public void Translucent_inset_shadow_without_glass_does_not_emit_mode17() {
            // Without backdrop-filter in the document, the backdrop RT is not
            // allocated — mode-17 reads from an uninitialized texture.
            const string css = @"
                #t { width: 100px; height: 30px;
                     box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.32); }
            ";
            var cmds = Paint("<div id=\"t\"></div>", css);
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "Translucent inset shadow in non-glass document must NOT emit mode-17 wrap");
        }

        // ── 7. Text mode-17 extension ─────────────────────────────────────────

        [Test]
        public void Translucent_text_in_glass_document_emits_mode17_wrap() {
            // Text with translucent color (e.g. rgba(244,242,255,0.66)) composited
            // linearly over a dark glass panel is too bright vs Chrome (≈22 counts
            // in the red channel). Mode-17 wrapping makes it sRGB-correct.
            const string css = @"
                #glass { width: 400px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                p { color: rgba(244, 242, 255, 0.66); font-size: 14px; }
            ";
            var cmds = Paint("<div id=\"glass\"><p>Hello</p></div>", css);
            var pushes = PageBlendPushes(cmds);
            // glass bg-color wrap + text wrap = at least 2.
            Assert.That(pushes, Has.Count.GreaterThanOrEqualTo(2),
                "Translucent text in glass document must emit mode-17 wrap");
            Assert.That(pushes, Has.All.Property("Mode").EqualTo(MixBlendMode.ExactSrgbSourceOver),
                "All mode-17 pushes must carry ExactSrgbSourceOver");
        }

        [Test]
        public void Opaque_text_in_glass_document_does_not_emit_mode17() {
            // Fully opaque text color is not affected by the compositing gap.
            const string css = @"
                #glass { width: 400px; height: 80px;
                         backdrop-filter: blur(8px);
                         background-color: rgba(255, 255, 255, 0.10); }
                p { color: rgb(255, 255, 255); font-size: 14px; }
            ";
            var cmds = Paint("<div id=\"glass\"><p>Hello</p></div>", css);
            var pushes = PageBlendPushes(cmds);
            // Only glass bg-color wrap = 1; opaque text produces no extra push.
            Assert.That(pushes, Has.Count.EqualTo(1),
                "Opaque text must NOT emit a mode-17 wrap; only the glass bg-color wrap expected");
        }

        [Test]
        public void Translucent_text_without_glass_does_not_emit_mode17() {
            // Text on non-glass pages (no backdrop-filter anywhere) must not
            // get mode-17 wrapping — the backdrop RT is not allocated.
            const string css = @"
                p { color: rgba(244, 242, 255, 0.66); font-size: 14px; }
            ";
            var cmds = Paint("<p>Hello world</p>", css);
            Assert.That(PageBlendPushes(cmds), Has.Count.EqualTo(0),
                "Translucent text in non-glass document must NOT emit mode-17 wrap");
        }

        // ── 8. Enum ordinal pin ───────────────────────────────────────────────

        [Test]
        public void ExactSrgbSourceOver_enum_ordinal_is_17() {
            // The shader dispatches on the integer ordinal packed into
            // TransformRow0.z. If ordinal 17 changes, the GPU path silently
            // composes with the wrong formula (or falls through to the
            // Weva_BlendFormula pass-through). Pin it here so any reorder fails fast.
            Assert.That((int)MixBlendMode.ExactSrgbSourceOver, Is.EqualTo(17),
                "ExactSrgbSourceOver must remain ordinal 17 — the shader dispatches on this integer");
        }

        [Test]
        public void ExactSrgbSourceOver_is_not_mapped_by_MixBlendModeResolver() {
            // No CSS keyword must ever resolve to ordinal 17. It is internal-only
            // and must be inaccessible from author CSS.
            var resolvedKeywords = new[] {
                "normal", "multiply", "screen", "overlay", "darken", "lighten",
                "color-dodge", "color-burn", "hard-light", "soft-light",
                "difference", "exclusion", "plus-lighter",
                "hue", "saturation", "color", "luminosity",
            };
            foreach (var keyword in resolvedKeywords) {
                var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
                s.Set("mix-blend-mode", keyword);
                var resolved = MixBlendModeResolver.Resolve(s);
                Assert.That(resolved, Is.Not.EqualTo(MixBlendMode.ExactSrgbSourceOver),
                    $"CSS keyword '{keyword}' must not resolve to ExactSrgbSourceOver (ordinal 17)");
                Assert.That((int)resolved, Is.LessThan(17),
                    $"All CSS blend-mode keywords must resolve to ordinal < 17; '{keyword}' = {(int)resolved}");
            }
        }
    }
}
