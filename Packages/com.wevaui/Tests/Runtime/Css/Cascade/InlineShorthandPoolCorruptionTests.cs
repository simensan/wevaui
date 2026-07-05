using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Regression tests for the Declaration pool PropertyId-cache corruption bug.
    //
    // Root cause: CascadeScratch.RentDeclaration reuses Declaration objects across
    // elements by overwriting Property/ValueText/Important, but did NOT reset the
    // lazily-cached Declaration.cachedId. When element T expanded inline shorthand
    // `padding:24px` and the cascade accessed pool[0].PropertyId (caching
    // padding-top's property id), then element U expanded inline `background:#123`
    // and received pool[0] as background-color — pool[0].cachedId still held
    // padding-top's id, so style.Set(padding_top_id, "#123") wrote the wrong slot.
    //
    // Fix: RentDeclaration calls InvalidatePropertyIdCache() on every reused slot.
    public class InlineShorthandPoolCorruptionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        // Primary repro: compute T (inline padding+border shorthand) then U (inline
        // background shorthand). U's padding-top must come from .c (12px), not from
        // U's own background value leaking into the wrong slot.
        [Test]
        public void Second_element_padding_top_not_corrupted_by_first_element_inline_shorthand() {
            var doc = Html(
                "<div id='t' class='c' style='padding:24px; border:2px solid #fbbf24'>x</div>" +
                "<div id='u' class='c' style='background:#123'>y</div>");
            var engine = new CascadeEngine(new[] {
                Author("* { box-sizing: border-box; margin: 0; padding: 0; } .c { padding: 12px; }")
            });

            var t = engine.Compute(doc.GetElementById("t"));
            var u = engine.Compute(doc.GetElementById("u"));

            // T's inline padding wins (24px).
            Assert.That(t.Get("padding-top"),    Is.EqualTo("24px"), "T padding-top");
            Assert.That(t.Get("padding-left"),   Is.EqualTo("24px"), "T padding-left");

            // U has no inline padding — .c rule wins (12px).
            Assert.That(u.Get("padding-top"),    Is.EqualTo("12px"), "U padding-top should be 12px, not U's background value");
            Assert.That(u.Get("padding-left"),   Is.EqualTo("12px"), "U padding-left should be 12px");

            // U's inline background must be correct.
            Assert.That(u.Get("background-color"), Is.EqualTo("#123"), "U background-color");
        }

        // Symmetric repro: first element has inline border (12-longhand expansion),
        // second has inline padding — pool slot aliasing must not cross-contaminate.
        [Test]
        public void Border_first_then_padding_inline_no_cross_contamination() {
            var doc = Html(
                "<div id='a' class='base' style='border:3px dashed red'>a</div>" +
                "<div id='b' class='base' style='padding:8px'>b</div>");
            var engine = new CascadeEngine(new[] {
                Author(".base { background-color: blue; }")
            });

            var a = engine.Compute(doc.GetElementById("a"));
            var b = engine.Compute(doc.GetElementById("b"));

            // A: inline border wins over default "none".
            Assert.That(a.Get("border-top-style"),  Is.EqualTo("dashed"), "A border-top-style");
            Assert.That(a.Get("border-top-color"),  Is.EqualTo("red"),    "A border-top-color");
            Assert.That(a.Get("border-top-width"),  Is.EqualTo("3px"),    "A border-top-width");

            // B: inline padding wins; background comes from .base.
            Assert.That(b.Get("padding-top"),       Is.EqualTo("8px"),  "B padding-top");
            Assert.That(b.Get("padding-right"),     Is.EqualTo("8px"),  "B padding-right");
            Assert.That(b.Get("background-color"),  Is.EqualTo("blue"), "B background-color");

            // B's border must be initial (not leaked from A).
            Assert.That(b.Get("border-top-style"),  Is.EqualTo("none"), "B border-top-style must be initial, not A's dashed");
        }

        // Verify that computing T alone never corrupts T's own values (sanity).
        [Test]
        public void Single_element_inline_padding_shorthand_correct() {
            var doc = Html("<div id='x' style='padding:16px'>x</div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var cs = engine.Compute(doc.GetElementById("x"));
            Assert.That(cs.Get("padding-top"),    Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-right"),  Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-bottom"), Is.EqualTo("16px"));
            Assert.That(cs.Get("padding-left"),   Is.EqualTo("16px"));
        }

        // Three-element sequence: each element has a different inline shorthand.
        // Pool reuse across all three must produce correct per-element values.
        [Test]
        public void Three_elements_different_inline_shorthands_no_corruption() {
            var doc = Html(
                "<div id='e1' style='padding:5px'>e1</div>" +
                "<div id='e2' style='margin:10px'>e2</div>" +
                "<div id='e3' style='background:green'>e3</div>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());

            var e1 = engine.Compute(doc.GetElementById("e1"));
            var e2 = engine.Compute(doc.GetElementById("e2"));
            var e3 = engine.Compute(doc.GetElementById("e3"));

            Assert.That(e1.Get("padding-top"),    Is.EqualTo("5px"),         "e1 padding-top");
            Assert.That(e1.Get("margin-top"),     Is.EqualTo("0"),           "e1 margin-top (initial)");
            Assert.That(e2.Get("margin-top"),     Is.EqualTo("10px"),        "e2 margin-top");
            Assert.That(e2.Get("padding-top"),    Is.EqualTo("0"),           "e2 padding-top (initial)");
            Assert.That(e3.Get("background-color"), Is.EqualTo("green"),     "e3 background-color");
            Assert.That(e3.Get("padding-top"),    Is.EqualTo("0"),           "e3 padding-top (initial, not corrupted)");
            Assert.That(e3.Get("margin-top"),     Is.EqualTo("0"),           "e3 margin-top (initial, not corrupted)");
        }
    }
}
