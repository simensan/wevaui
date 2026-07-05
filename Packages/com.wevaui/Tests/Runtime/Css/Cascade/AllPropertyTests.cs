using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Cascade Level 4 §3.2 / §7 — the `all` shorthand.
    //
    // `all` is a shorthand (NOT registered as a property) that resets every
    // CSS property — except `direction`, `unicode-bidi`, and custom
    // properties — to the value implied by the keyword:
    //
    //   initial       — every property resolves to its spec-defined initial value.
    //   inherit       — every property inherits from its parent.
    //   unset         — each property resolves as: inherit if inherited, initial if not.
    //   revert        — roll back to UA/user origin value (treated as initial in v1
    //                   per A4; once revert origin-stack lands these become real reverts).
    //   revert-layer  — CSS Cascade L5 §6.3; same v1 limitation as `revert`.
    //
    // Implementation: `Runtime/Css/Cascade/Shorthands/AllShorthandExpander.cs`.
    public class AllPropertyTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        // ── shorthand contract ──────────────────────────────────────────

        [Test]
        public void All_is_not_registered_as_a_property() {
            // `all` is a SHORTHAND, not a longhand. The cascade expands it
            // via ShorthandRegistry but never writes an "all" slot.
            var cs = Compute("#x { all: initial; }");
            Assert.That(cs.Get("all"), Is.EqualTo("").Or.Null,
                "all is a shorthand — no slot to read");
        }

        [Test]
        public void All_does_not_reset_custom_properties() {
            // Custom properties are excluded from `all` per CSS Cascade L4 §3.2.
            var cs = Compute("#x { --my-color: coral; all: initial; }");
            Assert.That(cs.Get("--my-color"), Is.EqualTo("coral"),
                "custom property is unaffected by all: initial");
        }

        // ── spec-correct expansion ──────────────────────────────────────

        [Test]
        public void All_initial_resets_color_to_its_initial_value() {
            // CSS Cascade L4 §3.2 + CSS Color L3 §3.2 — color's initial is `black`
            // (canonicalCurrentColor in our engine, but the spec value is black).
            var cs = Compute("#x { color: red; all: initial; }");
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "all: initial must reset color to its initial value");
        }

        [Test]
        public void All_initial_resets_font_size_to_initial() {
            var cs = Compute("#x { font-size: 32px; all: initial; }");
            Assert.That(cs.Get("font-size"), Is.EqualTo("16px"),
                "all: initial must reset font-size to 16px");
        }

        [Test]
        public void All_initial_does_not_reset_direction() {
            // `direction` is excluded from `all` per CSS Cascade L4 §3.2.
            var cs = Compute("#x { direction: rtl; all: initial; }");
            Assert.That(cs.Get("direction"), Is.EqualTo("rtl"),
                "direction is excluded from all — must survive all: initial");
        }

        [Test]
        public void All_initial_does_not_reset_unicode_bidi() {
            // `unicode-bidi` is excluded from `all` per CSS Cascade L4 §3.2.
            var cs = Compute("#x { unicode-bidi: isolate; all: initial; }");
            Assert.That(cs.Get("unicode-bidi"), Is.EqualTo("isolate"),
                "unicode-bidi is excluded from all — must survive all: initial");
        }

        [Test]
        public void All_inherit_forces_non_inherited_display_to_inherit() {
            var doc = Html("<div id=\"parent\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { display: flex; } #x { all: inherit; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("flex"),
                "all: inherit forces inheritance of non-inherited display");
        }

        [Test]
        public void All_unset_on_inherited_property_acts_as_inherit() {
            var doc = Html("<div id=\"parent\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { color: blue; } #x { color: green; all: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("color"), Is.EqualTo("blue"),
                "all: unset on inherited color should inherit parent blue");
        }

        [Test]
        public void All_unset_on_non_inherited_property_acts_as_initial() {
            var doc = Html("<div id=\"parent\"><span id=\"x\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { display: flex; } #x { display: grid; all: unset; }")
            });
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("display"), Is.EqualTo("inline"),
                "all: unset on non-inherited display should reset to initial (inline)");
        }

        [Test]
        public void All_revert_with_no_origin_below_author_resolves_to_initial() {
            // A4 — revert collapses to initial when there is no UA/user origin rule
            // below the author origin. The two-target resolution (real revert
            // origin stack) is tracked separately; for now `all: revert` ≡ `all: initial`.
            var cs = Compute("#x { color: red; all: revert; }");
            Assert.That(cs.Get("color"), Is.EqualTo("black"),
                "all: revert with no UA origin resolves to initial (black)");
        }

        // ── source-order with all ───────────────────────────────────────

        [Test]
        public void All_initial_before_color_lets_later_color_win() {
            // `all: initial` expands to per-longhand `color: initial` at the
            // shorthand's source position; a LATER `color: red` then wins.
            var cs = Compute("#x { all: initial; color: red; }");
            Assert.That(cs.Get("color"), Is.EqualTo("red"),
                "later color: red wins source order over preceding all: initial expansion");
        }

        [Test]
        public void All_invalid_keyword_is_dropped() {
            // CSS Cascade L4 §3.2 — only CSS-wide keywords are valid.
            // A non-keyword token is a parse error → the shorthand is dropped
            // and prior declarations survive.
            var cs = Compute("#x { color: red; all: 12px; }");
            Assert.That(cs.Get("color"), Is.EqualTo("red"),
                "invalid all value dropped; color: red survives");
        }
    }
}
