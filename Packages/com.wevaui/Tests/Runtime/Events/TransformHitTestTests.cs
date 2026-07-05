using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Events {
    public class TransformHitTestTests {
        [Test]
        public void TranslateX_minus50pct_hit_test_matches_visual_position() {
            var css = @"
                html, body { width: 100%; height: 100%; margin: 0; }
                .wrapper { position: absolute; left: 50%; transform: translateX(-50%); width: 200px; height: 60px; background: red; }
            ";
            var html = @"<div class=""wrapper"" id=""w"">Hello</div>";
            var (root, styles, ctx) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var wrapperEl = FindByTag(root, "div")?.Element;
            Assert.That(wrapperEl, Is.Not.Null, "wrapper element must exist");

            var hitTester = new BoxTreeHitTester(root);

            // Layout position: left:50% of 800 = 400. Width=200.
            // Transform: translateX(-50%) = -100px.
            // Visual position: 400 - 100 = 300 to 500.

            var centerHit = hitTester.HitTest(400, 30);
            Assert.That(centerHit, Is.Not.Null, "center of visual box should hit");

            var leftHit = hitTester.HitTest(350, 30);
            Assert.That(leftHit, Is.Not.Null, "left side of visual box should hit");

            // Click at x=550 — outside visual box (300..500), should NOT hit the wrapper
            var outsideHit = hitTester.HitTest(550, 30);
            Assert.That(outsideHit?.GetAttribute("id"), Is.Not.EqualTo("w"),
                "outside the visual box should not hit the wrapper");
        }

        [Test]
        public void Nested_button_inside_translated_absolute_container() {
            var css = @"
                html, body { width: 100%; height: 100%; margin: 0; }
                .page { position: absolute; top: 0; left: 0; right: 0; bottom: 0; }
                .action { position: absolute; bottom: 32px; left: 50%; transform: translateX(-50%); z-index: 5; }
                .btn { display: flex; align-items: center; justify-content: center; min-width: 260px; height: 76px; background: green; border: none; }
                .btn-label { pointer-events: none; font-size: 28px; }
            ";
            var html = @"
                <div class=""page"">
                    <div class=""action"">
                        <button class=""btn"" id=""play"">
                            <span class=""btn-label"">PLAY</span>
                        </button>
                    </div>
                </div>
            ";
            var (root, styles, ctx) = Build(html, css, viewportWidth: 800, viewportHeight: 600);

            var hitTester = new BoxTreeHitTester(root);

            var centerHit = hitTester.HitTest(400, 560);
            Assert.That(centerHit, Is.Not.Null, "center of play button should hit something");
        }

        static Box FindByTag(Box root, string tag, string cls) {
            if (root.Element?.TagName == tag && root.Element?.ClassName?.Contains(cls) == true) return root;
            foreach (var c in root.Children) {
                var f = FindByTag(c, tag, cls);
                if (f != null) return f;
            }
            return null;
        }

        static Box FindByTag(Box root, string tag) {
            if (root.Element?.TagName == tag) return root;
            foreach (var c in root.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }
    }
}
