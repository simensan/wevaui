using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Regression: in chat.html `<div class='bubble-meta'>12:02 · <span class='read'>read</span></div>`,
    // the painter must resolve the SPAN's `color` for "read" rather than the
    // parent block's `color`. This pins the cascade-→-TextRun.Style-→-paint
    // pipeline so that
    //   1. The span's ComputedStyle has color: var(--green) → #22c55e.
    //   2. The TextRun emitted under the span carries that ComputedStyle.
    //   3. EmitTextRun emits a DrawTextCommand whose Color reflects the span's
    //      color, NOT the parent block's color.
    public class NestedSpanColorTests {
        [Test]
        public void Inner_span_textrun_carries_span_color_not_parent_color() {
            var (root, _, _) = Build(
                "<div class='m'>12:02 <span class='r'>read</span></div>",
                ".m { color: white; } .r { color: green; }",
                800);

            TextRun readRun = null;
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr && tr.Text != null && tr.Text.Contains("read")) { readRun = tr; break; }
            }
            Assert.That(readRun, Is.Not.Null, "TextRun for 'read' must exist");
            Assert.That(readRun.Style?.Get("color"), Is.EqualTo("green"),
                "Inner span's TextRun must carry the span's color, not the parent block's color");
        }

        [Test]
        public void Chat_bubble_meta_read_span_paints_distinct_green_drawtext() {
            // Reproduces the chat.html structure exactly:
            //   .bubble.me { color: white; }
            //   .bubble.me .bubble-meta { color: rgba(255,255,255,0.75); }
            //   .bubble-meta .read { color: var(--green); }
            const string css = @"
                :root { --green: #22c55e; --ink-dim: #64748b; }
                body { font-size: 14px; color: white; }
                .bubble.me { color: white; }
                .bubble-meta { color: var(--ink-dim); font-size: 11px; }
                .bubble.me .bubble-meta { color: rgba(255, 255, 255, 0.75); }
                .bubble-meta .read { color: var(--green); font-weight: 600; }
            ";
            const string html =
                "<body><div class='bubble me'>" +
                "<div class='bubble-meta'>12:02 · <span class='read'>read</span></div>" +
                "</div></body>";
            var (root, _, _) = Build(html, css, 800);

            var converter = new BoxToPaintConverter();
            var list = converter.Convert(root);

            DrawTextCommand readCmd = null;
            DrawTextCommand metaCmd = null;
            foreach (var c in list.Commands) {
                if (c is DrawTextCommand dt && dt.Text != null) {
                    if (dt.Text.Trim() == "read") readCmd = dt;
                    else if (dt.Text.Contains("12:02")) metaCmd = dt;
                }
            }
            Assert.That(readCmd, Is.Not.Null, "DrawTextCommand for span 'read' must exist");
            Assert.That(metaCmd, Is.Not.Null, "DrawTextCommand for meta '12:02' must exist");

            // var(--green) = #22c55e → linear ~(0.016, 0.558, 0.112).
            Assert.That(readCmd.Color.G, Is.GreaterThan(readCmd.Color.R + readCmd.Color.B),
                "'read' must paint green-dominant; got " + readCmd.Color);
            // Meta must be visibly different from the span's green.
            Assert.That(metaCmd.Color.R, Is.GreaterThan(0.5f),
                "'12:02' must paint with a white-ish color; got " + metaCmd.Color);
        }
    }
}
