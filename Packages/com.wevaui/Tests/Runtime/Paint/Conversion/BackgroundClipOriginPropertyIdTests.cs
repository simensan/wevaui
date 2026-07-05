using System;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // Coverage for the P10 fix: BackgroundClipOrigin.Resolve now reads
    // `background-clip` / `background-origin` via CssProperties.BackgroundClipId
    // and BackgroundOriginId instead of string-keyed style.Get probes. These
    // tests pin:
    //   1. Behavioural parity — the int-id read path returns the same rects
    //      as the legacy string-keyed path did for every spec keyword
    //      (border-box / padding-box / content-box) AND for the multi-layer
    //      comma-separated case (FirstLayer wins).
    //   2. Allocation cleanliness — 100 Resolve calls allocate near-zero
    //      bytes for a steady-state computed style. The string-keyed path
    //      went through CssProperties.GetId(string) for every probe; the
    //      int-keyed path indexes the values[] array directly.
    public class BackgroundClipOriginPropertyIdTests {
        static BlockBox MakeBox() {
            var bb = new BlockBox();
            bb.Width = 200; bb.Height = 100;
            bb.BorderLeft = 4; bb.BorderTop = 4; bb.BorderRight = 4; bb.BorderBottom = 4;
            bb.PaddingLeft = 8; bb.PaddingTop = 8; bb.PaddingRight = 8; bb.PaddingBottom = 8;
            return bb;
        }

        // Parity matrix: for every recognised keyword pair, the int-id path
        // must produce identical paint / origin rects to the values we would
        // have gotten from the prior string-keyed path. We assert against the
        // hand-computed expected rects (border / padding / content insets) so
        // the test would fail if the migration accidentally swapped ids or
        // started returning the wrong default. Iterated inline rather than
        // expressed via [TestCase] so reflection-based runners (lite test
        // harness in Tools/TestVerifyAll) cover every row.
        [Test]
        public void Resolve_parity_matches_string_keyword_table() {
            // (clip, origin, pX, pY, pW, pH, oX, oY, oW, oH)
            var cases = new[] {
                ("border-box",  "border-box",  0.0,  0.0, 200.0, 100.0,  0.0,  0.0, 200.0, 100.0),
                ("border-box",  "padding-box", 0.0,  0.0, 200.0, 100.0,  4.0,  4.0, 192.0,  92.0),
                ("border-box",  "content-box", 0.0,  0.0, 200.0, 100.0, 12.0, 12.0, 176.0,  76.0),
                ("padding-box", "border-box",  4.0,  4.0, 192.0,  92.0,  0.0,  0.0, 200.0, 100.0),
                ("padding-box", "padding-box", 4.0,  4.0, 192.0,  92.0,  4.0,  4.0, 192.0,  92.0),
                ("content-box", "content-box",12.0, 12.0, 176.0,  76.0, 12.0, 12.0, 176.0,  76.0),
            };
            foreach (var (clip, origin, pX, pY, pW, pH, oX, oY, oW, oH) in cases) {
                var box = MakeBox();
                var style = new ComputedStyle(new Element("div"));
                style.Set(CssProperties.BackgroundClipId, clip);
                style.Set(CssProperties.BackgroundOriginId, origin);
                BackgroundClipOrigin.Resolve(style, box, out var paint, out var originRect);
                Assert.That(paint.X, Is.EqualTo(pX), $"paint.X for clip={clip}");
                Assert.That(paint.Y, Is.EqualTo(pY), $"paint.Y for clip={clip}");
                Assert.That(paint.Width, Is.EqualTo(pW), $"paint.Width for clip={clip}");
                Assert.That(paint.Height, Is.EqualTo(pH), $"paint.Height for clip={clip}");
                Assert.That(originRect.X, Is.EqualTo(oX), $"origin.X for origin={origin}");
                Assert.That(originRect.Y, Is.EqualTo(oY), $"origin.Y for origin={origin}");
                Assert.That(originRect.Width, Is.EqualTo(oW), $"origin.Width for origin={origin}");
                Assert.That(originRect.Height, Is.EqualTo(oH), $"origin.Height for origin={origin}");
            }
        }

        // The multi-layer FirstLayer split path still works through the
        // int-id Get(). Authors writing `background-clip: padding-box, border-box`
        // get padding-box (the first layer's value) per CSS Backgrounds 3 §3.7.
        [Test]
        public void Resolve_honours_first_layer_for_comma_separated_values() {
            var box = MakeBox();
            var style = new ComputedStyle(new Element("div"));
            style.Set("background-clip", "padding-box, border-box");
            style.Set("background-origin", "content-box, padding-box");
            BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
            // First layer of clip == padding-box → inset by border widths.
            Assert.That(paint.X, Is.EqualTo(4));
            Assert.That(paint.Width, Is.EqualTo(192));
            // First layer of origin == content-box → inset by border + padding.
            Assert.That(origin.X, Is.EqualTo(12));
            Assert.That(origin.Width, Is.EqualTo(176));
        }

        // Unset → spec defaults (clip=border-box, origin=padding-box). The
        // int-id Get path must return null for both ids when the slot is not
        // occupied, NOT throw or surface stale array data.
        [Test]
        public void Resolve_unset_style_falls_back_to_spec_defaults() {
            var box = MakeBox();
            var style = new ComputedStyle(new Element("div"));
            BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
            Assert.That(paint.Width, Is.EqualTo(200));
            Assert.That(origin.X, Is.EqualTo(4));
            Assert.That(origin.Width, Is.EqualTo(192));
        }

        // The P10 fix's headline win: removing two CssProperties.GetId(string)
        // dictionary probes per Resolve call. We pin steady-state allocation
        // for 100 resolves with a populated style. The multi-layer comma
        // FirstLayer path still substring-allocs (separately tracked in the
        // adjacency note in CODE_AUDIT_FINDINGS) so we use single-keyword
        // values here to isolate the dict-lookup win.
        [Test]
        public void Resolve_100_calls_allocates_near_zero_bytes() {
            var box = MakeBox();
            var style = new ComputedStyle(new Element("div"));
            style.Set(CssProperties.BackgroundClipId, "padding-box");
            style.Set(CssProperties.BackgroundOriginId, "content-box");

            // Warmup: prime any lazy-materialised parsed-value caches the
            // first call would otherwise pay.
            for (int i = 0; i < 10; i++) {
                BackgroundClipOrigin.Resolve(style, box, out _, out _);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                BackgroundClipOrigin.Resolve(style, box, out var paint, out var origin);
                // Touch results to defeat dead-code elimination.
                if (paint.Width < -1 || origin.Width < -1) throw new Exception("unreachable");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            Assert.That(delta, Is.LessThan(1024),
                $"100 Resolve calls allocated {delta} bytes; expected near-zero after the int-id migration.");
        }
    }
}
