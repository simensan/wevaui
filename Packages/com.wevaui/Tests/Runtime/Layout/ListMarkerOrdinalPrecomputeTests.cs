using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Compiled;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // PA6 fix: BoxBuilder.MaybeInjectListMarker no longer walks the `<ul>`/
    // `<ol>` parent's children to compute each `<li>`'s ordinal. Instead,
    // BuildChildren seeds a precomputed `Dictionary<Element,int>` in one
    // O(siblings) pass when it enters a list parent, honouring `start`,
    // `reversed`, and per-`<li>` `value` attributes. MaybeInjectListMarker
    // reads from that dict in O(1), collapsing total list-marker cost
    // from O(N^2) to O(N).
    //
    // The diagnostic counter `BoxBuilder.ListMarkerOrdinalWalks` /
    // `SnapshotBoxBuilder.ListMarkerOrdinalWalks` is bumped whenever the
    // fallback walk runs. A normal build through BuildDocument /
    // BuildFromSnapshot keeps it at zero.
    public class ListMarkerOrdinalPrecomputeTests {
        // li uses display:list-item (CSS Lists L3 §2) so MaybeInjectListMarker
        // fires on the display-gate rather than the old tag-gate.
        const string ListUA = "html, body, ul, ol { display: block; } li { display: list-item; } ul { list-style-type: disc; } ol { list-style-type: decimal; }";

        // ----- Shared helpers ----------------------------------------------

        static (Document doc, Dictionary<Element, ComputedStyle> styles) Cascade(string html, string css) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(ListUA))
            };
            if (!string.IsNullOrEmpty(css)) sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            return (doc, styles);
        }

        static (Box root, BoxBuilder builder) BuildWithBuilder(string html, string css = null) {
            var (doc, styles) = Cascade(html, css);
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            return (bb.BuildDocument(doc), bb);
        }

        static List<string> MarkerTextsInOrder(Box root) {
            var texts = new List<string>();
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") {
                    // The marker is an Element-less inline-block BlockBox
                    // whose single child is a TextRun with the glyph. In the
                    // simple `<li>x</li>` case it lives at li.Children[0];
                    // when the li has block children, BoxFinalize wraps the
                    // leading inline-block marker in an anonymous block, so
                    // walk the li's subtree to find it. The first such
                    // Element-less inline-block under the li in DFS order
                    // is the marker.
                    string marker = FindFirstMarkerText(bb);
                    if (marker != null) texts.Add(marker);
                }
            }
            return texts;
        }

        static string FindFirstMarkerText(Box b) {
            // Don't descend into nested `<li>` subtrees — that would harvest
            // an inner list's marker as the outer li's marker. The outer
            // walker (MarkerTextsInOrder) visits every li separately.
            if (b is BlockBox bb && bb.Element == null && bb.IsInlineBlock
                && bb.Children.Count > 0 && bb.Children[0] is TextRun tr) {
                return tr.Text;
            }
            foreach (var c in b.Children) {
                if (c is BlockBox cli && cli.Element?.TagName == "li") continue;
                var t = FindFirstMarkerText(c);
                if (t != null) return t;
            }
            return null;
        }

        // ----- Parity tests (BoxBuilder) -----------------------------------

        [Test]
        public void Ol_100_items_get_consecutive_ordinals_1_through_100() {
            var sb = new System.Text.StringBuilder("<ol>");
            for (int i = 0; i < 100; i++) sb.Append("<li>x</li>");
            sb.Append("</ol>");
            var (root, builder) = BuildWithBuilder(sb.ToString());
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers.Count, Is.EqualTo(100));
            for (int i = 0; i < 100; i++) {
                Assert.That(markers[i], Is.EqualTo((i + 1).ToString() + "."),
                    $"li #{i} should have ordinal {i + 1}");
            }
            // The precomputed path should not have fallen back to the
            // O(siblings) walk for any of the 100 markers.
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0),
                "BuildDocument should seed liOrdinals so no marker walks the parent.");
        }

        [Test]
        public void Ol_reversed_start_5_counts_down() {
            // 5 items, start=5, reversed -> 5, 4, 3, 2, 1
            var (root, builder) = BuildWithBuilder(
                "<ol start=\"5\" reversed><li>a</li><li>b</li><li>c</li><li>d</li><li>e</li></ol>");
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers, Is.EqualTo(new[] { "5.", "4.", "3.", "2.", "1." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        [Test]
        public void Li_value_attribute_resets_counter_midlist() {
            // 1, 2, 42, 43, 44 — `<li value="42">` restarts the counter
            // from 42 and subsequent items continue at 43, 44.
            var (root, builder) = BuildWithBuilder(
                "<ol><li>a</li><li>b</li><li value=\"42\">c</li><li>d</li><li>e</li></ol>");
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers, Is.EqualTo(new[] { "1.", "2.", "42.", "43.", "44." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        [Test]
        public void Ol_reversed_without_start_seeds_from_li_count() {
            // HTML: a reversed `<ol>` without `start` counts down from the
            // number of `<li>` children.
            var (root, builder) = BuildWithBuilder(
                "<ol reversed><li>a</li><li>b</li><li>c</li></ol>");
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers, Is.EqualTo(new[] { "3.", "2.", "1." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        [Test]
        public void Nested_lists_each_get_own_counter() {
            // Inner list reseeds at 1. Outer continues normally past the
            // nested `<ol>` since only `<li>` siblings count. Use a wrapping
            // `<div>` inside outer.li#2 so the inner `<ol>` does NOT mix
            // with inline content of the li (otherwise BoxFinalize wraps
            // the marker + "b" text into an anonymous block, masking the
            // marker behind one level of indirection — irrelevant to PA6).
            var (root, builder) = BuildWithBuilder(
                "<ol><li>a</li><li><ol><li>x</li><li>y</li></ol></li><li>c</li></ol>",
                "div { display: block; }");
            var markers = MarkerTextsInOrder(root);
            // Document-order traversal: outer.1, outer.2 (and inside its
            // children: inner.1, inner.2), outer.3.
            Assert.That(markers, Is.EqualTo(new[] { "1.", "2.", "1.", "2.", "3." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        // ----- Algorithmic CPU pin (BoxBuilder) ----------------------------

        // Quadratic-vs-linear walk-count guard. Pre-fix a 1000-item list ran
        // ~500_000 sibling probes (1 + 2 + 3 + ... + 1000 ≈ N^2/2). With the
        // precompute the per-li walk is replaced by a single dict lookup —
        // ListMarkerOrdinalWalks must stay at zero. The wall-time delta on
        // a 1000-item list is several orders of magnitude.
        [Test]
        public void Ol_1000_items_walk_count_is_zero_on_precomputed_path() {
            var sb = new System.Text.StringBuilder("<ol>");
            for (int i = 0; i < 1000; i++) sb.Append("<li>x</li>");
            sb.Append("</ol>");
            var (root, builder) = BuildWithBuilder(sb.ToString());
            // Verify the build actually produced 1000 markers (so the test
            // can't pass by no-oping the marker path).
            int markerCount = 0;
            foreach (var t in MarkerTextsInOrder(root)) {
                markerCount++;
                // Spot-check first / last
                if (markerCount == 1) Assert.That(t, Is.EqualTo("1."));
                if (markerCount == 1000) Assert.That(t, Is.EqualTo("1000."));
            }
            Assert.That(markerCount, Is.EqualTo(1000));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0),
                "1000-item list must not fall back to the O(siblings) walk on any marker.");
        }

        // Cross-check: tearing the parent away from the BoxBuilder's
        // BuildChildren seed path SHOULD push the marker through the
        // fallback walk and bump the diagnostic counter. This pins the
        // diagnostic itself so the zero-walks assertion above is meaningful.
        [Test]
        public void Direct_li_build_bypasses_precompute_and_uses_fallback() {
            // Build only the `<li>` via Build(rootElement, style) — this
            // never invokes the parent's BuildChildren, so liOrdinals is
            // empty and MaybeInjectListMarker has to walk the parent.
            var (doc, styles) = Cascade("<ol><li>a</li><li>b</li></ol>", null);
            // Find the second <li> in document order.
            var lis = new List<Element>();
            CollectLis(doc, lis);
            Assert.That(lis.Count, Is.EqualTo(2));
            Element li2 = lis[1];

            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            var liStyle = styles[li2];
            var liRoot = bb.Build(li2, liStyle);
            // First child should be the marker for "2."
            Assert.That(liRoot, Is.Not.Null);
            var blockRoot = (BlockBox)liRoot;
            Assert.That(blockRoot.Children.Count, Is.GreaterThan(0));
            var marker = blockRoot.Children[0] as BlockBox;
            Assert.That(marker, Is.Not.Null);
            var run = marker.Children[0] as TextRun;
            Assert.That(run, Is.Not.Null);
            Assert.That(run.Text, Is.EqualTo("2."));
            // Fallback walked the parent to find the index.
            Assert.That(bb.ListMarkerOrdinalWalks, Is.EqualTo(1));
        }

        static void CollectLis(Node n, List<Element> lis) {
            if (n is Element e && e.TagName == "li") lis.Add(e);
            foreach (var c in n.Children) CollectLis(c, lis);
        }

        // ----- Snapshot builder parity -------------------------------------

        static (Box root, SnapshotBoxBuilder builder) BuildSnapshotWithBuilder(string html) {
            // Snapshot-mode cascade produces both the per-Element ComputedStyle
            // dict AND a populated `LastSnapshot` that SnapshotBoxBuilder
            // consumes via its NodeId-indexed style accessor.
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(ListUA))
            };
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var snap = engine.LastSnapshot;
            var arr = SnapshotStyleArray.FromMap(snap, styles);
            var sb = new SnapshotBoxBuilder(arr.At);
            return (sb.BuildFromSnapshot(snap), sb);
        }

        [Test]
        public void Snapshot_ol_100_items_get_consecutive_ordinals() {
            var html = new System.Text.StringBuilder("<ol>");
            for (int i = 0; i < 100; i++) html.Append("<li>x</li>");
            html.Append("</ol>");
            var (root, builder) = BuildSnapshotWithBuilder(html.ToString());
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers.Count, Is.EqualTo(100));
            for (int i = 0; i < 100; i++) {
                Assert.That(markers[i], Is.EqualTo((i + 1).ToString() + "."));
            }
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0),
                "Snapshot path should also seed liOrdinals via BuildChildren.");
        }

        [Test]
        public void Snapshot_ol_reversed_start_5_counts_down() {
            var (root, builder) = BuildSnapshotWithBuilder(
                "<ol start=\"5\" reversed><li>a</li><li>b</li><li>c</li><li>d</li><li>e</li></ol>");
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers, Is.EqualTo(new[] { "5.", "4.", "3.", "2.", "1." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_li_value_attribute_resets_counter() {
            var (root, builder) = BuildSnapshotWithBuilder(
                "<ol><li>a</li><li>b</li><li value=\"42\">c</li><li>d</li><li>e</li></ol>");
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers, Is.EqualTo(new[] { "1.", "2.", "42.", "43.", "44." }));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0));
        }

        // Wall-time pin (BoxBuilder). Building a 1000-item `<ol>` should be
        // fast — pre-fix walked 1+2+...+1000 = 500_500 sibling probes; post-
        // fix walks each sibling once during PrecomputeLiOrdinals (1_000
        // probes) plus the per-li dict lookup. We pin a generous 1-second
        // ceiling for the whole 1000-item build; CI bench shows the build
        // completes in low tens of milliseconds on a developer machine.
        [Test]
        public void Ol_1000_items_builds_under_one_second() {
            var sb = new System.Text.StringBuilder("<ol>");
            for (int i = 0; i < 1000; i++) sb.Append("<li>x</li>");
            sb.Append("</ol>");
            string html = sb.ToString();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (root, _) = BuildWithBuilder(html);
            sw.Stop();
            // Sanity: actually produced markers.
            int liCount = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.TagName == "li") liCount++;
            }
            Assert.That(liCount, Is.EqualTo(1000));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1000),
                $"1000-item <ol> build took {sw.ElapsedMilliseconds} ms; expected < 1000 ms with O(N) ordinal precompute.");
        }

        [Test]
        public void Snapshot_ol_1000_items_walk_count_is_zero_on_precomputed_path() {
            var html = new System.Text.StringBuilder("<ol>");
            for (int i = 0; i < 1000; i++) html.Append("<li>x</li>");
            html.Append("</ol>");
            var (root, builder) = BuildSnapshotWithBuilder(html.ToString());
            var markers = MarkerTextsInOrder(root);
            Assert.That(markers.Count, Is.EqualTo(1000));
            Assert.That(markers[0], Is.EqualTo("1."));
            Assert.That(markers[999], Is.EqualTo("1000."));
            Assert.That(builder.ListMarkerOrdinalWalks, Is.EqualTo(0),
                "1000-item list must not fall back to the O(siblings) walk on the snapshot path either.");
        }
    }
}
