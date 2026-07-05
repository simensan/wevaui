using System;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Forms;
using Weva.Layout.Boxes;
using Weva.Paint;

namespace Weva.Tests.Forms {
    // Coverage for the LA1 partial fix on InputRenderer's hot paths. Five
    // `style.Get(string)` probes in InputRenderer.cs (placeholder color,
    // selection background, caret color, accent color, font size) now read
    // via CssProperties.*Id constants instead of doing a name-to-id
    // dictionary probe per paint. These tests pin:
    //   1. Behavioural parity — a populated int-id slot is observed by the
    //      renderer the same way the string-keyed read used to see it
    //      (placeholder picks up `color: red` via the int-id path).
    //   2. Fallback parity — when caret-color is unset / `auto` the caret
    //      still derives from currentColor (the renderer's documented
    //      fallback). Regression-pin for the int-id Get returning null for
    //      unoccupied slots without throwing.
    //   3. Allocation cleanliness — 100 AppendOverlays calls on a typed
    //      input allocate within a tight budget. Each old call site went
    //      through CssProperties.GetId(string) per paint; the int-id path
    //      indexes ComputedStyle.values directly. Painting allocates
    //      command objects regardless — the budget is set above that floor
    //      so a regression that re-introduces dict probes would still
    //      blow it.
    public class InputRendererPropertyIdTests {
        static BlockBox MakeBox(Element e, double w, double h) {
            var b = new BlockBox();
            b.Element = e;
            b.Style = new ComputedStyle(e);
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            return b;
        }

        static InputRenderer.TextWidthFunc Mono(double charWidth) {
            return (text, fs) => (text?.Length ?? 0) * charWidth;
        }

        // Parity 1: a placeholder ComputedStyle that sets `color` via the
        // int-id slot must flow into the painted placeholder color the
        // same way the legacy string-keyed read did. We assert against red
        // (R dominant, G/B near zero) rather than a hard-coded triplet so
        // this stays robust to sRGB → linear conversion details.
        [Test]
        public void Placeholder_color_resolves_via_int_id_path() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            e.SetAttribute("placeholder", "hint");
            var box = MakeBox(e, 200, 24);
            var placeholderStyle = new ComputedStyle(e);
            // Write through the int-id slot so the test would fail if the
            // renderer's read had been hard-coded to a different id (or had
            // been left on the string-keyed dictionary path that the
            // CssProperties registry would still serve, masking a typo).
            placeholderStyle.Set(CssProperties.ColorId, "red");
            var state = new InputState(e); // empty value → placeholder paints
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8), _ => placeholderStyle, null);
            var text = list.Commands.OfType<DrawTextCommand>().FirstOrDefault();
            Assert.That(text, Is.Not.Null, "placeholder text quad should be emitted");
            Assert.That(text.Color.R, Is.GreaterThan(0.5f));
            Assert.That(text.Color.G, Is.LessThan(0.1f));
            Assert.That(text.Color.B, Is.LessThan(0.1f));
        }

        // Parity 2 / regression pin: unset caret-color must fall through to
        // currentColor. The int-id ResolveCaretColor read must return null
        // for an unoccupied slot (not throw, not surface stale array data),
        // and the empty/auto branch must then defer to color. We set color
        // through the int-id slot too so a bug that confused the two ids
        // would surface.
        [Test]
        public void Caret_color_unset_falls_through_to_color_via_int_id_path() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            box.Style.Set(CssProperties.ColorId, "blue");
            // caret-color intentionally left unset — the int-id read must
            // observe an unoccupied slot and the renderer must then derive
            // the caret brush from currentColor. This matches the CSS UI 4
            // §5.4 "auto / initial" fallback.
            var state = new InputState(e) { Value = "x" };
            state.SetCaret(1);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            var caret = list.Commands.OfType<FillRectCommand>().FirstOrDefault(c => c.Bounds.Width == 1);
            Assert.That(caret, Is.Not.Null, "caret rect should still paint when caret-color is unset");
            // currentColor = blue: B dominates, R near zero.
            Assert.That(caret.Brush.Color.B, Is.GreaterThan(0.5f));
            Assert.That(caret.Brush.Color.R, Is.LessThan(0.1f));
        }

        // Allocation pin: 100 AppendOverlays calls on a typed text input with
        // populated style slots must stay within a bounded budget. The five
        // migrated probes (placeholder color, selection bg, caret color,
        // accent color, font size) are read on every paint. The int-id Get
        // is allocation-free; the only growth here is the paint command list
        // (DrawTextCommand / FillRectCommand) which the renderer always
        // emits. Budget is comfortably above the per-call paint floor so a
        // regression that re-introduces string-keyed reads (dict probes,
        // substring allocs in shorthand expansion, ...) would push past it.
        [Test]
        public void AppendOverlays_100_calls_on_typed_input_stays_within_budget() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            // Populate every migrated property's int-id slot so each paint
            // exercises the converted Get path (not the unoccupied-slot
            // early-out, which would skip the read entirely).
            box.Style.Set(CssProperties.ColorId, "black");
            box.Style.Set(CssProperties.CaretColorId, "red");
            box.Style.Set(CssProperties.AccentColorId, "blue");
            box.Style.Set(CssProperties.FontSizeId, "14px");
            var state = new InputState(e) { Value = "hello" };
            state.SetSelection(1, 3);
            var widthFn = Mono(8);

            // Warmup: prime any lazy-materialised parsed-value caches and
            // first-call JIT so the measured window reflects steady-state.
            for (int i = 0; i < 10; i++) {
                var warm = new PaintList();
                InputRenderer.AppendOverlays(e, box, state, warm, widthFn);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                var list = new PaintList();
                InputRenderer.AppendOverlays(e, box, state, list, widthFn);
                // Touch the list to defeat dead-code elimination.
                if (list.Commands.Count < 0) throw new Exception("unreachable");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            // Budget covers 100 fresh PaintList + per-call command objects
            // (caret rect, selection rect, ...). The int-id reads add zero
            // to that floor. A regression that puts string-keyed Get probes
            // back would push allocation past this ceiling.
            Assert.That(delta, Is.LessThan(256 * 1024),
                $"100 AppendOverlays calls allocated {delta} bytes — the int-id InputRenderer reads regressed.");
        }
    }
}
