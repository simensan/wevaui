using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Filters {
    public class FilterIntegrationTests {
        sealed class HoverState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            long version = 1;
            public long Version => version;
            public void Set(Element e, ElementState s) { map[e] = s; version++; }
            public ElementState GetState(Element e) =>
                map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        [Test]
        public void Modal_backdrop_with_blur_emits_PushFilter() {
            var (root, _, _) = Build(
                "<div class=\"backdrop\"><p>content</p></div>",
                ".backdrop { filter: blur(8px); background-color: rgba(0, 0, 0, 0.4); }",
                400);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            PushFilterCommand pushFilter = null;
            foreach (var c in cmds) {
                if (c is PushFilterCommand pf) { pushFilter = pf; break; }
            }
            Assert.That(pushFilter, Is.Not.Null);
            Assert.That(pushFilter.Filters.Functions.Count, Is.EqualTo(1));
            Assert.That(pushFilter.Filters.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(((BlurFilter)pushFilter.Filters.Functions[0]).RadiusPx, Is.EqualTo(8).Within(1e-6));
            // Pop filter must also exist.
            int popCount = 0;
            foreach (var c in cmds) if (c is PopFilterCommand) popCount++;
            Assert.That(popCount, Is.EqualTo(1));
        }

        [Test]
        public void Card_drop_shadow_emits_DropShadow_filter() {
            // RECALIBRATED (long-standing known-red): a LONE drop-shadow
            // renders via the synthetic DrawShadow path, not a filter scope
            // (the scope's composite painted over later siblings —
            // story-bubble `.frame`; see isLoneDropShadow in
            // BoxToPaintConverter). The resolved values still round-trip
            // cascade → FilterParser → synthetic BoxShadow exactly.
            var (root, _, _) = Build(
                "<div class=\"card\"><p>card</p></div>",
                ".card { filter: drop-shadow(0 4px 8px rgba(0, 0, 0, 0.2)); background-color: white; }",
                400);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            bool anyPush = false;
            DrawShadowCommand shadow = null;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) anyPush = true;
                if (shadow == null && c is DrawShadowCommand dsc) shadow = dsc;
            }
            Assert.That(anyPush, Is.False, "lone drop-shadow must not open a filter scope");
            Assert.That(shadow, Is.Not.Null, "synthetic DrawShadow expected");
            Assert.That(shadow.Shadow.OffsetX, Is.EqualTo(0));
            Assert.That(shadow.Shadow.OffsetY, Is.EqualTo(4));
            Assert.That(shadow.Shadow.BlurRadius, Is.EqualTo(8));
            Assert.That(shadow.Shadow.Color.A, Is.EqualTo(0.2f).Within(0.02));
            Assert.That(shadow.Shadow.Inset, Is.False);
        }

        [Test]
        public void Hover_filter_flows_through_cascade_to_converter() {
            // Author a filter chain that CANNOT be folded into paint colors —
            // `TryFoldBrightnessFilterIntoPaintColors` only folds single
            // brightness/contrast/etc. into FillRect's color, so a chain with
            // `blur(...)` (which has no folding shortcut) forces the
            // PushFilter / PopFilter scope path. This is what we want to
            // assert: the hover-driven cascade → ComputedStyle → BoxToPaint
            // round-trip produces the PushFilter scope.
            var doc = Html("<div id=\"b\">click</div>");
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author("#b { background-color: red; } #b:hover { filter: blur(4px) brightness(1.1); }")
            };
            var engine = new CascadeEngine(sheets);
            var btn = doc.GetElementById("b");
            var state = new HoverState();

            // Idle: no filter (the cascade's initial value is the keyword "none", which the
            // resolver treats as an empty chain).
            var idleStyles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc, state)) idleStyles[kv.Key] = kv.Value;
            string idleFilter = idleStyles[btn].Get("filter");
            Assert.That(string.IsNullOrEmpty(idleFilter) || idleFilter == "none", Is.True);

            // Hover: filter present.
            state.Set(btn, ElementState.Hover);
            var hoverStyles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc, state)) hoverStyles[kv.Key] = kv.Value;
            string filterText = hoverStyles[btn].Get("filter");
            Assert.That(filterText, Is.Not.Null.And.Not.Empty);
            Assert.That(filterText, Does.Contain("blur"));
            Assert.That(filterText, Does.Contain("brightness"));

            // Build a Box for the hovered button + run converter; expect PushFilter
            // because blur cannot be folded into paint colors.
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 400,
                ViewportHeightPx = 300,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => hoverStyles.TryGetValue(e, out var cs) ? cs : null, ctx);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            bool sawPushFilter = false;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) { sawPushFilter = true; break; }
            }
            Assert.That(sawPushFilter, Is.True,
                "Hover state with non-foldable filter chain should produce a PushFilterCommand");
        }

        [Test]
        public void Hover_single_brightness_filter_folds_into_paint_color_no_PushFilter() {
            // Companion test: `filter: brightness(1.1)` (single function) IS
            // foldable per `TryFoldBrightnessFilterIntoPaintColors`. Engine
            // adjusts the FillRect's color and skips the PushFilter scope.
            // This is the documented optimization path; the test guards
            // against accidental regression (e.g. always-emit PushFilter would
            // make this test fail).
            var doc = Html("<div id=\"b\">click</div>");
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUserAgent),
                Author("#b { background-color: red; } #b:hover { filter: brightness(1.1); }")
            };
            var engine = new CascadeEngine(sheets);
            var btn = doc.GetElementById("b");
            var state = new HoverState();
            state.Set(btn, ElementState.Hover);
            var hoverStyles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc, state)) hoverStyles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 400,
                ViewportHeightPx = 300,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => hoverStyles.TryGetValue(e, out var cs) ? cs : null, ctx);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int pushFilterCount = 0;
            foreach (var c in cmds) {
                if (c is PushFilterCommand) pushFilterCount++;
            }
            Assert.That(pushFilterCount, Is.EqualTo(0),
                "Single-brightness filter must fold into paint colors; PushFilter scope must NOT be emitted");
        }
    }
}
