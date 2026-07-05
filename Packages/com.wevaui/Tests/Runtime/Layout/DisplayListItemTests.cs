using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Audit tests for CSS Display L3 §2 / CSS Lists L3 §2 — `display: list-item`.
    //
    // Spec says: any element with `display: list-item` has a block-level outer
    // display and generates a list-item principal box plus a marker box driven
    // by `list-style-type` / `list-style-image`.
    //
    // v1 state (2026-05-30):
    //   - The CASCADE correctly stores `display: list-item` verbatim.
    //   - The UA stylesheet uses it on `<summary>` elements.
    //   - BoxBuilder.AppendNodeAsBlockChild does NOT have a `list-item` branch,
    //     so the outer box becomes an InlineBox rather than a BlockBox.
    //   - MaybeInjectListMarker only fires for <li> elements whose parent is
    //     <ul> or <ol>; display:list-item on arbitrary elements is ignored.
    //
    // Tests:
    //   1-3  pin the cascade round-trip (green, confirms parser accepts the value)
    //   4-5  pin current layout behaviour (outer box is InlineBox, no marker)
    //   6-7  [Ignore]'d spec-correct assertions documenting the v1 gap
    //   8    UA summary path: display:list-item stored by UA, layout check
    public class DisplayListItemTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // Helper: walk all boxes and count those that are
        // element-less inline-block BlockBoxes (= injected markers).
        static int CountMarkerBoxes(Box root) {
            int count = 0;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element == null && bb.IsInlineBlock) {
                    count++;
                }
            }
            return count;
        }

        // ── 1. Cascade round-trip ──────────────────────────────────────────

        [Test]
        public void Display_list_item_cascade_round_trip() {
            // CSS Display L3 §2: `list-item` is a valid display keyword.
            // The cascade must store and echo it without dropping the value.
            var cs = Compute("#x { display: list-item; }");
            Assert.That(cs.Get("display"), Is.EqualTo("list-item"),
                "display: list-item must survive the cascade unchanged");
        }

        [Test]
        public void Display_list_item_not_inherited() {
            // `display` is NOT inherited (CSS Display L3 §2). A child with
            // no own display value should see the initial "inline", not list-item.
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { display: list-item; }")
            });
            engine.Compute(doc.GetElementById("parent"));
            var childCs = engine.Compute(doc.GetElementById("child"));
            Assert.That(childCs.Get("display"), Is.Not.EqualTo("list-item"),
                "display is non-inherited; child must not see list-item");
        }

        [Test]
        public void Display_list_item_higher_specificity_wins() {
            // A more specific `display: list-item` rule overrides a less
            // specific `display: block` rule on the same element.
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] {
                Author("div { display: block; } #x { display: list-item; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("list-item"),
                "higher-specificity list-item rule must win over div:block");
        }

        // ── 4-5. Spec assertions (C7 + C8 fixed — BlockBox + marker) ────────

        [Test]
        public void Display_list_item_div_allocates_block_box() {
            // CSS Display L3 §2: display:list-item has block-level outer display.
            // BoxBuilder must allocate a BlockBox, not an InlineBox.
            var (root, _) = BuildBoxesOnly(
                "<div id=\"x\">text</div>",
                "#x { display: list-item; }");
            BlockBox found = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.Id == "x") {
                    found = bb;
                }
            }
            Assert.That(found, Is.Not.Null,
                "display:list-item on a <div> must produce a BlockBox (C7 fix)");
        }

        [Test]
        public void Display_list_item_div_marker_injected() {
            // CSS Lists L3 §2: any element with display:list-item and a non-none
            // list-style-type must get an auto-generated marker box.
            var (root, _) = BuildBoxesOnly(
                "<div id=\"x\">text</div>",
                "#x { display: list-item; list-style-type: disc; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(1),
                "display:list-item + list-style-type:disc must produce one marker box (C8 fix)");
        }

        // ── 6. Spec assertions (previously [Ignore]'d) ────────────────────

        [Test]
        public void Spec_display_list_item_outer_box_must_be_block_level() {
            // Per CSS Display L3 §2, `display: list-item` has block-level outer
            // display. BoxBuilder must allocate a BlockBox, not an InlineBox.
            var (root, _) = BuildBoxesOnly(
                "<div id=\"x\">text</div>",
                "#x { display: list-item; }");
            BlockBox found = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.Id == "x") {
                    found = bb;
                }
            }
            Assert.That(found, Is.Not.Null,
                "spec: element with display:list-item must produce a BlockBox");
        }

        [Test]
        public void Spec_display_list_item_must_inject_marker_for_list_style_type() {
            // A non-<li> element with `display: list-item` and a non-none
            // list-style-type must get an auto-generated marker box.
            var (root, _) = BuildBoxesOnly(
                "<div id=\"x\">text</div>",
                "#x { display: list-item; list-style-type: disc; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(1),
                "spec: display:list-item + list-style-type:disc must produce one marker box");
        }

        // ── 7. Additional coverage (C7 + C8 spec) ────────────────────────

        [Test]
        public void Display_list_item_span_gets_marker() {
            // A <span> with display:list-item must also get a marker box —
            // the fix is display-gated, not tag-gated.
            var (root, _) = BuildBoxesOnly(
                "<span id=\"x\">A</span>",
                "#x { display: list-item; list-style-type: disc; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(1),
                "display:list-item on <span> must inject a marker box");
        }

        [Test]
        public void Display_list_item_button_gets_marker() {
            // A <button> element with display:list-item must get a marker box.
            var (root, _) = BuildBoxesOnly(
                "<button id=\"x\">Click</button>",
                "#x { display: list-item; list-style-type: decimal; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(1),
                "display:list-item on <button> must inject a marker box");
        }

        [Test]
        public void Display_list_item_multiple_divs_all_get_markers() {
            // Multiple sibling elements with display:list-item each receive
            // their own marker box (spec: each principal list-item box generates
            // one marker).
            var (root, _) = BuildBoxesOnly(
                "<div class=\"item\">A</div><div class=\"item\">B</div><div class=\"item\">C</div>",
                ".item { display: list-item; list-style-type: decimal; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(3),
                "three display:list-item divs must each inject a marker box");
        }

        [Test]
        public void Display_list_item_mixed_li_and_div_both_get_markers() {
            // A mix of <li> (which gets display:list-item from the UA stylesheet)
            // and a <div> with explicit display:list-item — both must get markers.
            var (root, _) = BuildBoxesOnly(
                "<ul><li>Native</li></ul><div id=\"d\">Custom</div>",
                "#d { display: list-item; list-style-type: disc; }");
            int markers = CountMarkerBoxes(root);
            // <li> inside <ul> → 1 marker; <div> → 1 marker; total = 2.
            Assert.That(markers, Is.EqualTo(2),
                "an <li> and a display:list-item <div> must each get a marker box");
        }

        [Test]
        public void Display_list_item_list_style_type_none_suppresses_marker() {
            // list-style-type: none must suppress text markers even when
            // display: list-item is set (applies to non-<li> elements too).
            var (root, _) = BuildBoxesOnly(
                "<div id=\"x\">text</div>",
                "#x { display: list-item; list-style-type: none; }");
            int markers = CountMarkerBoxes(root);
            Assert.That(markers, Is.EqualTo(0),
                "display:list-item + list-style-type:none must produce no marker box");
        }

        // ── 8. UA summary element ──────────────────────────────────────────

        [Test]
        public void Ua_summary_display_list_item_cascade_stored() {
            // The UA stylesheet assigns `display: list-item` to <summary>.
            // Confirm the cascade stores this value when the element is computed
            // with the UA stylesheet included (BuiltinUserAgent does not set it,
            // so we supply the UA rule explicitly here).
            var doc = Html("<details><summary id=\"s\">Title</summary></details>");
            var engine = new CascadeEngine(new[] {
                OriginatedStylesheet.UserAgent(Css("details > summary { display: list-item; }"))
            });
            var cs = engine.Compute(doc.GetElementById("s"));
            Assert.That(cs.Get("display"), Is.EqualTo("list-item"),
                "UA display:list-item on summary must be stored by the cascade");
        }
    }
}
