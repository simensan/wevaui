using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;

namespace Weva.Tests.Css.Cascade {
    // NG6 — pin the documented "null value is the clear form" semantics on
    // ComputedStyle.Set(string)/Set(int) and SetParsed. Downstream readers
    // (stub-property scan, viewport-unit scan, wrapper/decoration default
    // comparisons, parsed-cache state) all tolerate null without an NRE; this
    // suite is the regression pin behind that contract.
    public class ComputedStyleSetNullValueTests {
        [Test]
        public void Set_string_with_null_value_stores_null_and_does_not_throw_NG6() {
            var cs = new ComputedStyle(new Element("div"));
            Assert.DoesNotThrow(() => cs.Set("color", null));
            // TryGet(string) reports occupied=true with value=null — the slot
            // is the documented "cleared" state, distinguishable from
            // never-set (which reports false).
            Assert.That(cs.TryGet("color", out var v), Is.True);
            Assert.That(v, Is.Null);
        }

        [Test]
        public void Set_int_with_null_value_stores_null_and_does_not_throw_NG6() {
            var cs = new ComputedStyle(new Element("div"));
            int colorId = CssProperties.GetId("color");
            Assert.That(colorId, Is.GreaterThanOrEqualTo(0));
            Assert.DoesNotThrow(() => cs.Set(colorId, null));
            Assert.That(cs.Get(colorId), Is.Null);
        }

        [Test]
        public void Set_string_with_null_after_a_real_value_clears_NG6() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
            cs.Set("color", null);
            Assert.That(cs.Get("color"), Is.Null);
        }

        [Test]
        public void SetParsed_with_null_value_marks_slot_failed_and_does_not_throw_NG6() {
            var cs = new ComputedStyle(new Element("div"));
            int colorId = CssProperties.GetId("color");
            Assert.DoesNotThrow(() => cs.SetParsed(colorId, (CssValue)null));
            Assert.That(cs.Get(colorId), Is.Null);
        }

        [Test]
        public void Set_string_happy_path_still_stores_value_NG6() {
            var cs = new ComputedStyle(new Element("div"));
            cs.Set("color", "red");
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }
    }
}
