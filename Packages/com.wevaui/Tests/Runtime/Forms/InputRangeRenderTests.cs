using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Forms;
using Weva.Layout.Boxes;
using Weva.Paint;

namespace Weva.Tests.Forms {
    // Coverage for InputRenderer.DrawRangeTrack — the UA rendering of
    // <input type=range>. It emits, into the supplied PaintList:
    //   • a full-width unfilled GROOVE rect (thin, muted accent),
    //   • an accent FILL rect from the rail start to the thumb centre, and
    //   • a round THUMB knob centred on the value's fractional position.
    // The groove/fill height is min(contentH, 6); the knob diameter is
    // max(railH, min(contentH, 14)) so it always FITS the box (never clips,
    // unlike a fixed 14px thumb overhanging a short box).
    public class InputRangeRenderTests {
        const double BoxW = 200;
        const double BoxH = 20; // contentH 20 → railH 6, knob 14, usable 186

        static BlockBox MakeBox(Element e, double w, double h) {
            var b = new BlockBox();
            b.Element = e;
            b.Style = new ComputedStyle(e);
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            return b;
        }

        static Element MakeRange(string value) {
            var e = new Element("input");
            e.SetAttribute("type", "range");
            e.SetAttribute("min", "0");
            e.SetAttribute("max", "100");
            if (value != null) e.SetAttribute("value", value);
            return e;
        }

        static List<FillRectCommand> Rects(PaintList l) =>
            l.Commands.OfType<FillRectCommand>().ToList();
        // The knob is the only square (width == height) rect.
        static FillRectCommand Thumb(PaintList l) =>
            Rects(l).First(c => System.Math.Abs(c.Bounds.Width - c.Bounds.Height) < 1e-6);
        // The groove is the only full-width (== BoxW) rect.
        static FillRectCommand Groove(PaintList l) =>
            Rects(l).First(c => System.Math.Abs(c.Bounds.Width - BoxW) < 1e-6);

        [Test]
        public void Range_value_50_emits_groove_fill_and_centered_knob() {
            var e = MakeRange("50");
            var box = MakeBox(e, BoxW, BoxH);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);

            var rects = Rects(list);
            Assert.That(rects.Count, Is.EqualTo(3), "groove + fill + knob");

            // railH = min(20,6) = 6; knob = max(6, min(20,14)) = 14; usable = 186.
            // frac 0.5 → cx = 7 + 0.5*186 = 100.
            var thumb = Thumb(list);
            Assert.That(thumb.Bounds.Width, Is.EqualTo(14.0).Within(1e-6),
                "knob fits the box at the 14px default");
            double thumbCx = thumb.Bounds.X + thumb.Bounds.Width * 0.5;
            Assert.That(thumbCx, Is.EqualTo(100.0).Within(1e-6), "value=50 centres the knob");
            Assert.That(thumb.Radii.IsZero, Is.False, "knob is round");

