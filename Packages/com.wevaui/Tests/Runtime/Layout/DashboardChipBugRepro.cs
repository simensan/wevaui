using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Deterministic repros for two visual bugs surfaced by the
    // advanced-dashboard probe against the Unity renderer.
    //
    // Bug 1 — chip background missing.
    //   `.chip { background: rgba(255,255,255,0.04); border: 1px solid var(--line);
    //            border-radius: var(--radius-md); }`
    //   Expected: BackgroundResolver returns a non-null SolidColor Brush;
    //             HasDecorationProperties is true on the chip's ComputedStyle.
    //   Repro: assert the background-color reaches the resolved ComputedStyle
    //          and that the cascade correctly expands the `background` shorthand.
    //
    // Bug 2 — badge ::after much taller than expected.
    //   `.chip.has-badge::after { display: flex; height: 18px; min-width: 18px;
    //                             font-size: 10px; ... }`
    //   Expected: laid-out box height == 18px.
    //   Repro: assert the final box height of the absolute ::after FlexBox.
    public class DashboardChipBugRepro {

        // CSS custom-property variables and chip/badge rules extracted from
        // advanced-dashboard.css, stripped to the minimum needed for each repro.
        const string ChipVars = @"
            :root {
                --bg-card:  #131826;
                --line:     rgba(255, 255, 255, 0.06);
                --red:      #ef4444;
                --text:     #d3dae5;
                --radius-md: 10px;
            }
        ";

        const string ChipCss = @"
            .chip {
                display: inline-flex;
                align-items: center;
                gap: 8px;
                padding: 6px 12px;
                border-radius: var(--radius-md);
                background: rgba(255, 255, 255, 0.04);
                border: 1px solid var(--line);
                color: var(--text);
                font-size: 13px;
                font-weight: 700;
                position: relative;
            }
        ";

        // The dashboard CSS has `* { box-sizing: border-box; }` which applies to
        // pseudo-elements too.  This must reach the ::after computed style so that
        // `height: 18px` is treated as the BORDER-BOX size (18px total including
        // the 2px border), not the content-box size (18px content + 4px borders = 22px total).
        const string BoxSizingBorderBox = @"
            * { box-sizing: border-box; }
        ";

        const string BadgeCss = @"
            .chip.has-badge {
                position: relative;
            }
            .chip.has-badge::after {
                content: attr(data-badge);
                position: absolute;
                top: -6px;
                right: -6px;
                min-width: 18px;
                height: 18px;
                padding: 0 5px;
                border-radius: 999px;
                background: var(--red);
                color: #fff;
                font-size: 10px;
                font-weight: 800;
                display: flex;
                align-items: center;
                justify-content: center;
                border: 2px solid var(--bg-card);
            }
        ";

        // Full layout helper: cascade + BoxBuilder (with pseudo resolvers) +
        // LayoutEngine pass.
        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) BuildLaid(
            string html, string css, double vpWidth = 800) {

            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(css))
            };
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var fm = new MonoFontMetrics();
            var ctx = new LayoutContext(fm) {
                ViewportWidthPx = vpWidth,
                ViewportHeightPx = 600,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(fm);
            le.BeforeStyleOf = e => engine.ComputeBefore(e);
            le.AfterStyleOf  = e => engine.ComputeAfter(e);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles, ctx);
        }

        // BoxBuilder-only helper (no layout pass) — for testing box-tree shape
        // and style properties without needing sized geometry.
        static (Box root, Dictionary<Element, ComputedStyle> styles) BuildBoxes(
            string html, string css) {

            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(css))
            };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            bb.BeforeStyleOf = e => engine.ComputeBefore(e);
            bb.AfterStyleOf  = e => engine.ComputeAfter(e);
            return (bb.BuildDocument(doc), styles);
        }

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        static Box FindBoxWithElement(Box root, string tagName) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.TagName == tagName) return b;
            }
            return null;
        }

        static Box FindBoxWithClass(Box root, string className) {
            foreach (var b in AllBoxes(root)) {
                if (b.Element != null && b.Element.ClassList.Any(c => c == className)) return b;
            }
            return null;
        }

        // ── Bug 1a: cascade sets background-color on chip from rgba() ──────────
        //
        // `.chip { background: rgba(255,255,255,0.04); }` — the `background`
        // shorthand must expand to `background-color: rgba(255,255,255,0.04)`.
        // If the expander drops the rgba() token (e.g. IsColor returns false
        // for an unrecognised function form), background-color stays "transparent"
        // and the chip renders blank.
        [Test]
        public void Bug1a_Chip_background_color_is_cascaded_as_non_transparent_rgba() {
            var (root, styles) = BuildBoxes(
                "<div class=\"chip\">1,420</div>",
                ChipVars + ChipCss);

            var chipBox = FindBoxWithClass(root, "chip");
            Assert.That(chipBox, Is.Not.Null, "chip box must exist in the box tree");

            var style = chipBox.Style;
            Assert.That(style, Is.Not.Null, "chip must have a ComputedStyle");

            // The background shorthand should have expanded to background-color.
            string bgColor = style.Get("background-color");
            Assert.That(bgColor, Is.Not.Null.And.Not.EqualTo("transparent"),
                "background-color must not be transparent after rgba() shorthand expansion; got: " + bgColor);
        }

        // ── Bug 1b: BackgroundResolver returns a non-null Brush for rgba(α=0.04) ─
        //
        // Even if the cascade correctly stores the rgba() value, BackgroundResolver
        // must produce a non-null Brush. The guard `color.A <= 0f` must NOT
        // discard a color with A=0.04 — only A==0 is truly transparent.
        [Test]
        public void Bug1b_BackgroundResolver_returns_brush_for_low_alpha_rgba() {
            BackgroundResolver.ResetCaches_TestOnly();

            var style = new ComputedStyle(new Element("button"));
            // Simulate what the cascade sets after shorthand expansion.
            style.Set("background-color", "rgba(255, 255, 255, 0.04)");
            style.Set("color", "#d3dae5");

            var brush = BackgroundResolver.ResolveBackground(style, new Rect(0, 0, 120, 32));

            Assert.That(brush, Is.Not.Null,
                "BackgroundResolver must return a Brush for rgba(255,255,255,0.04) — " +
                "A=0.04 is above the zero-alpha cutoff");
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.SolidColor),
                "rgba() background must resolve to a SolidColor brush");
            Assert.That(brush.Color.A, Is.GreaterThan(0f).And.LessThan(0.1f),
                "brush alpha must preserve the authored 0.04 value (within sRGB precision)");
        }

        // ── Bug 1c: HasDecorationProperties is set on the chip ComputedStyle ───
        //
        // BoxToPaintConverter's EmitVisibleDecorations fast-path gate:
        //   `if (!style.HasDecorationProperties ...) return;`
        // If this flag is false, the chip's background, border, and border-radius
        // commands are never emitted, producing the "blank chip" symptom.
        [Test]
        public void Bug1c_Chip_computed_style_has_decoration_properties_flagged() {
            var (root, styles) = BuildBoxes(
                "<div class=\"chip\">1,420</div>",
                ChipVars + ChipCss);

            var chipBox = FindBoxWithClass(root, "chip");
            Assert.That(chipBox, Is.Not.Null);

            var style = chipBox.Style;
            Assert.That(style, Is.Not.Null);
            Assert.That(style.HasDecorationProperties, Is.True,
                "HasDecorationProperties must be set when background + border + border-radius " +
                "are authored — otherwise BoxToPaintConverter skips EmitVisibleDecorations entirely");
        }

        // ── Bug 1d: border-radius is cascaded on the chip ─────────────────────
        //
        // `border-radius: var(--radius-md)` with `--radius-md: 10px` must resolve
        // to `10px`. If var() substitution fails, border-radius stays at `0` and
        // the chip renders as a plain rectangle (with a visible sharp-corner
        // mismatch vs Chrome's 10px-rounded shape).
        [Test]
        public void Bug1d_Chip_border_radius_resolves_from_custom_property() {
            var (root, styles) = BuildBoxes(
                "<div class=\"chip\">1,420</div>",
                ChipVars + ChipCss);

            var chipBox = FindBoxWithClass(root, "chip");
            Assert.That(chipBox, Is.Not.Null);

            var style = chipBox.Style;
            // border-radius shorthand expands to the four corner longhands.
            // After var(--radius-md) substitution each should be "10px".
            string tlr = style.Get("border-top-left-radius")
                      ?? style.Get("border-radius");
            Assert.That(tlr, Is.EqualTo("10px").Or.Contains("10px"),
                "border-radius corner must be 10px after var(--radius-md) substitution; got: " + tlr);
        }

        // ── Bug 2a: badge ::after height is exactly 18px ──────────────────────
        //
        // The badge `::after` has `height: 18px` and `display: flex`.
        // The dashboard also has `* { box-sizing: border-box }` which makes
        // `height: 18px` the border-box height (not content + 4px borders = 22px).
        // After the full layout pass (BlockLayout → FlexLayout → PositioningPass),
        // the badge box height must equal 18px (border-box, matching Chrome).
        //
        // SPEC (matches Chrome): the universal selector `*` matches ELEMENTS,
        // not pseudo-elements, and box-sizing is not inherited — so
        // `* { box-sizing: border-box }` does NOT reach `::after`. The pseudo
        // keeps the content-box initial, so its border-box height is
        // 18px (content) + 2px×2 border = 22px. (Authors who want pseudos
        // border-box must spell out `*, *::before, *::after { ... }`.) This
        // test guards that the engine matches Chrome here.
        [Test]
        public void Badge_after_box_height_is_content_box_22px_matching_chrome() {
            // Wrap in a positioned container so PositioningPass resolves
            // the absolute ::after relative to the chip.
            const string html = "<div style=\"position:relative; width:200px;\">" +
                                   "<button class=\"chip has-badge\" data-badge=\"3\">" +
                                     "<span>Mail</span>" +
                                   "</button>" +
                                 "</div>";

            var (root, styles, ctx) = BuildLaid(html,
                ChipVars + BoxSizingBorderBox + ChipCss + BadgeCss);

            // Find the ::after generated box: a FlexBox with no DOM Element
            // whose parent is the chip's box.
            Box chipBox = FindBoxWithClass(root, "chip");
            Assert.That(chipBox, Is.Not.Null, "chip box must be found in the laid-out tree");

            Box badgeBox = null;
            foreach (var b in AllBoxes(chipBox)) {
                // ::after box: no Element, is a FlexBox (from display:flex on ::after), not the chip itself.
                if (b != chipBox && b.Element == null && b is FlexBox) {
                    badgeBox = b;
                    break;
                }
            }

            Assert.That(badgeBox, Is.Not.Null,
                "::after FlexBox must exist as a child of the chip box");

            Assert.That(badgeBox.Height, Is.EqualTo(22.0).Within(1.0),
                "Badge ::after is content-box (18px content + 2px×2 border = 22px), " +
                "matching Chrome — `*` does not apply box-sizing to pseudo-elements. Was: " + badgeBox.Height);
        }

        // ── Bug 2a2 (cascade companion): `*` does NOT reach the ::after pseudo ──
        //
        // The cascade-level companion to the box-height test: box-sizing on the
        // ::after pseudo stays content-box because the universal selector `*`
        // matches elements only, not pseudo-elements (CSS Selectors L4), and
        // box-sizing isn't inherited. Both this engine AND Chrome require
        // `*, *::before, *::after { ... }` to apply box-sizing to pseudos, so
        // this guards that the engine matches Chrome.
        [Test]
        public void Star_box_sizing_does_not_reach_after_pseudo_matching_chrome() {
            var doc = HtmlParser.Parse(
                "<button class=\"chip has-badge\" data-badge=\"3\">Mail</button>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(
                    ChipVars + BoxSizingBorderBox + ChipCss + BadgeCss))
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { /* populate cache */ }

            var btn = FindByTag(doc, "button");
            Assert.That(btn, Is.Not.Null);

            var afterStyle = engine.ComputeAfter(btn);
            Assert.That(afterStyle, Is.Not.Null);

            // `*` does NOT reach the ::after pseudo (matches Chrome), so
            // box-sizing stays at the content-box initial.
            string bs = afterStyle.Get("box-sizing");
            Assert.That(bs, Is.EqualTo("content-box"),
                "box-sizing on ::after stays content-box — `*` matches elements not " +
                "pseudo-elements (same as Chrome); got: " + bs);
        }

        // ── Bug 2b: badge font-size is 10px (not inherited from chip 13px) ────
        //
        // The badge `::after` has `font-size: 10px` explicitly.
        // If the pseudo's font-size incorrectly inherits the chip's 13px (the
        // host value), and em-based layout dimensions were present, boxes would
        // scale to ~13/10 × authored size. This test pins the cascade contract
        // that the explicit 10px wins.
        [Test]
        public void Bug2b_Badge_after_computed_font_size_is_10px_not_inherited_13px() {
            var doc = HtmlParser.Parse(
                "<button class=\"chip has-badge\" data-badge=\"3\">Mail</button>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(ChipVars + ChipCss + BadgeCss))
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { /* populate cache */ }

            var btn = FindByTag(doc, "button");
            Assert.That(btn, Is.Not.Null);

            var afterStyle = engine.ComputeAfter(btn);
            Assert.That(afterStyle, Is.Not.Null,
                "CascadeEngine.ComputeAfter must return a style for .chip.has-badge::after");

            string fs = afterStyle.Get("font-size");
            Assert.That(fs, Is.EqualTo("10px"),
                "::after font-size must be the authored 10px, not the host chip's 13px; got: " + fs);
        }

        // ── Bug 2c: badge content resolves to attr(data-badge) value ──────────
        //
        // `content: attr(data-badge)` with `data-badge="3"` must resolve to "3".
        // If AttrResolver fails here, the badge is empty (no text "3" renders)
        // which is a separate symptom from the size bug but exercised by the
        // same ::after path.
        [Test]
        public void Bug2c_Badge_after_content_resolves_to_attr_data_badge_value() {
            var doc = HtmlParser.Parse(
                "<button class=\"chip has-badge\" data-badge=\"3\">Mail</button>");
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(ChipVars + ChipCss + BadgeCss))
            };
            var engine = new CascadeEngine(sheets);
            foreach (var kv in engine.ComputeAll(doc)) { /* populate cache */ }

            var btn = FindByTag(doc, "button");
            Assert.That(btn, Is.Not.Null);

            var afterStyle = engine.ComputeAfter(btn);
            Assert.That(afterStyle, Is.Not.Null);

            string contentRaw = afterStyle.Get("content");
            // ResolveContentString(raw, host) must resolve attr(data-badge) to "3".
            string resolved = CascadeEngine.ResolveContentString(contentRaw, btn);
            Assert.That(resolved, Is.EqualTo("3"),
                "content: attr(data-badge) must resolve to the attribute value '3'; " +
                "raw content was: " + contentRaw);
        }
    }
}
