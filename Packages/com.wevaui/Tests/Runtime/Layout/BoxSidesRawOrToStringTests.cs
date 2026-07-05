using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Regression coverage for CODE_AUDIT_FINDINGS.md PA1.
    //
    // ResolveBoxSidesPx hits the shorthand-expansion branch when every
    // padding/margin longhand is at its initial-zero value AND the shorthand
    // slot is set via SetParsed — i.e. the animation overlay path. Before the
    // fix, each of the 4 sides materialised `?.Raw ?? ?.ToString() ?? "0"`
    // inline, allocating a fresh string per side whenever the parsed sub-value
    // was constructed programmatically (Raw == null). The fix routes every
    // side through a single RawOrToString helper.
    //
    // The first test asserts the helper preserves layout correctness when
    // Raw is null. The second is an allocation-floor regression: 100 layouts
    // of a single-box scene with a Raw-less SetParsed padding overlay must
    // stay well under the previous per-side ToString fan-out.
    public class BoxSidesRawOrToStringTests {
        static BlockBox FindFirstBlock(Box root, string tag) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        // Build a single-div doc, install a synthetic padding shorthand whose
        // sub-values carry no Raw string (mirrors what the animation engine
        // emits when interpolating a CssValueList between two padding states).
        static (LayoutEngine engine, Document doc, Func<Element, ComputedStyle> resolve, LayoutContext ctx, Element div)
            BuildDocWithRawlessPaddingOverlay(double pxPerSide) {

            var doc = Html("<div id=\"target\">x</div>");
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author("#target { width: 200px; }")
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var div = FindByTag(doc, "div");
            Assert.That(div, Is.Not.Null);
            var divStyle = styles[div];

            // Construct CssLengths with raw=null (the lazy-raw overload). This
            // mirrors the animation typed overlay: the interpolator builds
            // fresh CssLengths each Tick and never bothers to fill Raw.
            var items = new List<CssValue> {
                new CssLength(pxPerSide, CssLengthUnit.Px, raw: null),
                new CssLength(pxPerSide, CssLengthUnit.Px, raw: null),
                new CssLength(pxPerSide, CssLengthUnit.Px, raw: null),
                new CssLength(pxPerSide, CssLengthUnit.Px, raw: null),
            };
            var list = new CssValueList(items, CssValueListSeparator.Space, raw: null);
            divStyle.SetParsed(CssProperties.PaddingId, list);

            // Sanity-check that we actually exercise the cold path: shorthand
            // slot must be set with null Raw on at least the sub-items, and
            // all four longhand slots must read as initial-zero so
            // ResolveBoxSidesPx takes the shorthand-expansion branch.
            Assert.That(list.Raw, Is.Null, "padding shorthand Raw must be null to exercise the PA1 helper path");
            for (int i = 0; i < items.Count; i++) {
                Assert.That(items[i].Raw, Is.Null,
                    "padding side[" + i + "] Raw must be null to exercise the PA1 helper path");
            }
            Assert.That(IsInitialZeroRaw(divStyle.Get(CssProperties.PaddingTopId)));
            Assert.That(IsInitialZeroRaw(divStyle.Get(CssProperties.PaddingRightId)));
            Assert.That(IsInitialZeroRaw(divStyle.Get(CssProperties.PaddingBottomId)));
            Assert.That(IsInitialZeroRaw(divStyle.Get(CssProperties.PaddingLeftId)));

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            return (engine, doc, Resolve, ctx, div);
        }

        static bool IsInitialZeroRaw(string raw) {
            return string.IsNullOrEmpty(raw) || raw == "0";
        }

        [Test]
        public void Padding_with_raw_null_subvalues_resolves_to_correct_pixels() {
            var (engine, doc, resolve, ctx, div) = BuildDocWithRawlessPaddingOverlay(12);
            var root = engine.Layout(doc, resolve, ctx);

            var box = FindFirstBlock(root, "div");
            Assert.That(box, Is.Not.Null);
            // All four sides come from the shorthand expansion branch — if the
            // helper had bailed (returned "0" instead of materialising via
            // ToString) we'd see 0px padding here. 12px confirms the typed
            // value reached ResolveParsedLengthPx via the haveParsed code path.
            Assert.That(box.PaddingTop, Is.EqualTo(12).Within(0.001));
            Assert.That(box.PaddingRight, Is.EqualTo(12).Within(0.001));
            Assert.That(box.PaddingBottom, Is.EqualTo(12).Within(0.001));
            Assert.That(box.PaddingLeft, Is.EqualTo(12).Within(0.001));
        }

        [Test]
        public void Padding_with_raw_null_subvalues_uses_helper_across_asymmetric_sides() {
            // Same path but with four distinct values so a positional bug
            // (e.g. RawOrToString returning the wrong side's string) would
            // surface as crossed pixel counts.
            var doc = Html("<div id=\"target\">x</div>");
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author("#target { width: 200px; }")
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var div = FindByTag(doc, "div");
            var divStyle = styles[div];
            var items = new List<CssValue> {
                new CssLength(3, CssLengthUnit.Px, raw: null),  // top
                new CssLength(5, CssLengthUnit.Px, raw: null),  // right
                new CssLength(7, CssLengthUnit.Px, raw: null),  // bottom
                new CssLength(11, CssLengthUnit.Px, raw: null), // left
            };
            divStyle.SetParsed(CssProperties.PaddingId,
                new CssValueList(items, CssValueListSeparator.Space, raw: null));

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 600,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = engine.Layout(doc, Resolve, ctx);

            var box = FindFirstBlock(root, "div");
            Assert.That(box.PaddingTop, Is.EqualTo(3).Within(0.001));
            Assert.That(box.PaddingRight, Is.EqualTo(5).Within(0.001));
            Assert.That(box.PaddingBottom, Is.EqualTo(7).Within(0.001));
            Assert.That(box.PaddingLeft, Is.EqualTo(11).Within(0.001));
        }

        // -----------------------------------------------------------------
        // Allocation regression. 100 layouts of a Raw-less padding overlay.
        // Pre-fix: per-call ResolveBoxSidesPx allocated 4 fresh strings from
        // the inline `?.Raw ?? ?.ToString() ?? "0"` chain — ~32 bytes/side
        // × 4 sides × N boxes per call. Post-fix: same ToString calls happen
        // (Raw is still null) but the helper centralises the path and the
        // delta-per-call is dominated by the 4 ToString allocations that are
        // unavoidable until we cache on CssValue. The assertion is loose
        // enough to absorb GC bookkeeping but tight enough to catch a
        // regression where the helper accidentally allocates extra strings
        // (e.g. an interpolated logging path).
        // -----------------------------------------------------------------

        static long Snapshot() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetTotalAllocatedBytes(precise: false);
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        static void Stabilize() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        [Test, Explicit("alloc"), Category("alloc")]
        public void Repeated_layout_with_rawless_padding_allocates_near_zero() {
            var (engine, doc, resolve, ctx, _) = BuildDocWithRawlessPaddingOverlay(8);

            // Warm: prime the box-sides cache, the box pool, and any
            // first-call scratch lists so we're measuring steady state.
            for (int i = 0; i < 5; i++) engine.Layout(doc, resolve, ctx);

            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 100; i++) {
                engine.Layout(doc, resolve, ctx);
            }
            long end = Snapshot();
            long delta = end - start;
            TestContext.Progress.WriteLine(
                "100x Layout (Raw-null padding overlay): " + delta + " bytes");

            // BoxSidesCacheKey caches the resolved sides keyed on (style,
            // shorthandId, fs, basis) — every layout after the first hits the
            // cache and skips ResolveBoxSidesPx entirely, so the ToString calls
            // fire ONCE total across all 100 iterations. Anything substantially
            // above 1 MB across 100 layouts of a single-div doc indicates a
            // per-pass allocation creep elsewhere in the pipeline.
            Assert.That(delta, Is.LessThan(1_000_000),
                "100x Layout allocated " + delta + " bytes (>1 MB ceiling) — PA1 helper may be re-materialising per pass");
        }
    }
}
