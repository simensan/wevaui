using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    public class PositioningIntegrationTests {
        [Test]
        public void Modal_centered_with_fixed_top_left_50_percent() {
            // The transform: translate(-50%, -50%) part is a paint-time concern; layout
            // pins the modal's TOP-LEFT to the viewport center.
            const string css = @"
                .modal { position: fixed; top: 50%; left: 50%; width: 200px; height: 100px;
                          transform: translate(-50%, -50%); }
            ";
            var (root, _, _) = Build("<div class=\"modal\"></div>", css, viewportWidth: 800, viewportHeight: 600);
            var modal = FirstByClass(root, "modal");
            var (mx, my) = AbsoluteOriginOf(modal);
            Assert.That(mx, Is.EqualTo(400).Within(0.001));
            Assert.That(my, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Tooltip_absolutely_positioned_over_relative_button() {
            const string css = @"
                .button { position: relative; margin-top: 100px; margin-left: 50px;
                          width: 120px; height: 40px; }
                .tooltip { position: absolute; top: -30px; left: 0; width: 80px; height: 20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"button\"><div class=\"tooltip\"></div></div>",
                css, viewportWidth: 800);
            var tooltip = FirstByClass(root, "tooltip");
            var (tx, ty) = AbsoluteOriginOf(tooltip);
            Assert.That(tx, Is.EqualTo(50).Within(0.001));
            Assert.That(ty, Is.EqualTo(100 - 30).Within(0.001));
        }

        [Test]
        public void Three_overlapping_boxes_traverse_in_z_order() {
            const string html = @"<div id=""a""></div><div id=""b""></div><div id=""c""></div>";
            const string css = @"
                #a { position: absolute; top: 0; left: 0; width: 50px; height: 50px; z-index: 3; }
                #b { position: absolute; top: 0; left: 0; width: 50px; height: 50px; z-index: 1; }
                #c { position: absolute; top: 0; left: 0; width: 50px; height: 50px; z-index: 2; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var sc = new StackingContextBuilder().Build(root);
            var ordered = new List<Box>(PaintOrderTraversal.Enumerate(sc));
            int Index(string id) {
                for (int i = 0; i < ordered.Count; i++) {
                    if (ordered[i].Element != null && ordered[i].Element.Id == id) return i;
                }
                return -1;
            }
            Assert.That(Index("b"), Is.LessThan(Index("c")));
            Assert.That(Index("c"), Is.LessThan(Index("a")));
        }

        [Test]
        public void Realistic_ui_with_toolbar_main_and_floating_action_button() {
            const string css = @"
                .toolbar { position: relative; height: 50px; }
                .main { position: relative; height: 400px; }
                .fab { position: fixed; bottom: 20px; right: 20px; width: 56px; height: 56px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"toolbar\"></div><div class=\"main\"></div><div class=\"fab\"></div>",
                css, viewportWidth: 800, viewportHeight: 600);
            var toolbar = FirstByClass(root, "toolbar");
            var main = FirstByClass(root, "main");
            var fab = FirstByClass(root, "fab");

            Assert.That(toolbar.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(main.Y, Is.EqualTo(50).Within(0.001));
            var (fx, fy) = AbsoluteOriginOf(fab);
            // fab pinned to viewport bottom-right: x = 800 - 20 - 56, y = 600 - 20 - 56.
            Assert.That(fx, Is.EqualTo(800 - 20 - 56).Within(0.001));
            Assert.That(fy, Is.EqualTo(600 - 20 - 56).Within(0.001));
        }
    }
}
