using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // TG2 — OutlineResolver direct coverage.
    // CSS UI 4 §7: resolves outline-{style,width,color,offset} into a
    // single BorderEdge for paint, plus a single-slot per-style memo
    // keyed on (style, Version, BaseFontSizePx).
    public class OutlineResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        [Test]
        public void Solid_2px_red_resolves_all_four_properties() {
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "red");
            bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out var offset);
            Assert.That(has, Is.True);
            Assert.That(edge.Style, Is.EqualTo(BorderStyle.Solid));
            Assert.That(edge.Width, Is.EqualTo(2.0).Within(1e-6));
            // red = (1,0,0) in sRGB — the resolver returns linear color.
            Assert.That(edge.Color.R, Is.GreaterThan(0.5f));
            Assert.That(edge.Color.G, Is.LessThan(0.05f));
            Assert.That(edge.Color.B, Is.LessThan(0.05f));
            // No outline-offset declared → 0.
            Assert.That(offset, Is.EqualTo(0.0).Within(1e-6));
        }

        [Test]
        public void Style_none_returns_no_edge() {
            var s = Style();
            s.Set("outline-style", "none");
            s.Set("outline-width", "5px");
            s.Set("outline-color", "red");
            bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out var offset);
            Assert.That(has, Is.False);
            Assert.That(edge.Style, Is.EqualTo(BorderStyle.None));
            Assert.That(edge.Width, Is.EqualTo(0.0).Within(1e-6));
            Assert.That(offset, Is.EqualTo(0.0).Within(1e-6));
        }

        [Test]
        public void Missing_outline_style_returns_no_edge() {
            // No outline-style set at all — initial is `none`, so resolver
            // must report no outline regardless of width / color.
            var s = Style();
            s.Set("outline-width", "5px");
            s.Set("outline-color", "blue");
            bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out var offset);
            Assert.That(has, Is.False);
            Assert.That(edge.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Positive_and_negative_outline_offset_resolve() {
            var sPos = Style();
            sPos.Set("outline-style", "solid");
            sPos.Set("outline-width", "1px");
            sPos.Set("outline-offset", "4px");
            Assert.That(
                OutlineResolver.TryResolve(sPos, Ctx(), out _, out var posOffset),
                Is.True);
            Assert.That(posOffset, Is.EqualTo(4.0).Within(1e-6));

            var sNeg = Style();
            sNeg.Set("outline-style", "solid");
            sNeg.Set("outline-width", "1px");
            sNeg.Set("outline-offset", "-3px");
            Assert.That(
                OutlineResolver.TryResolve(sNeg, Ctx(), out _, out var negOffset),
                Is.True);
            Assert.That(negOffset, Is.EqualTo(-3.0).Within(1e-6));
        }

        [Test]
        public void Repeated_resolve_on_same_style_returns_equal_edge_via_memo() {
            // OutlineResolver has a single-slot static memo keyed on
            // (style, Version, BaseFontSizePx). Two consecutive
            // TryResolve calls with the same ComputedStyle must return
            // the identical BorderEdge + offset payload. Since
            // BorderEdge is a readonly struct, "same instance" maps to
            // value-equality on every field of the struct returned by
            // both calls.
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "3px");
            s.Set("outline-color", "green");
            s.Set("outline-offset", "2px");

            bool has1 = OutlineResolver.TryResolve(s, Ctx(), out var edge1, out var off1);
            bool has2 = OutlineResolver.TryResolve(s, Ctx(), out var edge2, out var off2);
            Assert.That(has1, Is.True);
            Assert.That(has2, Is.True);
            // BorderEdge implements value equality on (Style, Width, Color).
            // A second resolve that did not hit the memo would have run
            // ColorResolver again, but the parsed cache + identical inputs
            // mean the output is bit-equal either way; the memo test pins
            // the contract that the resolver returns a stable answer for
            // an unchanged style.
            Assert.That(edge2.Equals(edge1), Is.True,
                "Repeated resolve on unchanged style must yield equal BorderEdge");
            Assert.That(off2, Is.EqualTo(off1).Within(1e-9));
            Assert.That(edge2.Style, Is.EqualTo(BorderStyle.Solid));
            Assert.That(edge2.Width, Is.EqualTo(3.0).Within(1e-6));
        }

        [Test]
        public void Style_change_bumps_version_and_invalidates_memo() {
            // Mutating the style after a first resolve must produce a
            // fresh result, not the stale memoized payload (the memo key
            // includes style.Version which Set() bumps).
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "1px");
            s.Set("outline-color", "red");
            bool has1 = OutlineResolver.TryResolve(s, Ctx(), out var edge1, out _);
            Assert.That(has1, Is.True);
            Assert.That(edge1.Width, Is.EqualTo(1.0).Within(1e-6));
            long versionBefore = s.Version;

            // Change a property → Version bumps → memo key misses.
            s.Set("outline-width", "6px");
            Assert.That(s.Version, Is.Not.EqualTo(versionBefore),
                "Set must bump Version so the memo key invalidates");

            bool has2 = OutlineResolver.TryResolve(s, Ctx(), out var edge2, out _);
            Assert.That(has2, Is.True);
            Assert.That(edge2.Width, Is.EqualTo(6.0).Within(1e-6),
                "Memo must have invalidated and re-resolved with the new width");
            Assert.That(edge2.Equals(edge1), Is.False);

            // Flipping style:none on top must drop the outline entirely
            // on the next resolve (negative-cache memo path).
            s.Set("outline-style", "none");
            bool has3 = OutlineResolver.TryResolve(s, Ctx(), out var edge3, out _);
            Assert.That(has3, Is.False);
            Assert.That(edge3.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Width_zero_returns_no_edge() {
            var s = Style();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "0px");
            s.Set("outline-color", "red");
            bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out _);
            Assert.That(has, Is.False);
            Assert.That(edge.Style, Is.EqualTo(BorderStyle.None));
        }

        [Test]
        public void Width_keyword_thin_medium_thick_map_to_1_3_5() {
            foreach (var (kw, px) in new[] { ("thin", 1.0), ("medium", 3.0), ("thick", 5.0) }) {
                var s = Style();
                s.Set("outline-style", "solid");
                s.Set("outline-width", kw);
                bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out _);
                Assert.That(has, Is.True, "outline-width:" + kw);
                Assert.That(edge.Width, Is.EqualTo(px).Within(1e-6),
                    "outline-width:" + kw + " → " + px + "px");
            }
        }

        [Test]
        public void Invert_color_falls_through_to_currentcolor() {
            // CSS UI 4: `invert` is the initial outline-color. The v1
            // paint pipeline has no invert primitive, so the resolver
            // approximates it by falling through to currentColor.
            var s = Style();
            s.Set("color", "blue");
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "invert");
            bool has = OutlineResolver.TryResolve(s, Ctx(), out var edge, out _);
            Assert.That(has, Is.True);
            // currentColor = blue → B channel should dominate.
            Assert.That(edge.Color.B, Is.GreaterThan(0.5f));
            Assert.That(edge.Color.R, Is.LessThan(0.05f));
        }

        [Test]
        public void Null_style_returns_false() {
            bool has = OutlineResolver.TryResolve(null, Ctx(), out var edge, out var offset);
            Assert.That(has, Is.False);
            Assert.That(edge.Style, Is.EqualTo(BorderStyle.None));
            Assert.That(offset, Is.EqualTo(0.0).Within(1e-6));
        }
    }
}
