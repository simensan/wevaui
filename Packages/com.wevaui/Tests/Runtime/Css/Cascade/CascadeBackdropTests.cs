using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CascadeEngine.ComputeBackdrop wires the `::backdrop` rule bucket into
    // a per-host computed style. Selectors with PseudoElement == "backdrop"
    // are routed to a separate compile-time list (see CascadeEngine.cs:310);
    // ComputeBackdrop walks that list, matches it against the originating
    // host element, applies specificity sort, and forces UA defaults that
    // make the backdrop fill the viewport. Sibling pseudo-elements
    // (::before / ::after / ::placeholder / ::selection) have their own
    // ComputeFoo entry points and rule buckets; ::marker is still dropped.
    public class CascadeBackdropTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        [Test]
        public void Bare_backdrop_rule_matches_any_host() {
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(new[] { Author("::backdrop { background-color: red; }") });
            var dialog = FindByTag(doc, "dialog");
            var bs = engine.ComputeBackdrop(dialog);
            Assert.That(bs.Get("background-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Tag_qualified_backdrop_rule_matches_only_matching_host() {
            var doc = Html("<dialog data-modal></dialog><div popover data-popover-open></div>");
            var engine = new CascadeEngine(new[] { Author("dialog::backdrop { background-color: red; }") });
            var dialog = FindByTag(doc, "dialog");
            var popover = FindByTag(doc, "div");

            var dialogBs = engine.ComputeBackdrop(dialog);
            var popoverBs = engine.ComputeBackdrop(popover);

            Assert.That(dialogBs.Get("background-color"), Is.EqualTo("red"));
            // Popover doesn't satisfy `dialog` selector — initial bg-color.
            Assert.That(popoverBs.Get("background-color"), Is.EqualTo("transparent"));
        }

        [Test]
        public void Backdrop_forces_viewport_filling_position() {
            // Even with no `::backdrop` rules at all, ComputeBackdrop returns
            // a style that fills the viewport via UA-baked overrides.
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("position"), Is.EqualTo("fixed"));
            Assert.That(bs.Get("top"), Is.EqualTo("0"));
            Assert.That(bs.Get("right"), Is.EqualTo("0"));
            Assert.That(bs.Get("bottom"), Is.EqualTo("0"));
            Assert.That(bs.Get("left"), Is.EqualTo("0"));
            Assert.That(bs.Get("display"), Is.EqualTo("block"));
            Assert.That(bs.Get("box-sizing"), Is.EqualTo("border-box"));
        }

        [Test]
        public void Author_cannot_override_backdrop_position_via_cascade() {
            // The UA defaults overwrite anything the author cascaded for the
            // viewport-filling longhands. Authors who want differently sized
            // overlays should style the dialog itself, not the backdrop.
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author("::backdrop { position: relative; top: 50px; left: 20px; }")
            });
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("position"), Is.EqualTo("fixed"));
            Assert.That(bs.Get("top"), Is.EqualTo("0"));
            Assert.That(bs.Get("left"), Is.EqualTo("0"));
        }

        [Test]
        public void Specificity_resolves_competing_backdrop_rules() {
            var doc = Html("<dialog id=\"d\" class=\"modal\" data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author(".modal::backdrop { background-color: red; } #d::backdrop { background-color: blue; }")
            });
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("background-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Source_order_breaks_ties_within_same_specificity() {
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author("dialog::backdrop { background-color: red; } dialog::backdrop { background-color: green; }")
            });
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("background-color"), Is.EqualTo("green"));
        }

        [Test]
        public void Important_overrides_normal_in_same_origin() {
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author("#d::backdrop { background-color: red !important; } dialog::backdrop { background-color: blue; }")
            });
            // No #d here — but the !important still beats normal even at lower
            // specificity (origin sub-step lifts !important above normal).
            var dialog = FindByTag(doc, "dialog");
            dialog.SetAttribute("id", "d");
            // Re-compute with the id applied.
            var bs = engine.ComputeBackdrop(dialog);
            Assert.That(bs.Get("background-color"), Is.EqualTo("red"));
        }

        [Test]
        public void Inheritance_does_not_pull_from_host_or_ancestors() {
            // ::backdrop only inherits from itself per CSS Fullscreen — there
            // is no parent backdrop in v1, so unset inherited properties fall
            // back to their initial values. The host's color must NOT bleed
            // into the backdrop.
            var doc = Html("<body style=\"color: red\"><dialog data-modal></dialog></body>");
            var engine = new CascadeEngine(new[] { Author("body { color: red; }") });
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Custom_property_cascades_into_backdrop() {
            var doc = Html("<dialog data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author("::backdrop { --tint: blue; background-color: var(--tint); }")
            });
            var bs = engine.ComputeBackdrop(FindByTag(doc, "dialog"));
            Assert.That(bs.Get("background-color"), Is.EqualTo("blue"));
        }

        [Test]
        public void Returns_null_for_null_host() {
            var engine = new CascadeEngine(System.Array.Empty<OriginatedStylesheet>());
            Assert.That(engine.ComputeBackdrop(null), Is.Null);
        }

        [Test]
        public void Non_backdrop_pseudo_elements_remain_unsupported() {
            // ::before / ::after rules still drop at compile time. Confirm
            // they don't accidentally start matching the host now that the
            // pseudo-element gate is more lenient.
            var doc = Html("<div id=\"x\">hello</div>");
            var engine = new CascadeEngine(new[] {
                Author("::before { color: red; } #x { color: green; }")
            });
            var x = doc.GetElementById("x");
            var cs = engine.Compute(x);
            // The `::before` rule should not have applied to x's own style.
            Assert.That(cs.Get("color"), Is.EqualTo("green"));
        }

        [Test]
        public void Backdrop_rule_does_not_match_via_compute_on_host_element() {
            // Confirm a `::backdrop` rule's declarations don't leak into the
            // host element's regular cascade output.
            var doc = Html("<dialog id=\"d\" data-modal></dialog>");
            var engine = new CascadeEngine(new[] {
                Author("dialog::backdrop { background-color: red; }")
            });
            var dialog = doc.GetElementById("d");
            var hostStyle = engine.Compute(dialog);
            Assert.That(hostStyle.Get("background-color"), Is.Not.EqualTo("red"));
        }
    }
}
