using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS UI Level 4 §8.1 — full `cursor` keyword enumeration.
    //
    // `cursor` is a string-pass-through property (inherited). The cascade engine
    // stores the authored value and carries it to the computed style; the
    // MouseCursorService reads the value at hover time to set the OS cursor.
    //
    // UiPropertyTests.cs covers the four most common keywords (auto, default,
    // text, not-allowed). This file pins the remaining 33 keywords from the
    // CSS UI L4 §8.1.2 table, plus URL-based image cursor round-trip, plus
    // inheritance and non-inheritance-when-set behaviour.
    //
    // The full spec keyword set (37 keywords total):
    //   General-purpose:       auto, default, none
    //   Link + status:         context-menu, help, pointer, progress, wait
    //   Selection:             cell, crosshair, text, vertical-text
    //   Drag + drop:           alias, copy, move, no-drop, not-allowed, grab, grabbing
    //   Resize (1-axis):       e-resize, n-resize, s-resize, w-resize
    //   Resize (diagonal):     ne-resize, nw-resize, se-resize, sw-resize
    //   Resize (bidirectional):ew-resize, ns-resize, nesw-resize, nwse-resize
    //   Resize (table):        col-resize, row-resize
    //   Scroll:                all-scroll
    //   Zoom:                  zoom-in, zoom-out
    //
    // UiPropertyTests already covers: auto, default, text, not-allowed. Those
    // four are NOT duplicated here.
    public class CursorKeywordTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── registry ──────────────────────────────────────────────────────

        [Test]
        public void Cursor_is_registered_as_inherited() {
            // CSS UI 4 §8.1: cursor is inherited so a single parent rule
            // propagates to all descendants without per-element rules.
            Assert.That(CssProperties.IsInherited("cursor"), Is.True,
                "cursor must be flagged inherited in CssProperties");
        }

        [Test]
        public void Cursor_initial_value_is_auto() {
            // CSS UI 4 §8.1: initial value is `auto`.
            var cs = Compute("");
            Assert.That(cs.Get("cursor"), Is.EqualTo("auto"));
        }

        // ── general-purpose keywords ──────────────────────────────────────

        [Test]
        public void Cursor_none_round_trips() {
            var cs = Compute("#x { cursor: none; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("none"));
        }

        // ── link + status keywords ────────────────────────────────────────

        [Test]
        public void Cursor_context_menu_round_trips() {
            var cs = Compute("#x { cursor: context-menu; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("context-menu"));
        }

        [Test]
        public void Cursor_help_round_trips() {
            var cs = Compute("#x { cursor: help; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("help"));
        }

        [Test]
        public void Cursor_pointer_round_trips() {
            // Most-used cursor keyword in game UI (interactive elements).
            var cs = Compute("#x { cursor: pointer; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("pointer"));
        }

        [Test]
        public void Cursor_progress_round_trips() {
            var cs = Compute("#x { cursor: progress; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("progress"));
        }

        [Test]
        public void Cursor_wait_round_trips() {
            var cs = Compute("#x { cursor: wait; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("wait"));
        }

        // ── selection keywords ────────────────────────────────────────────

        [Test]
        public void Cursor_cell_round_trips() {
            var cs = Compute("#x { cursor: cell; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("cell"));
        }

        [Test]
        public void Cursor_crosshair_round_trips() {
            var cs = Compute("#x { cursor: crosshair; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("crosshair"));
        }

        [Test]
        public void Cursor_vertical_text_round_trips() {
            var cs = Compute("#x { cursor: vertical-text; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("vertical-text"));
        }

        // ── drag and drop keywords ────────────────────────────────────────

        [Test]
        public void Cursor_alias_round_trips() {
            var cs = Compute("#x { cursor: alias; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("alias"));
        }

        [Test]
        public void Cursor_copy_round_trips() {
            var cs = Compute("#x { cursor: copy; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("copy"));
        }

        [Test]
        public void Cursor_move_round_trips() {
            var cs = Compute("#x { cursor: move; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("move"));
        }

        [Test]
        public void Cursor_no_drop_round_trips() {
            var cs = Compute("#x { cursor: no-drop; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("no-drop"));
        }

        [Test]
        public void Cursor_grab_round_trips() {
            var cs = Compute("#x { cursor: grab; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("grab"));
        }

        [Test]
        public void Cursor_grabbing_round_trips() {
            var cs = Compute("#x { cursor: grabbing; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("grabbing"));
        }

        // ── single-axis resize keywords ───────────────────────────────────

        [Test]
        public void Cursor_e_resize_round_trips() {
            var cs = Compute("#x { cursor: e-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("e-resize"));
        }

        [Test]
        public void Cursor_n_resize_round_trips() {
            var cs = Compute("#x { cursor: n-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("n-resize"));
        }

        [Test]
        public void Cursor_s_resize_round_trips() {
            var cs = Compute("#x { cursor: s-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("s-resize"));
        }

        [Test]
        public void Cursor_w_resize_round_trips() {
            var cs = Compute("#x { cursor: w-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("w-resize"));
        }

        // ── diagonal resize keywords ──────────────────────────────────────

        [Test]
        public void Cursor_ne_resize_round_trips() {
            var cs = Compute("#x { cursor: ne-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("ne-resize"));
        }

        [Test]
        public void Cursor_nw_resize_round_trips() {
            var cs = Compute("#x { cursor: nw-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("nw-resize"));
        }

        [Test]
        public void Cursor_se_resize_round_trips() {
            var cs = Compute("#x { cursor: se-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("se-resize"));
        }

        [Test]
        public void Cursor_sw_resize_round_trips() {
            var cs = Compute("#x { cursor: sw-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("sw-resize"));
        }

        // ── bidirectional resize keywords ─────────────────────────────────

        [Test]
        public void Cursor_ew_resize_round_trips() {
            var cs = Compute("#x { cursor: ew-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("ew-resize"));
        }

        [Test]
        public void Cursor_ns_resize_round_trips() {
            var cs = Compute("#x { cursor: ns-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("ns-resize"));
        }

        [Test]
        public void Cursor_nesw_resize_round_trips() {
            var cs = Compute("#x { cursor: nesw-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("nesw-resize"));
        }

        [Test]
        public void Cursor_nwse_resize_round_trips() {
            var cs = Compute("#x { cursor: nwse-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("nwse-resize"));
        }

        // ── table resize keywords ─────────────────────────────────────────

        [Test]
        public void Cursor_col_resize_round_trips() {
            var cs = Compute("#x { cursor: col-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("col-resize"));
        }

        [Test]
        public void Cursor_row_resize_round_trips() {
            var cs = Compute("#x { cursor: row-resize; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("row-resize"));
        }

        // ── scroll keywords ───────────────────────────────────────────────

        [Test]
        public void Cursor_all_scroll_round_trips() {
            var cs = Compute("#x { cursor: all-scroll; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("all-scroll"));
        }

        // ── zoom keywords ─────────────────────────────────────────────────

        [Test]
        public void Cursor_zoom_in_round_trips() {
            var cs = Compute("#x { cursor: zoom-in; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("zoom-in"));
        }

        [Test]
        public void Cursor_zoom_out_round_trips() {
            var cs = Compute("#x { cursor: zoom-out; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("zoom-out"));
        }

        // ── URL image cursor ──────────────────────────────────────────────

        [Test]
        public void Cursor_url_with_keyword_fallback_round_trips() {
            // CSS UI 4 §8.1: cursor may begin with url() image sources followed
            // by a mandatory fallback keyword. The cascade must carry the full
            // value string verbatim so the MouseCursorService can parse both
            // the image URL and the fallback at use-time.
            var cs = Compute("#x { cursor: url(custom.png), pointer; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("url(custom.png), pointer"));
        }

        [Test]
        public void Cursor_url_with_hotspot_and_keyword_fallback_round_trips() {
            // url() cursors may include an x y hotspot pair before the comma.
            var cs = Compute("#x { cursor: url(crosshair.png) 8 8, crosshair; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("url(crosshair.png) 8 8, crosshair"));
        }

        // ── inheritance mechanics ─────────────────────────────────────────

        [Test]
        public void Cursor_inherits_from_parent_when_child_has_no_rule() {
            // cursor is inherited — parent's pointer propagates to child.
            var cs = ComputeChild("#p { cursor: pointer; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("pointer"),
                "cursor inherits to child when child has no explicit rule");
        }

        [Test]
        public void Cursor_child_rule_overrides_inherited_parent_value() {
            // Child's explicit rule beats the inherited parent value.
            var cs = ComputeChild("#p { cursor: pointer; } #x { cursor: crosshair; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("crosshair"),
                "child's explicit cursor rule wins over inherited parent value");
        }
    }
}
