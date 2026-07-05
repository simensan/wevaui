using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // CSS Transforms L2 — 3D transform properties cascade.
    //
    // Weva's transform pipeline focuses on 2D transforms; the 3D
    // properties are registered for cascade parity and authoring-tool
    // round-tripping but the visual pipeline currently flattens to 2D.
    // These tests pin the parse → cascade → Get round-trip so that
    // future renderer wiring for the 3D path doesn't drop the
    // declarations on the floor.
    //
    // Registration (CssProperties.BuildRegistry):
    //   transform-style       not inherited  initial="flat"          §6
    //   backface-visibility   not inherited  initial="visible"       §6
    //   perspective           not inherited  initial="none"          §6
    //   perspective-origin    not inherited  initial="50% 50%"       §6
    //   transform-box         not inherited  initial="view-box"      §3.4
    //
    // (transform-origin already covered by TransformIndividualPropertyTests.)
    public class Transform3DCascadeTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        static ComputedStyle Compute(string css) {
            var doc = Html("<div id=\"x\"></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("x"));
        }

        static ComputedStyle ComputeChild(string css) {
            var doc = Html("<div id=\"p\"><span id=\"c\"></span></div>");
            var engine = new CascadeEngine(new[] { Author(css) });
            return engine.Compute(doc.GetElementById("c"));
        }

        // ─── transform-style ──────────────────────────────────────────────

        [Test]
        public void TransformStyle_initial_is_flat() {
            // §6: initial = `flat` (children flattened to the 2D plane).
            var cs = Compute("");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("flat"));
        }

        [Test]
        public void TransformStyle_flat_round_trips() {
            var cs = Compute("#x { transform-style: flat; }");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("flat"));
        }

        [Test]
        public void TransformStyle_preserve_3d_round_trips() {
            // §6: `preserve-3d` keeps the element's children in the same
            // 3D space, allowing nested transforms to compose.
            var cs = Compute("#x { transform-style: preserve-3d; }");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("preserve-3d"));
        }

        [Test]
        public void TransformStyle_does_not_inherit() {
            // §6: transform-style is NOT inherited per spec.
            var child = ComputeChild("#p { transform-style: preserve-3d; }");
            Assert.That(child.Get("transform-style"), Is.EqualTo("flat"));
        }

        [Test]
        public void TransformStyle_important_wins_cascade() {
            var cs = Compute("#x { transform-style: preserve-3d !important; transform-style: flat; }");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("preserve-3d"));
        }

        [Test]
        public void TransformStyle_initial_keyword_resets_to_flat() {
            var cs = Compute("#x { transform-style: preserve-3d; transform-style: initial; }");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("flat"));
        }

        // ─── backface-visibility ──────────────────────────────────────────

        [Test]
        public void BackfaceVisibility_initial_is_visible() {
            // §6: initial = `visible`.
            var cs = Compute("");
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("visible"));
        }

        [Test]
        public void BackfaceVisibility_visible_round_trips() {
            var cs = Compute("#x { backface-visibility: visible; }");
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("visible"));
        }

        [Test]
        public void BackfaceVisibility_hidden_round_trips() {
            // §6: `hidden` culls the element when its back face is camera-
            // facing — used for two-sided card flip effects.
            var cs = Compute("#x { backface-visibility: hidden; }");
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("hidden"));
        }

        [Test]
        public void BackfaceVisibility_does_not_inherit() {
            // §6: backface-visibility is NOT inherited per spec.
            var child = ComputeChild("#p { backface-visibility: hidden; }");
            Assert.That(child.Get("backface-visibility"), Is.EqualTo("visible"));
        }

        [Test]
        public void BackfaceVisibility_important_wins_cascade() {
            var cs = Compute("#x { backface-visibility: hidden !important; backface-visibility: visible; }");
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("hidden"));
        }

        // ─── perspective ──────────────────────────────────────────────────

        [Test]
        public void Perspective_initial_is_none() {
            // §6: initial = `none` (no perspective, parallel projection).
            var cs = Compute("");
            Assert.That(cs.Get("perspective"), Is.EqualTo("none"));
        }

        [Test]
        public void Perspective_length_value_round_trips() {
            // §6: perspective takes a <length> — viewer distance in px.
            var cs = Compute("#x { perspective: 800px; }");
            Assert.That(cs.Get("perspective"), Is.EqualTo("800px"));
        }

        [Test]
        public void Perspective_none_keyword_round_trips() {
            var cs = Compute("#x { perspective: none; }");
            Assert.That(cs.Get("perspective"), Is.EqualTo("none"));
        }

        [Test]
        public void Perspective_does_not_inherit() {
            // §6: perspective is NOT inherited per spec.
            var child = ComputeChild("#p { perspective: 600px; }");
            Assert.That(child.Get("perspective"), Is.EqualTo("none"));
        }

        [Test]
        public void Perspective_important_wins_cascade() {
            var cs = Compute("#x { perspective: 600px !important; perspective: 1200px; }");
            Assert.That(cs.Get("perspective"), Is.EqualTo("600px"));
        }

        [Test]
        public void Perspective_initial_keyword_resets_to_none() {
            var cs = Compute("#x { perspective: 600px; perspective: initial; }");
            Assert.That(cs.Get("perspective"), Is.EqualTo("none"));
        }

        // ─── perspective-origin ──────────────────────────────────────────

        [Test]
        public void PerspectiveOrigin_initial_is_center() {
            // §6: initial = `50% 50%` (perspective focused at element centre).
            var cs = Compute("");
            Assert.That(cs.Get("perspective-origin"), Is.EqualTo("50% 50%"));
        }

        [Test]
        public void PerspectiveOrigin_keyword_round_trips() {
            // §6: keyword shorthands `top left` / `right` etc.
            var cs = Compute("#x { perspective-origin: top left; }");
            var v = cs.Get("perspective-origin");
            Assert.That(v, Is.Not.Null);
            // Keyword form may resolve to the literal string or to
            // computed percentage form; accept either.
            Assert.That(v == "top left" || v == "0% 0%" || v == "left top" || v == "0 0",
                $"perspective-origin top-left round-trip got '{v}'");
        }

        [Test]
        public void PerspectiveOrigin_percentage_pair_round_trips() {
            var cs = Compute("#x { perspective-origin: 30% 70%; }");
            Assert.That(cs.Get("perspective-origin"), Is.EqualTo("30% 70%"));
        }

        [Test]
        public void PerspectiveOrigin_does_not_inherit() {
            var child = ComputeChild("#p { perspective-origin: 30% 70%; }");
            Assert.That(child.Get("perspective-origin"), Is.EqualTo("50% 50%"));
        }

        // ─── transform-box ───────────────────────────────────────────────

        [Test]
        public void TransformBox_initial_is_view_box() {
            // §3.4: initial = `view-box` (SVG-context default).
            var cs = Compute("");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("view-box"));
        }

        [Test]
        public void TransformBox_fill_box_round_trips() {
            // §3.4: `fill-box` uses object bounding box (SVG only).
            var cs = Compute("#x { transform-box: fill-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("fill-box"));
        }

        [Test]
        public void TransformBox_stroke_box_round_trips() {
            var cs = Compute("#x { transform-box: stroke-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("stroke-box"));
        }

        [Test]
        public void TransformBox_content_box_round_trips() {
            // §3.4: `content-box` uses the element's CSS content edge.
            var cs = Compute("#x { transform-box: content-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("content-box"));
        }

        [Test]
        public void TransformBox_border_box_round_trips() {
            // §3.4: `border-box` uses the element's CSS border edge.
            var cs = Compute("#x { transform-box: border-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("border-box"));
        }

        [Test]
        public void TransformBox_does_not_inherit() {
            // §3.4: transform-box is NOT inherited per spec.
            var child = ComputeChild("#p { transform-box: border-box; }");
            Assert.That(child.Get("transform-box"), Is.EqualTo("view-box"));
        }

        [Test]
        public void TransformBox_important_wins_cascade() {
            var cs = Compute("#x { transform-box: border-box !important; transform-box: fill-box; }");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("border-box"));
        }

        // ─── Cross-property independence ──────────────────────────────────

        [Test]
        public void Transform_3d_longhands_are_independent() {
            // Setting one 3D longhand must not bleed into the others.
            // These cascade in distinct slots (CssProperties registers
            // each with its own property id).
            var cs = Compute("#x { perspective: 800px; }");
            Assert.That(cs.Get("perspective"), Is.EqualTo("800px"));
            Assert.That(cs.Get("perspective-origin"), Is.EqualTo("50% 50%"),
                "perspective-origin must remain at initial when only perspective is set");
            Assert.That(cs.Get("transform-style"), Is.EqualTo("flat"),
                "transform-style must remain at initial when only perspective is set");
            Assert.That(cs.Get("backface-visibility"), Is.EqualTo("visible"),
                "backface-visibility must remain at initial when only perspective is set");
            Assert.That(cs.Get("transform-box"), Is.EqualTo("view-box"),
                "transform-box must remain at initial when only perspective is set");
        }
    }
}
