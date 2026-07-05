using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Forms;
using Weva.Layout.Boxes;
using Weva.Paint;

namespace Weva.Tests.Forms {
    public class InputRendererTests {
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

        [Test]
        public void Text_input_emits_caret_rect() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            var state = new InputState(e) { Value = "hi" };
            state.SetCaret(2);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            Assert.That(list.Commands.OfType<FillRectCommand>().Any(c => c.Bounds.Width == 1), Is.True);
        }

        [Test]
        public void Selection_emits_filled_rect_between_anchor_and_caret() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            var state = new InputState(e) { Value = "abcdef" };
            state.SetSelection(1, 4);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            // 1 selection rect + 1 caret rect
            var rects = list.Commands.OfType<FillRectCommand>().ToList();
            Assert.That(rects.Any(r => r.Bounds.Width > 1), Is.True);
        }

        [Test]
        public void Checked_checkbox_emits_check_glyph() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            e.SetAttribute("checked", "");
            var box = MakeBox(e, 14, 14);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            Assert.That(list.Commands.OfType<FillRectCommand>().Any(), Is.True);
        }

        [Test]
        public void Unchecked_checkbox_emits_no_glyph() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            var box = MakeBox(e, 14, 14);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            Assert.That(list.Commands.Count, Is.EqualTo(0));
        }

        [Test]
        public void Checked_radio_emits_inner_dot_with_round_radii() {
            var e = new Element("input");
            e.SetAttribute("type", "radio");
            e.SetAttribute("checked", "");
            var box = MakeBox(e, 14, 14);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var rect = list.Commands.OfType<FillRectCommand>().FirstOrDefault();
            Assert.That(rect, Is.Not.Null);
            Assert.That(rect.Radii.IsZero, Is.False);
        }

        [Test]
        public void Caret_color_property_overrides_default_caret_brush() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            box.Style.Set("caret-color", "red");
            var state = new InputState(e) { Value = "x" };
            state.SetCaret(1);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            // Caret rect is the 1px-wide rect; its brush should be red, not black.
            var caret = list.Commands.OfType<FillRectCommand>().FirstOrDefault(c => c.Bounds.Width == 1);
            Assert.That(caret, Is.Not.Null);
            var brush = caret.Brush;
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.SolidColor));
            // Red in linear space: R should dominate, G/B should be zero.
            Assert.That(brush.Color.R, Is.GreaterThan(0.5f));
            Assert.That(brush.Color.G, Is.LessThan(0.1f));
            Assert.That(brush.Color.B, Is.LessThan(0.1f));
        }

        [Test]
        public void Caret_color_auto_falls_back_to_current_color() {
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            box.Style.Set("color", "blue");
            box.Style.Set("caret-color", "auto");
            var state = new InputState(e) { Value = "x" };
            state.SetCaret(1);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            var caret = list.Commands.OfType<FillRectCommand>().FirstOrDefault(c => c.Bounds.Width == 1);
            Assert.That(caret, Is.Not.Null);
            // currentColor = blue: B dominates.
            Assert.That(caret.Brush.Color.B, Is.GreaterThan(0.5f));
            Assert.That(caret.Brush.Color.R, Is.LessThan(0.1f));
        }

        [Test]
        public void Select_emits_caret_indicator_bar() {
            var e = new Element("select");
            var box = MakeBox(e, 200, 24);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            Assert.That(list.Commands.OfType<FillRectCommand>().Any(), Is.True);
        }

        [Test]
        public void accent_color_resolves_to_red_on_checkbox() {
            var e = new Element("input");
            e.SetAttribute("type", "checkbox");
            e.SetAttribute("checked", "");
            var box = MakeBox(e, 14, 14);
            box.Style.Set("accent-color", "red");
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var glyph = list.Commands.OfType<FillRectCommand>().FirstOrDefault();
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph.Brush.Kind, Is.EqualTo(BrushKind.SolidColor));
            Assert.That(glyph.Brush.Color.R, Is.GreaterThan(0.5f));
            Assert.That(glyph.Brush.Color.G, Is.LessThan(0.1f));
            Assert.That(glyph.Brush.Color.B, Is.LessThan(0.1f));
        }

        [Test]
        public void accent_color_resolves_to_blue_on_radio() {
            var e = new Element("input");
            e.SetAttribute("type", "radio");
            e.SetAttribute("checked", "");
            var box = MakeBox(e, 14, 14);
            box.Style.Set("accent-color", "blue");
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var glyph = list.Commands.OfType<FillRectCommand>().FirstOrDefault();
            Assert.That(glyph, Is.Not.Null);
            Assert.That(glyph.Brush.Kind, Is.EqualTo(BrushKind.SolidColor));
            Assert.That(glyph.Brush.Color.B, Is.GreaterThan(0.5f));
            Assert.That(glyph.Brush.Color.R, Is.LessThan(0.1f));
            Assert.That(glyph.Brush.Color.G, Is.LessThan(0.1f));
        }

        [Test]
        public void accent_color_auto_falls_back_to_platform_default() {
            // `accent-color: auto` and an unset value must paint identically:
            // the InputRenderer's hard-coded indigo. We compare the explicit-
            // auto box against an unstyled control to lock that invariant
            // without hard-coding the platform RGB triplet here (the renderer
            // owns the default).
            var styled = new Element("input");
            styled.SetAttribute("type", "checkbox");
            styled.SetAttribute("checked", "");
            var styledBox = MakeBox(styled, 14, 14);
            styledBox.Style.Set("accent-color", "auto");
            var styledList = new PaintList();
            InputRenderer.AppendOverlays(styled, styledBox, null, styledList, null);
            var styledGlyph = styledList.Commands.OfType<FillRectCommand>().First();

            var bare = new Element("input");
            bare.SetAttribute("type", "checkbox");
            bare.SetAttribute("checked", "");
            var bareBox = MakeBox(bare, 14, 14);
            var bareList = new PaintList();
            InputRenderer.AppendOverlays(bare, bareBox, null, bareList, null);
            var bareGlyph = bareList.Commands.OfType<FillRectCommand>().First();

            Assert.That(styledGlyph.Brush.Color.R, Is.EqualTo(bareGlyph.Brush.Color.R).Within(1e-6));
            Assert.That(styledGlyph.Brush.Color.G, Is.EqualTo(bareGlyph.Brush.Color.G).Within(1e-6));
            Assert.That(styledGlyph.Brush.Color.B, Is.EqualTo(bareGlyph.Brush.Color.B).Within(1e-6));
            Assert.That(styledGlyph.Brush.Color.A, Is.EqualTo(bareGlyph.Brush.Color.A).Within(1e-6));
        }

        [Test]
        public void Placeholder_renders_when_input_value_is_empty() {
            // Empty input field with `placeholder="Type here"` should
            // produce a DrawTextCommand carrying the placeholder string.
            // Without an InputState value (default empty), the renderer's
            // empty-value branch must fire and emit one text quad.
            var e = new Element("input");
            e.SetAttribute("type", "text");
            e.SetAttribute("placeholder", "Type here");
            var box = MakeBox(e, 200, 24);
            var state = new InputState(e); // Value defaults to empty
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            var textCmds = list.Commands.OfType<DrawTextCommand>().ToList();
            Assert.That(textCmds.Count, Is.EqualTo(1), "exactly one placeholder text quad should be emitted");
            Assert.That(textCmds[0].Text, Is.EqualTo("Type here"));
        }

        [Test]
        public void Placeholder_does_not_render_when_input_has_value() {
            // The placeholder must not paint over the user's typed text —
            // browsers hide the placeholder the moment value becomes non-empty.
            var e = new Element("input");
            e.SetAttribute("type", "text");
            e.SetAttribute("placeholder", "Type here");
            var box = MakeBox(e, 200, 24);
            var state = new InputState(e) { Value = "abc" };
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8));
            Assert.That(list.Commands.OfType<DrawTextCommand>().Any(), Is.False);
        }

        [Test]
        public void Selection_uses_cascaded_background_when_selection_style_provided() {
            // ::selection { background-color: yellow; } resolved by the
            // cascade flows through the PseudoStyleResolver hook into the
            // selection rect's brush. Yellow = R+G dominate, B near zero.
            var e = new Element("input");
            e.SetAttribute("type", "text");
            var box = MakeBox(e, 200, 24);
            var state = new InputState(e) { Value = "abcdef" };
            state.SetSelection(1, 4);
            var selStyle = new ComputedStyle(e);
            selStyle.Set("background-color", "yellow");
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, state, list, Mono(8), null, _ => selStyle);
            var selRect = list.Commands.OfType<FillRectCommand>().FirstOrDefault(c => c.Bounds.Width > 1);
            Assert.That(selRect, Is.Not.Null);
            Assert.That(selRect.Brush.Color.R, Is.GreaterThan(0.5f));
            Assert.That(selRect.Brush.Color.G, Is.GreaterThan(0.5f));
            Assert.That(selRect.Brush.Color.B, Is.LessThan(0.1f));
        }

        [Test]
        public void Password_input_selection_width_is_independent_of_value_chars() {
            // Selection and caret math must operate on the masked render, not
            // the raw value: clicking between 'a' and 'b' in "abcd" with a
            // password input must produce the same selection rectangle as
            // clicking between 'X' and 'Y' in "XYZW", because the user sees
            // bullets in both cases. Width function is monospace so equal
            // length → equal width.
            var passwordEl = new Element("input");
            passwordEl.SetAttribute("type", "password");
            var passwordBox = MakeBox(passwordEl, 200, 24);
            var passwordState = new InputState(passwordEl) { Value = "abcd" };
            passwordState.SetSelection(1, 3);
            var passwordList = new PaintList();
            InputRenderer.AppendOverlays(passwordEl, passwordBox, passwordState, passwordList, Mono(8));

            var textEl = new Element("input");
            textEl.SetAttribute("type", "text");
            var textBox = MakeBox(textEl, 200, 24);
            var textState = new InputState(textEl) { Value = "XYZW" };
            textState.SetSelection(1, 3);
            var textList = new PaintList();
            InputRenderer.AppendOverlays(textEl, textBox, textState, textList, Mono(8));

            // Selection rect width: 2 chars * 8 px = 16 px, identical regardless of plaintext.
            var passwordSel = passwordList.Commands.OfType<FillRectCommand>().First(c => c.Bounds.Width > 1);
            var textSel = textList.Commands.OfType<FillRectCommand>().First(c => c.Bounds.Width > 1);
            Assert.That(passwordSel.Bounds.Width, Is.EqualTo(textSel.Bounds.Width).Within(1e-6));
            Assert.That(passwordSel.Bounds.Width, Is.EqualTo(16).Within(1e-6));

            // Caret X for an unmasked plaintext input ending at index 4 sits at
            // the same X as the password input: both are 4 monospace cells
            // wide because the password mask uses one bullet per code unit.
            var passwordState2 = new InputState(passwordEl) { Value = "abcd" };
            passwordState2.SetCaret(4);
            var pwList2 = new PaintList();
            InputRenderer.AppendOverlays(passwordEl, passwordBox, passwordState2, pwList2, Mono(8));
            var pwCaret = pwList2.Commands.OfType<FillRectCommand>().First(c => c.Bounds.Width == 1);
            Assert.That(pwCaret.Bounds.X, Is.EqualTo(passwordBox.PaddingLeft + passwordBox.BorderLeft + 32).Within(1e-6),
                "caret index must match the unmasked text length × bullet width");
        }
    }
}
