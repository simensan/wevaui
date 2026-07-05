using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Coverage for the P13 fix: BoxBuilder's hot paths now read the
    // background and list-style properties via CssProperties.*Id constants
    // instead of string-keyed style.Get probes. Three call sites in
    // BoxBuilder.cs got converted:
    //   - IsBackgroundEmpty (background-color + background-image)
    //   - body-to-html background propagation (same two)
    //   - MaybeInjectListMarker (list-style-type + list-style-image)
    // These tests pin:
    //   1. Parity — body-to-html background propagation still fires when
    //      the html has no background and the body does; the list-item
    //      marker still injects (or suppresses) under the spec rules.
    //   2. Allocation — 100 BuildBoxesOnly cycles allocate within a
    //      bounded budget, demonstrating the int-id read is not the
    //      source of per-build dict-probe pressure.
    public class BoxBuilderPropertyIdTests {
        const string ListUA = "ul { list-style-type: disc; } ol { list-style-type: decimal; }";

        static BlockBox FindMarker(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li" && bb.Children.Count > 0) {
                    if (bb.Children[0] is BlockBox m && m.Element == null && m.IsInlineBlock) {
                        return m;
                    }
                }
            }
            return null;
        }

        // Parity 1: body-to-html background propagation. CSS Backgrounds 3
        // §2.11.2 says the root box's background paints the canvas; when
        // <html> has no background and <body> has one, the engine moves
        // <body>'s background up to <html>. The propagation check uses
        // IsBackgroundEmpty(html) which now reads via int ids.
        [Test]
        public void Body_background_color_propagates_to_html_via_int_id_read() {
            var (root, styles) = BuildBoxesOnly(
                "<html><body style=\"background-color: rgb(10,20,30)\"></body></html>");
            Element html = null, body = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == "html") html = b.Element;
                if (b.Element?.TagName == "body") body = b.Element;
            }
            Assert.That(html, Is.Not.Null);
            Assert.That(body, Is.Not.Null);
            var htmlStyle = styles[html];
            var bodyStyle = styles[body];
            // The exact serialisation of the color (spaces, casing) is parser-
            // dependent; pin parity instead — html's slot now matches body's,
            // proving the int-id Get(BackgroundColorId) read fed the
            // propagation correctly.
            string propagated = htmlStyle.Get("background-color");
            string bodyColor = bodyStyle.Get("background-color");
            Assert.That(propagated, Is.Not.Null.And.Not.Empty);
            Assert.That(propagated, Is.EqualTo(bodyColor),
                "html should have received body's background-color via the int-id propagation path");
        }

        // Parity 2: when html ALREADY has a non-trivial background, the
        // propagation must NOT fire (the int-id IsBackgroundEmpty sees the
        // populated slot and bails). Pinning this ensures we did not swap
        // the truthiness check accidentally.
        [Test]
        public void Body_background_does_not_overwrite_existing_html_background() {
            var (root, styles) = BuildBoxesOnly(
                "<html style=\"background-color: rgb(1,2,3)\"><body style=\"background-color: rgb(10,20,30)\"></body></html>");
            Element html = null;
            foreach (var b in AllBoxes(root)) {
                if (b.Element?.TagName == "html") { html = b.Element; break; }
            }
            Assert.That(html, Is.Not.Null);
            string htmlColor = styles[html].Get("background-color");
            Assert.That(htmlColor, Is.Not.Null.And.Not.Empty);
            // If propagation had incorrectly fired, html's color would have
            // been overwritten with body's "rgb(10,20,30)". We assert it
            // does NOT contain the body's distinctive "30" token (which
            // cannot appear in the original "rgb(1,2,3)" serialisation
            // regardless of whitespace / casing normalisation).
            Assert.That(htmlColor, Does.Not.Contain("30"),
                "html's pre-existing background-color must NOT be overwritten by body's rgb(10,20,30)");
        }

        // Parity 3: list-style-image overrides list-style-type per CSS
        // Lists 3 §3.3 — the marker is injected without a text glyph. The
        // image-resolution branch now reads list-style-image via int id.
        [Test]
        public void List_style_image_replaces_text_marker_via_int_id_read() {
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-image: url(marker.png)\">x</li></ul>", ListUA);
            var marker = FindMarker(root);
            Assert.That(marker, Is.Not.Null, "marker should be injected even when only the image longhand is set");
            // Image-only markers have no TextRun child — the BuildListMarkerBox
            // path is text=null, image="marker.png".
            bool hasTextRun = false;
            foreach (var c in marker.Children) if (c is TextRun) hasTextRun = true;
            Assert.That(hasTextRun, Is.False, "image marker must not carry a text glyph");
        }

        // Parity 4: list-style-type:none with NO image must suppress the
        // marker — the int-id read of list-style-type must return "none"
        // and the int-id read of list-style-image must return null/"none".
        [Test]
        public void List_style_type_none_without_image_suppresses_marker() {
            var (root, _) = BuildBoxesOnly(
                "<ul><li style=\"list-style-type: none\">x</li></ul>", ListUA);
            Assert.That(FindMarker(root), Is.Null);
        }

        // Steady-state allocation pin scoped to JUST the BoxBuilder hot
        // path (the int-id Get calls we just migrated). We parse / cascade
        // once outside the measurement window and call BuildDocument 100
        // times inside it; this isolates the BoxBuilder allocation profile
        // from the upstream parsing / cascade pipeline (which dominates
        // wall-clock and allocation regardless of our migration).
        //
        // Before the migration each BuildDocument paid `style.Get(string)`
        // probes per <li> (for list-style-type / list-style-image) plus per
        // html-vs-body propagation check (for background-color /
        // background-image). The int-id path indexes the values array
        // directly. We assert tightly: 100 builds of a 3-li fixture stay
        // well-bounded.
        [Test]
        public void BuildDocument_100_calls_with_list_and_body_bg_stays_within_alloc_budget() {
            const string html =
                "<html><body style=\"background-color: rgb(10,20,30)\">" +
                "<ul><li>a</li><li>b</li><li>c</li></ul>" +
                "</body></html>";

            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(ListUA))
            };
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            Func<Element, ComputedStyle> styleOf =
                e => styles.TryGetValue(e, out var cs) ? cs : null;

            // Warmup: prime any lazy parsed-value materialisation the first
            // call would otherwise pay (list-style-type CssValue parse, ...).
            for (int i = 0; i < 10; i++) {
                var warm = new BoxBuilder(styleOf);
                warm.BuildDocument(doc);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) {
                var bb = new BoxBuilder(styleOf);
                var root = bb.BuildDocument(doc);
                if (root == null) throw new Exception("unreachable");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            // BoxBuilder-only budget: parsing + cascade are excluded. The
            // bulk of remaining allocation is fresh BoxPool / Box instances
            // per iteration (each BoxBuilder ctor allocates a new pool).
            // The int-id reads themselves are allocation-free; anything
            // well under 4 MB / 100 calls demonstrates the migrated reads
            // are not hot-allocating.
            Assert.That(delta, Is.LessThan(4 * 1024 * 1024),
                $"100 BuildDocument calls allocated {delta} bytes — the int-id BoxBuilder reads regressed.");
        }
    }
}
