using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint.Images;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    // A real game's .skill-slot-icon (`<img>` with `position: absolute;
    // inset: 0; width: 100%; height: 100%`) needs to stretch to fill its
    // surrounding .skill-slot. Mostly a regression sentinel — the
    // calculation is identical to <div> children, but img had its own
    // intrinsic-size override path in BoxBuilder and we want to be sure
    // an explicit 100% always wins.
    public class AbsoluteImgFillTests {
        [Test]
        public void Img_with_inset_zero_and_100pct_fills_div_containing_block() {
            const string css = @"
                .cb { position: relative; width: 60px; height: 60px; }
                .ico {
                    position: absolute;
                    inset: 0;
                    width: 100%;
                    height: 100%;
                    display: block;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><img class=\"ico\" src=\"x\" /></div>",
                css, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            Assert.That(ico, Is.Not.Null);
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5));
            Assert.That(ico.Height, Is.EqualTo(60).Within(0.5));
        }

        [Test]
        public void Img_with_inset_zero_inside_bordered_content_box_div_fills_60() {
            // content-box div: width:60 = content; total visible = 64×64
            // (with 2px border each side); padding box = content box = 60.
            // img width:100% = 60.
            const string css = @"
                .cb {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                }
                .ico {
                    position: absolute;
                    inset: 0;
                    width: 100%;
                    height: 100%;
                    display: block;
                }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><img class=\"ico\" src=\"x\" /></div>",
                css, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            Assert.That(ico, Is.Not.Null);
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5));
            Assert.That(ico.Height, Is.EqualTo(60).Within(0.5));
        }

        [Test]
        public void Img_with_inset_zero_inside_border_box_button_resolves_to_padding_box() {
            // A real game's exact case: `* { box-sizing: border-box }` plus a
            // button-slot (UA `button { display: inline-flex }`) whose 60×60
            // INCLUDES the 2px border per border-box. Padding box = 60-2-2
            // = 56; img width:100% = 56. The icon visually sits inside the
            // border. Authors who want the icon to overlap the border need
            // negative insets (e.g. `inset: -2px`) or content-box slots.
            const string css = @"
                * { box-sizing: border-box; }
                button { display: inline-flex; box-sizing: border-box; }
                .cb {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                }
                .ico {
                    position: absolute;
                    inset: 0;
                    width: 100%;
                    height: 100%;
                    display: block;
                }
            ";
            var (root, _, _) = Build(
                "<button class=\"cb\"><img class=\"ico\" src=\"x\" /></button>",
                css, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            Assert.That(ico, Is.Not.Null);
            Assert.That(ico.Width, Is.EqualTo(56).Within(0.5));
            Assert.That(ico.Height, Is.EqualTo(56).Within(0.5));
        }

        [Test]
        public void Img_with_inset_negative_stretches_past_border_even_with_registered_intrinsic_size() {
            // The real-world regression. The icon is `position: absolute;
            // inset: -2px` with NO css width — relies on the
            // four-pinned-no-width stretch-to-fill (width =
            // containing-block + |insetLeft| + |insetRight|) so the
            // image overflows the slot border, gets clipped by the
            // slot's overflow:hidden, and visually fills the rounded
            // outer edge. BoxBuilder.MaybeApplyImgIntrinsicSize used
            // to stamp `box.IntrinsicWidth = source.Width` even on
            // pinned boxes; PositioningPass.HasExplicitDim then
            // treated the intrinsic as "explicit" and skipped the
            // stretch branch, leaving the icon at the slot's padding
            // box (56px in a 60px slot — 4px short).
            const string css = @"
                * { box-sizing: border-box; }
                button { display: inline-flex; box-sizing: border-box; }
                .cb {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                    overflow: hidden;
                }
                .ico {
                    position: absolute;
                    inset: -2px;
                    display: block;
                    object-fit: cover;
                }
            ";
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/skill", new StubSource(256, 256));
            var (root, _, _) = BuildWithRegistry(
                "<button class=\"cb\"><img class=\"ico\" src=\"ui/skill\" /></button>",
                css, reg, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            Assert.That(ico, Is.Not.Null);
            // Slot padding box is 60-2-2 = 56. With inset: -2px on all
            // four sides and no explicit width, the icon spans 56 + 2
            // + 2 = 60 (border-box of the slot).
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5));
            Assert.That(ico.Height, Is.EqualTo(60).Within(0.5));
        }

        [Test]
        public void Img_width_survives_subtree_splice_of_sibling_during_cooldown_pattern() {
            // The real-world cooldown bug. The skill slot has TWO out-of-
            // flow children: the icon (`<img inset:-2>`) and a cooldown
            // sweep (`<div inset:0 style="...">`). The cooldown's
            // `style` attribute changes every frame while a cooldown
            // is ticking — AttributeBinding marks the sweep dirty with
            // Layout (no Structure), the engine takes the subtree-
            // splice path on the sweep, then calls
            // `positioningPass.Run(lastRoot)` on the whole tree. The
            // icon must come out the same size on every subtree-splice
            // round even though it's nominally untouched. This test
            // drives an explicit Layout-only InvalidationTracker for
            // the sibling and asserts the icon is stable across 50
            // splices (mirrors a few seconds of cooldown at 60 fps).
            const string css = @"
                * { box-sizing: border-box; }
                button { display: inline-flex; box-sizing: border-box; }
                .slot {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                    overflow: hidden;
                }
                .ico {
                    position: absolute;
                    inset: -2px;
                    display: block;
                    object-fit: cover;
                }
                .sweep {
                    position: absolute;
                    inset: 0;
                    background: transparent;
                }
            ";
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/skill", new StubSource(256, 256));
            var (root, styles, ctx, doc) = BuildWithRegistryAndDoc(
                "<button class=\"slot\"><img class=\"ico\" src=\"ui/skill\" /><div class=\"sweep\"></div></button>",
                css, reg, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            var sweep = FirstByClass(root, "sweep");
            Assert.That(ico, Is.Not.Null);
            Assert.That(sweep, Is.Not.Null);
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5), "initial");

            var le = new LayoutEngine(new MonoFontMetrics()) { ImageRegistry = reg };
            // Establish lastRoot on the engine via a normal full Layout.
            root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            ico = FirstByClass(root, "ico");
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5), "post-bootstrap");

            // 50 layouts with only the sweep dirty (Layout-only —
            // matching the AttributeBinding contract for `style`
            // attribute changes). This drives TryLayoutSubtree.
            for (int frame = 0; frame < 50; frame++) {
                // Bump the sweep's style attribute to look like a real
                // cooldown tick. The exact value doesn't matter — the
                // engine just sees "this element's style changed".
                sweep.Element.SetAttribute("style", "background: rgba(0,0,0," + (0.5 + frame * 0.005).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
                var tracker = new Weva.Reactive.InvalidationTracker();
                tracker.MarkDirty(sweep.Element,
                    Weva.Reactive.InvalidationKind.Style
                    | Weva.Reactive.InvalidationKind.Layout
                    | Weva.Reactive.InvalidationKind.Paint);
                root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx, tracker);
                ico = FirstByClass(root, "ico");
                Assert.That(ico, Is.Not.Null, "frame " + frame + ": ico orphaned");
                Assert.That(ico.Width, Is.EqualTo(60).Within(0.5), "frame " + frame + ": ico.Width");
                Assert.That(ico.Height, Is.EqualTo(60).Within(0.5), "frame " + frame + ": ico.Height");
            }
        }

        [Test]
        public void Img_with_inset_negative_stretches_consistently_across_repeated_layouts() {
            // The "rapid mouseover" regression. Even when LayoutEngine
            // is called repeatedly against the same document (mimicking
            // what a class-toggle / cascade-rebuild cycle does in the
            // editor), the stretch-to-fill result must stay stable.
            // The previous IntrinsicWidth-stamp-then-skip path produced
            // 56 on every pass; we want a steady 60 forever.
            const string css = @"
                * { box-sizing: border-box; }
                button { display: inline-flex; box-sizing: border-box; }
                .cb {
                    position: relative;
                    width: 60px;
                    height: 60px;
                    border: 2px solid #fff;
                    overflow: hidden;
                }
                .ico {
                    position: absolute;
                    inset: -2px;
                    display: block;
                    object-fit: cover;
                }
            ";
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/skill", new StubSource(256, 256));
            var (root, styles, ctx, doc) = BuildWithRegistryAndDoc(
                "<button class=\"cb\"><img class=\"ico\" src=\"ui/skill\" /></button>",
                css, reg, viewportWidth: 800);
            var ico = FirstByClass(root, "ico");
            Assert.That(ico, Is.Not.Null);
            Assert.That(ico.Width, Is.EqualTo(60).Within(0.5), "initial");
            // Loop the engine — each iteration relays out the same
            // document and stamps fresh boxes. The intrinsic-pinned
            // interaction should give the same answer every time.
            var le = new LayoutEngine(new MonoFontMetrics()) { ImageRegistry = reg };
            for (int pass = 0; pass < 5; pass++) {
                root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
                ico = FirstByClass(root, "ico");
                Assert.That(ico, Is.Not.Null);
                Assert.That(ico.Width, Is.EqualTo(60).Within(0.5), "pass " + pass);
                Assert.That(ico.Height, Is.EqualTo(60).Within(0.5), "pass " + pass);
            }
        }

        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        // Drop-in for LayoutTestHelpers.Build that wires an image
        // registry into the LayoutEngine. Necessary because the
        // intrinsic-img code path only fires when the registry resolves
        // a source, and the default helper passes null.
        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) BuildWithRegistry(
            string html, string css, IImageRegistry registry, double viewportWidth = 800, double viewportHeight = 600
        ) {
            var (root, styles, ctx, _) = BuildWithRegistryAndDoc(html, css, registry, viewportWidth, viewportHeight);
            return (root, styles, ctx);
        }

        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx, Document doc) BuildWithRegistryAndDoc(
            string html, string css, IImageRegistry registry, double viewportWidth = 800, double viewportHeight = 600
        ) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(new MonoFontMetrics()) { ImageRegistry = registry };
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles, ctx, doc);
        }
    }
}