            var groove = Groove(list);
            Assert.That(groove.Bounds.X, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(groove.Bounds.Height, Is.EqualTo(6.0).Within(1e-6), "thin rail");

            var fill = rects.First(c => c != thumb && c != groove);
            Assert.That(fill.Bounds.X, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(fill.Bounds.X + fill.Bounds.Width, Is.EqualTo(thumbCx).Within(1e-6),
                "fill reaches the knob centre");
        }

        [Test]
        public void Range_value_0_knob_at_left() {
            var e = MakeRange("0");
            var box = MakeBox(e, BoxW, BoxH);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var thumb = Thumb(list);
            double thumbCx = thumb.Bounds.X + thumb.Bounds.Width * 0.5;
            Assert.That(thumbCx, Is.EqualTo(7.0).Within(1e-6),
                "value=0 parks the knob centre a thumb-radius (7) from the left");
            Assert.That(thumb.Bounds.X, Is.EqualTo(0.0).Within(1e-6),
                "knob left edge sits at the rail start");
        }

        [Test]
        public void Range_value_100_knob_at_right() {
            var e = MakeRange("100");
            var box = MakeBox(e, BoxW, BoxH);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var thumb = Thumb(list);
            double thumbCx = thumb.Bounds.X + thumb.Bounds.Width * 0.5;
            Assert.That(thumbCx, Is.EqualTo(193.0).Within(1e-6),
                "value=100 pushes the knob to the far end of the usable rail (7 + 186)");
            Assert.That(thumb.Bounds.X + thumb.Bounds.Width, Is.EqualTo(BoxW).Within(1e-6),
                "knob right edge reaches the rail end");
        }

        [Test]
        public void Range_thin_rail_plus_fitting_knob_at_ua_height() {
            // At the UA default height (18px) the rail stays thin (6px) and the
            // knob is a distinct 14px circle that FULLY fits the box — it never
            // overflows/clips (the bug a fixed 14px thumb on a 6px box caused).
            var e = MakeRange("50");
            var box = MakeBox(e, BoxW, 18);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var thumb = Thumb(list);
            Assert.That(thumb.Bounds.Width, Is.EqualTo(14.0).Within(1e-6), "14px knob");
            Assert.That(thumb.Bounds.Y, Is.GreaterThanOrEqualTo(-1e-6), "knob top within the box");
            Assert.That(thumb.Bounds.Y + thumb.Bounds.Height, Is.LessThanOrEqualTo(18.0 + 1e-6),
                "knob bottom within the box — no clip");
            Assert.That(Groove(list).Bounds.Height, Is.EqualTo(6.0).Within(1e-6), "rail stays thin");
        }

        // ── checkbox / radio glyph (shared formula; the LIVE BoxToPaintConverter
        //    EmitCheckboxGlyph / EmitRadioGlyph mirror these) ──────────────────
        static Element MakeInput(string type, bool chec_) {
            var e = new Element("input");
            e.SetAttribute("type", type);
            if (chec_) e.SetAttribute("checked", "");
            return e;
        }

        [Test]
        public void Checkbox_checked_emits_accent_tick_inset_by_2() {
            var e = MakeInput("checkbox", true);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, MakeBox(e, 16, 16), null, list, null);
            var rects = Rects(list);
            Assert.That(rects.Count, Is.EqualTo(1), "checked checkbox paints one tick");
            Assert.That(rects[0].Bounds.X, Is.EqualTo(2.0).Within(1e-6), "inset 2");
            Assert.That(rects[0].Bounds.Width, Is.EqualTo(12.0).Within(1e-6), "16 - 2*2");
        }

        [Test]
        public void Checkbox_unchecked_emits_no_tick() {
            var e = MakeInput("checkbox", false);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, MakeBox(e, 16, 16), null, list, null);
            Assert.That(Rects(list).Count, Is.EqualTo(0), "unchecked checkbox paints nothing");
        }

        [Test]
        public void Radio_checked_emits_round_centered_dot() {
            var e = MakeInput("radio", true);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, MakeBox(e, 16, 16), null, list, null);
            var rects = Rects(list);
            Assert.That(rects.Count, Is.EqualTo(1), "checked radio paints one dot");
            Assert.That(rects[0].Bounds.X, Is.EqualTo(4.0).Within(1e-6), "inset 0.25*16");
            Assert.That(rects[0].Bounds.Width, Is.EqualTo(8.0).Within(1e-6), "16 - 2*4");
            Assert.That(rects[0].Radii.IsZero, Is.False, "dot is round");
        }

        [Test]
        public void Range_default_value_is_midpoint_when_no_value_attr() {
            var e = MakeRange(null);
            var box = MakeBox(e, BoxW, BoxH);
            var list = new PaintList();
            InputRenderer.AppendOverlays(e, box, null, list, null);
            var thumb = Thumb(list);
            double thumbCx = thumb.Bounds.X + thumb.Bounds.Width * 0.5;
            Assert.That(thumbCx, Is.EqualTo(100.0).Within(1e-6),
                "absent value defaults to the [min,max] midpoint");
        }
    }
}
