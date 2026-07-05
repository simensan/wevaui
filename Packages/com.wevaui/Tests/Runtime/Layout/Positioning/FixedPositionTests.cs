using NUnit.Framework;
using System.IO;
using UnityEngine;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Layout;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Positioning {
    public class FixedPositionTests {
        [Test]
        public void Fixed_positions_against_viewport_ignoring_positioned_ancestor() {
            const string css = @"
                .ancestor { position: relative; margin-top: 100px; margin-left: 100px; width: 200px; height: 200px; }
                .fix { position: fixed; top: 0; left: 0; width: 30px; height: 30px; }
            ";
            var (root, _, _) = Build("<div class=\"ancestor\"><div class=\"fix\"></div></div>", css, viewportWidth: 800);
            var fix = FirstByClass(root, "fix");
            var (fx, fy) = AbsoluteOriginOf(fix);
            Assert.That(fx, Is.EqualTo(0).Within(0.001));
            Assert.That(fy, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Fixed_top_left_percentages_resolve_against_viewport() {
            const string css = ".fix { position: fixed; top: 50%; left: 25%; width: 10px; height: 10px; }";
            var (root, _, _) = Build("<div class=\"fix\"></div>", css, viewportWidth: 800, viewportHeight: 600);
            var fix = FirstByClass(root, "fix");
            var (fx, fy) = AbsoluteOriginOf(fix);
            Assert.That(fx, Is.EqualTo(200).Within(0.001));
            Assert.That(fy, Is.EqualTo(300).Within(0.001));
        }

        [Test]
        public void Fixed_box_does_not_shift_following_siblings() {
            const string css = @"
                #fix { position: fixed; top: 0; left: 0; height: 100px; width: 100px; }
                #after { height: 30px; }
            ";
            var (root, _, _) = Build("<div id=\"fix\"></div><div id=\"after\"></div>", css, viewportWidth: 800);
            var after = FirstById(root, "after");
            Assert.That(after.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Fixed_full_viewport_stretch_with_all_offsets_zero() {
            const string css = ".fix { position: fixed; top: 0; right: 0; bottom: 0; left: 0; }";
            var (root, _, _) = Build("<div class=\"fix\"></div>", css, viewportWidth: 1024, viewportHeight: 768);
            var fix = FirstByClass(root, "fix");
            Assert.That(fix.Width, Is.EqualTo(1024).Within(0.001));
            Assert.That(fix.Height, Is.EqualTo(768).Within(0.001));
        }

        [Test]
        public void Fixed_inset_flex_overlay_centers_child_in_viewport() {
            const string css = @"
                .overlay { position: fixed; inset: 0; display: flex; align-items: center; justify-content: center; }
                .card { width: 560px; height: 420px; }
            ";
            var (root, _, _) = Build(
                "<section class=\"overlay\"><article class=\"card\"></article></section>",
                css,
                viewportWidth: 1700,
                viewportHeight: 1079);
            var overlay = FirstByClass(root, "overlay");
            var card = FirstByClass(root, "card");
            var (cx, cy) = AbsoluteOriginOf(card);

            Assert.That(overlay.Width, Is.EqualTo(1700).Within(0.001));
            Assert.That(overlay.Height, Is.EqualTo(1079).Within(0.001));
            Assert.That(cx, Is.EqualTo((1700 - 560) * 0.5).Within(0.001));
            Assert.That(cy, Is.EqualTo((1079 - 420) * 0.5).Within(0.001));
        }

        [Test]
        public void Match3_endgame_fixed_overlay_centers_card_in_viewport() {
            string rootPath = Path.GetDirectoryName(Application.dataPath);
            string htmlPath = Path.Combine(rootPath, "Assets", "UI", "match3-endgame.html");
            string cssPath = Path.Combine(rootPath, "Assets", "UI", "match3-endgame.css");
            var builder = new UIDocumentBuilder {
                DocumentSource = File.ReadAllText(htmlPath),
                DocumentPath = htmlPath,
                StylesheetSources = new[] { File.ReadAllText(cssPath) },
                StylesheetPaths = new[] { cssPath },
                MediaContext = MediaContext.Default(1729, 1080)
            };
            var state = builder.Build();

            UIDocumentLifecycle.RunLayout(state, state.Invalidation);

            var overlay = FirstByClass(state.RootBox, "end-overlay");
            var card = FirstByClass(state.RootBox, "end-card");
            Assert.That(overlay, Is.Not.Null);
            Assert.That(card, Is.Not.Null);
            var (cardX, cardY) = AbsoluteOriginOf(card);
            double expectedX = (1729 - card.Width) * 0.5;
            double expectedY = 28 + (1080 - 56 - card.Height) * 0.5;

            Assert.That(overlay.Width, Is.EqualTo(1729).Within(0.001));
            Assert.That(overlay.Height, Is.EqualTo(1080).Within(0.001));
            Assert.That(cardX, Is.EqualTo(expectedX).Within(0.5));
            Assert.That(cardY, Is.EqualTo(expectedY).Within(0.5));
        }
    }
}
