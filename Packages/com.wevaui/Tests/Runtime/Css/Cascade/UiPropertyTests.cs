using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Audit (2026-05-16): per CSS_FEATURES.md, `cursor`, `pointer-events`,
    // `user-select`, `caret-color`, and `accent-color` are registered in
    // CssProperties.cs but had zero dedicated cascade coverage. They are
    // string-pass-through properties — the parser stores the raw value and
    // the cascade carries it; downstream consumers (InputRenderer for caret/
    // accent colour, HitTest for pointer-events, MouseCursorService for
    // cursor) read the string at use-time.
    //
    // These tests confirm each property survives the parse → cascade → Get
    // round-trip with the expected keyword/colour preserved verbatim.
    //
    // v1 caveats: `user-select` is parser-only (no actual selection-blocking
    // engine), `accent-color`/`caret-color` are wired to InputRenderer but
    // only sampled when an input has focus, and `cursor` only feeds the
    // hover-state mouse-cursor pipeline (no image() / URL() fallback support).
    public class UiPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle ComputeWith(string css) {
            var doc = Html("<div id=\"x\" class=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        [Test]
        public void Cursor_pointer_round_trips_through_cascade() {
            var cs = ComputeWith(".x { cursor: pointer; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("pointer"));
        }

        [Test]
        public void Cursor_default_keyword_round_trips() {
            var cs = ComputeWith(".x { cursor: default; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("default"));
        }

        [Test]
        public void Cursor_text_keyword() {
            var cs = ComputeWith(".x { cursor: text; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("text"));
        }

        [Test]
        public void Cursor_not_allowed() {
            // `not-allowed` is the most-commonly-authored multi-word cursor
            // keyword; verify the parser doesn't choke on the hyphen and the
            // cascade preserves both halves.
            var cs = ComputeWith(".x { cursor: not-allowed; }");
            Assert.That(cs.Get("cursor"), Is.EqualTo("not-allowed"));
        }

        [Test]
        public void Pointer_events_none_round_trips() {
            var cs = ComputeWith(".x { pointer-events: none; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("none"));
        }

        [Test]
        public void Pointer_events_auto_round_trips() {
            var cs = ComputeWith(".x { pointer-events: auto; }");
            Assert.That(cs.Get("pointer-events"), Is.EqualTo("auto"));
        }

        [Test]
        public void User_select_none_round_trips() {
            // v1: `user-select` is parser-only — registered in CssProperties
            // as a round-trip string property (see the "round-trip properties"
            // comment in CssProperties.cs), but no selection-blocking engine
            // consumes the value. This test pins the cascade round-trip so a
            // future selection implementation can add behavioural coverage
            // on top.
            var cs = ComputeWith(".x { user-select: none; }");
            Assert.That(cs.Get("user-select"), Is.EqualTo("none"));
        }

        [Test]
        public void User_select_text_round_trips() {
            // v1: parser-only, see Note above on `user-select`.
            var cs = ComputeWith(".x { user-select: text; }");
            Assert.That(cs.Get("user-select"), Is.EqualTo("text"));
        }

        [Test]
        public void Caret_color_value_round_trips() {
            // CSS Basic User Interface 4 §5.4 — `caret-color` colours the
            // text-input insertion caret. Inherited string property.
            var cs = ComputeWith(".x { caret-color: red; }");
            Assert.That(cs.Get("caret-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Accent_color_value_round_trips() {
            // CSS Basic User Interface 4 §5.5 — `accent-color` tints UA-drawn
            // form-control accents. Hex colours should round-trip without
            // being re-serialised by the cascade.
            var cs = ComputeWith(".x { accent-color: #b388ff; }");
            Assert.That(cs.Get("accent-color"), Is.EqualTo("#b388ff"));
        }
    }
}
