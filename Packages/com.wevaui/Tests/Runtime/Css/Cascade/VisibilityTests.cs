using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Display Level 3 §4.2 / CSS 2.1 §11.2 — `visibility` property cascade.
    //
    // `visibility` has three values:
    //   visible  — element and its box are rendered normally (initial).
    //   hidden   — element's box is still laid out and takes space, but the
    //              content and borders are not painted. Children remain hidden
    //              unless they explicitly set visibility:visible (see override
    //              tests below).
    //   collapse — same as hidden for non-table elements; on table rows/columns
    //              the row/column is completely removed from layout (no space).
    //
    // Special inheritance rule (CSS 2.1 §11.2):
    //   `visibility` IS inherited — a parent's visibility:hidden propagates to
    //   children — but a child can OVERRIDE back to `visible` to become visible
    //   again. This "inherited but overridable" behaviour is the key difference
    //   from `display:none` (which cannot be overridden by descendants).
    //
    // Note: `visibility:hidden` ≠ `display:none`:
    //   hidden — takes layout space; descendants can override to visible.
    //   none   — removed from layout tree; descendants cannot override.
    public class VisibilityTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static (ComputedStyle parent, ComputedStyle child) ComputeParentChild(string css) {
            var doc = Html("<div id=\"parent\"><span id=\"child\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            var parent = engine.Compute(doc.GetElementById("parent"));
            var child = engine.Compute(doc.GetElementById("child"));
            return (parent, child);
        }

        // ── initial value ─────────────────────────────────────────────────

        [Test]
        public void Visibility_initial_is_visible() {
            // CSS 2.1 §11.2: initial value is `visible`.
            var cs = Compute("");
            Assert.That(cs.Get("visibility"), Is.EqualTo("visible"));
        }

        // ── keyword round-trips ───────────────────────────────────────────

        [Test]
        public void Visibility_visible_round_trips() {
            var cs = Compute("#x { visibility: visible; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("visible"));
        }

        [Test]
        public void Visibility_hidden_round_trips() {
            // `hidden`: element's box still takes space in layout but is
            // not painted. CSS 2.1 §11.2.
            var cs = Compute("#x { visibility: hidden; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("hidden"));
        }

        [Test]
        public void Visibility_collapse_round_trips() {
            // `collapse`: equivalent to `hidden` for non-table elements;
            // on table rows/columns it additionally removes the layout space.
            // CSS 2.1 §11.2.
            var cs = Compute("#x { visibility: collapse; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("collapse"));
        }

        // ── inheritance: propagates from parent ───────────────────────────

        [Test]
        public void Visibility_hidden_inherits_to_child_without_rule() {
            // CSS 2.1 §11.2: `visibility` is inherited. A child with no
            // explicit visibility rule inherits the parent's hidden value.
            var (parent, child) = ComputeParentChild("#parent { visibility: hidden; }");
            Assert.That(parent.Get("visibility"), Is.EqualTo("hidden"));
            Assert.That(child.Get("visibility"), Is.EqualTo("hidden"),
                "child inherits visibility:hidden from parent (no override rule)");
        }

        [Test]
        public void Visibility_collapse_inherits_to_child_without_rule() {
            var (_, child) = ComputeParentChild("#parent { visibility: collapse; }");
            Assert.That(child.Get("visibility"), Is.EqualTo("collapse"),
                "child inherits visibility:collapse from parent");
        }

        // ── override: child can flip back to visible ──────────────────────

        [Test]
        public void Child_can_override_inherited_hidden_to_visible() {
            // The defining spec contract: `visibility` is inherited but a
            // descendant CAN set `visibility: visible` to become visible again.
            // This is the unique semantics that distinguishes visibility from
            // display:none (which cannot be overridden from a descendant).
            var (parent, child) = ComputeParentChild(
                "#parent { visibility: hidden; } #child { visibility: visible; }");
            Assert.That(parent.Get("visibility"), Is.EqualTo("hidden"));
            Assert.That(child.Get("visibility"), Is.EqualTo("visible"),
                "child explicitly sets visible, overriding the inherited hidden");
        }

        [Test]
        public void Child_can_override_inherited_collapse_to_visible() {
            var (_, child) = ComputeParentChild(
                "#parent { visibility: collapse; } #child { visibility: visible; }");
            Assert.That(child.Get("visibility"), Is.EqualTo("visible"),
                "child overrides inherited collapse with explicit visible");
        }

        // ── non-override: child with no rule sees parent's hidden ─────────

        [Test]
        public void Sibling_without_rule_stays_on_parent_hidden() {
            // Three-element tree: parent hidden, one child overrides to visible,
            // another child has no rule — the uninstrumented sibling must inherit
            // hidden, not pick up the sibling's explicit visible.
            var doc = Html("<div id=\"parent\"><span id=\"a\"></span><span id=\"b\"></span></div>");
            var engine = new CascadeEngine(new[] {
                Author("#parent { visibility: hidden; } #a { visibility: visible; }")
            });
            var a = engine.Compute(doc.GetElementById("a"));
            var b = engine.Compute(doc.GetElementById("b"));
            Assert.That(a.Get("visibility"), Is.EqualTo("visible"), "a overrides to visible");
            Assert.That(b.Get("visibility"), Is.EqualTo("hidden"),
                "b has no rule — inherits hidden from parent, not visible from sibling");
        }

        // ── registry checks ───────────────────────────────────────────────

        [Test]
        public void Visibility_is_registered_as_inherited() {
            // CSS 2.1 §11.2: visibility IS inherited. CssProperties.IsInherited
            // drives the inheritance-mask bitset used by FillInherited.
            Assert.That(CssProperties.IsInherited("visibility"), Is.True,
                "visibility must be flagged inherited in CssProperties");
        }

        // ── cascade mechanics ─────────────────────────────────────────────

        [Test]
        public void Visibility_later_rule_wins_over_earlier_same_specificity() {
            // Standard cascade source-order: later rule wins.
            var cs = Compute("#x { visibility: hidden; } #x { visibility: visible; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("visible"),
                "later rule (visible) wins over earlier same-specificity rule (hidden)");
        }

        [Test]
        public void Visibility_important_wins_over_non_important() {
            var cs = Compute("#x { visibility: hidden !important; } #x { visibility: visible; }");
            Assert.That(cs.Get("visibility"), Is.EqualTo("hidden"),
                "!important rule wins regardless of source order");
        }
    }
}
