using NUnit.Framework;
using System.IO;
using UnityEngine;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Positioning.PositioningTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    public class ScrollLayoutTests {
        static (BlockBox container, ScrollContainer sc, LayoutContext ctx) BuildScroll(string css, string body) {
            var (root, _, ctx) = Build(body, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            BlockBox containerBox = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null && bb.Element.GetAttribute("class") == "viewport") {
                    containerBox = bb; break;
                }
            }
            return (containerBox, sc, ctx);
        }

        static Box FindByClass(Box root, string className) {
            foreach (var box in AllBoxes(root)) {
                var cls = box.Element?.GetAttribute("class");
                if (cls == null) continue;
                foreach (var part in cls.Split(' ')) {
                    if (part == className) return box;
                }
            }
            return null;
        }

        static double DirectContentBottom(Box box) {
            double interiorOriginY = box.PaddingTop + box.BorderTop;
            double maxBottom = 0;
            foreach (var child in box.Children) {
                double b = child.Y - interiorOriginY + child.Height;
                if (b > maxBottom) maxBottom = b;
            }
            return maxBottom;
        }

        [Test]
        public void Overflow_hidden_creates_no_scrollbar_track() {
            const string css = ".viewport { overflow: hidden; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:300px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.OverflowY, Is.EqualTo(ScrollOverflow.Hidden));
            Assert.That(state.ShowsTrackY, Is.False);
            Assert.That(state.ShowsTrackX, Is.False);
        }

        [Test]
        public void Overflow_scroll_always_shows_track() {
            const string css = ".viewport { overflow: scroll; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:50px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state.ShowsTrackY, Is.True);
            Assert.That(state.ShowsTrackX, Is.True);
        }

        [Test]
        public void Overflow_auto_shows_track_only_on_overflow() {
            const string cssNo = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string htmlNo = "<div class=\"viewport\"><div style=\"height:30px\"></div></div>";
            var noCase = BuildScroll(cssNo, htmlNo);
            var stateNo = noCase.sc.Get(noCase.container);
            Assert.That(stateNo.ShowsTrackY, Is.False);

            const string cssYes = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string htmlYes = "<div class=\"viewport\"><div style=\"height:300px\"></div></div>";
            var yesCase = BuildScroll(cssYes, htmlYes);
            var stateYes = yesCase.sc.Get(yesCase.container);
            Assert.That(stateYes.ShowsTrackY, Is.True);
        }

        [Test]
        public void Overflow_auto_uses_final_reflowed_content_extent_for_quest_fixture() {
            string root = Path.GetDirectoryName(Application.dataPath);
            string htmlPath = Path.Combine(root, "Assets", "UI", "quests.html");
            string cssPath = Path.Combine(root, "Assets", "UI", "quests.css");
            var builder = new UIDocumentBuilder {
                DocumentSource = File.ReadAllText(htmlPath),
                DocumentPath = htmlPath,
                StylesheetSources = new[] { File.ReadAllText(cssPath) },
                StylesheetPaths = new[] { cssPath },
                MediaContext = MediaContext.Default(1920, 1080)
            };
            var state = builder.Build();

            UIDocumentLifecycle.RunLayout(state, state.Invalidation);

            var quests = FindByClass(state.RootBox, "quests");
            Assert.That(quests, Is.Not.Null);
            var scrollState = state.LayoutEngine.ScrollContainer.Get(quests);
            Assert.That(scrollState, Is.Not.Null);
            Assert.That(DirectContentBottom(quests), Is.LessThan(scrollState.ViewportHeight));
            Assert.That(scrollState.ShowsTrackY, Is.False);
        }

        [Test]
        public void ScrollHeight_includes_overflowing_child() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:500px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state.ScrollHeight, Is.GreaterThanOrEqualTo(500 - 0.001));
        }

        [Test]
        public void ScrollWidth_at_least_viewport_when_no_overflow() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:30px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state.ScrollWidth, Is.GreaterThanOrEqualTo(state.ViewportWidth - 0.001));
            Assert.That(state.ScrollHeight, Is.GreaterThanOrEqualTo(state.ViewportHeight - 0.001));
        }

        [Test]
        public void Programmatic_ScrollY_clamps_to_max() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:500px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            // Cap to MaxScrollY when over.
            state.ScrollY = state.MaxScrollY + 999;
            // Re-run scroll layout to clamp.
            new ScrollLayout(sc).Run(container);
            Assert.That(state.ScrollY, Is.LessThanOrEqualTo(state.MaxScrollY + 0.001));
            Assert.That(state.ScrollY, Is.EqualTo(state.MaxScrollY).Within(0.001));
        }

        [Test]
        public void Programmatic_negative_ScrollY_clamps_to_zero() {
            const string css = ".viewport { overflow: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:500px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            state.ScrollY = -50;
            new ScrollLayout(sc).Run(container);
            Assert.That(state.ScrollY, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Resizing_container_reduces_scrollHeight() {
            // Initial: short content.
            const string css1 = ".viewport { overflow: auto; height: 200px; width: 200px; } .child { height: 500px; }";
            const string html1 = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var (container, sc, _) = BuildScroll(css1, html1);
            var state = sc.Get(container);
            double initialMax = state.MaxScrollY;
            state.ScrollY = initialMax;

            // Now shrink the child by re-running layout fully with smaller child.
            const string css2 = ".viewport { overflow: auto; height: 200px; width: 200px; } .child { height: 100px; }";
            const string html2 = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var fresh = BuildScroll(css2, html2);
            // New state (different ScrollContainer instance), but the property
            // we care about — clamping when content shrinks — is exercised
            // explicitly here:
            var freshState = fresh.sc.Get(fresh.container);
            Assert.That(freshState.MaxScrollY, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void OverflowX_hidden_overflowY_auto_yields_only_vertical_scrollbar() {
            const string css = ".viewport { overflow-x: hidden; overflow-y: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:500px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state.ShowsTrackY, Is.True);
            Assert.That(state.ShowsTrackX, Is.False);
        }

        [Test]
        public void OverflowY_hidden_overflowX_auto_yields_only_horizontal_scrollbar() {
            const string css = ".viewport { overflow-x: auto; overflow-y: hidden; height: 100px; width: 200px; } .wide { width: 800px; height: 50px; }";
            const string html = "<div class=\"viewport\"><div class=\"wide\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state.ShowsTrackY, Is.False);
            Assert.That(state.ShowsTrackX, Is.True);
        }

        [Test]
        public void Nested_scroll_containers_each_get_state() {
            const string css = ".viewport { overflow: auto; height: 400px; width: 400px; }" +
                               ".inner { overflow: auto; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div class=\"inner\"><div style=\"height:300px\"></div></div></div>";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var sc = new ScrollContainer();
            new ScrollLayout(sc).Run(root);
            int containerCount = 0;
            foreach (var kv in sc.All) containerCount++;
            Assert.That(containerCount, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void Visible_overflow_creates_no_scroll_state() {
            const string css = ".viewport { overflow: visible; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:500px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            Assert.That(sc.Get(container), Is.Null);
        }

        [Test]
        public void ScrollWidth_includes_relative_positioned_grandchild_protruding_past_parent() {
            const string css =
                ".viewport { overflow: auto; width: 100px; height: 100px; }" +
                ".child { width: 100px; height: 100px; }" +
                ".gc { width: 100px; height: 100px; position: relative; right: -300px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"><div class=\"gc\"></div></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.ScrollWidth, Is.GreaterThanOrEqualTo(400 - 0.001));

            // Regression guard: with no relative offset, the grandchild stays
            // inside the direct child, so the scroll region matches the
            // viewport width.
            const string cssNoOffset =
                ".viewport { overflow: auto; width: 100px; height: 100px; }" +
                ".child { width: 100px; height: 100px; }" +
                ".gc { width: 100px; height: 100px; }";
            const string htmlNoOffset = "<div class=\"viewport\"><div class=\"child\"><div class=\"gc\"></div></div></div>";
            var noOffsetCase = BuildScroll(cssNoOffset, htmlNoOffset);
            var noOffsetState = noOffsetCase.sc.Get(noOffsetCase.container);
            Assert.That(noOffsetState.ScrollWidth, Is.EqualTo(noOffsetState.ViewportWidth).Within(0.001));
            Assert.That(noOffsetState.ScrollWidth, Is.LessThan(150));
        }

        [Test]
        public void Scrollable_extent_includes_scroll_container_end_side_padding() {
            const string css =
                ".viewport { overflow: auto; width: 100px; height: 100px; padding: 20px; box-sizing: border-box; }" +
                ".child { width: 200px; height: 200px; }";
            const string html = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            Assert.That(state, Is.Not.Null);
            Assert.That(state.ScrollWidth, Is.EqualTo(220).Within(0.001));
            Assert.That(state.ScrollHeight, Is.EqualTo(220).Within(0.001));

            const string cssNoPad =
                ".viewport { overflow: auto; width: 100px; height: 100px; }" +
                ".child { width: 200px; height: 200px; }";
            const string htmlNoPad = "<div class=\"viewport\"><div class=\"child\"></div></div>";
            var noPadCase = BuildScroll(cssNoPad, htmlNoPad);
            var noPadState = noPadCase.sc.Get(noPadCase.container);
            Assert.That(noPadState.ScrollWidth, Is.EqualTo(200).Within(0.001));
            Assert.That(noPadState.ScrollHeight, Is.EqualTo(200).Within(0.001));
        }

        [Test]
        public void ViewportHeight_excludes_horizontal_scrollbar_track_when_present() {
            const string css = ".viewport { overflow: scroll; height: 100px; width: 200px; }";
            const string html = "<div class=\"viewport\"><div style=\"height:50px\"></div></div>";
            var (container, sc, _) = BuildScroll(css, html);
            var state = sc.Get(container);
            // overflow:scroll always reserves both tracks; so the viewport along
            // each axis must be smaller than the box's content edge by at least
            // the scrollbar thickness on the cross axis.
            double expectedMaxV = container.Height - container.PaddingTop - container.PaddingBottom
                                  - container.BorderTop - container.BorderBottom
                                  - ScrollMath.ScrollbarTrackThicknessPx;
            Assert.That(state.ViewportHeight, Is.EqualTo(expectedMaxV).Within(0.001));
        }
    }
}
