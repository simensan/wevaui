using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;

namespace Weva.Tests.Layout.Scrolling {
    public class ScrollbarPaintTests {
        sealed class FakeStateProvider : IElementStateProvider {
            public ElementState State;
            public ElementState GetState(Element e) => State;
        }

        static (BlockBox box, ScrollState state) MakeScrollable(double containerH, double contentH, ScrollOverflow overflow) {
            var bb = new BlockBox();
            bb.X = 0; bb.Y = 0;
            bb.Width = 200;
            bb.Height = containerH;
            var state = new ScrollState {
                ViewportHeight = containerH,
                ViewportWidth = bb.Width - ScrollMath.ScrollbarTrackThicknessPx,
                ScrollHeight = contentH,
                ScrollWidth = bb.Width - ScrollMath.ScrollbarTrackThicknessPx,
                OverflowX = ScrollOverflow.Hidden,
                OverflowY = overflow
            };
            return (bb, state);
        }

        [Test]
        public void Track_and_thumb_emitted_when_overflow_scroll() {
            var (bb, state) = MakeScrollable(100, 500, ScrollOverflow.Scroll);
            var list = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list, null);
            int fillCount = 0;
            foreach (var c in list.Commands) if (c is FillRectCommand) fillCount++;
            Assert.That(fillCount, Is.EqualTo(2));
        }

        [Test]
        public void Thumb_size_proportional_to_viewport_content_ratio() {
            var (bb, state) = MakeScrollable(100, 500, ScrollOverflow.Scroll);
            var list = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list, null);
            FillRectCommand thumb = null;
            int fills = 0;
            foreach (var c in list.Commands) {
                if (c is FillRectCommand f) { fills++; if (fills == 2) { thumb = f; break; } }
            }
            Assert.That(thumb, Is.Not.Null);
            // viewport/content ratio = 100/500 = 0.2; track height = 100; thumb = 20
            Assert.That(thumb.Bounds.Height, Is.EqualTo(20).Within(0.5));
        }

        [Test]
        public void Thumb_position_proportional_to_scroll() {
            var (bb, state) = MakeScrollable(100, 500, ScrollOverflow.Scroll);
            state.ScrollY = state.MaxScrollY; // bottom
            var list = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list, null);
            FillRectCommand thumb = null;
            int fills = 0;
            foreach (var c in list.Commands) {
                if (c is FillRectCommand f) { fills++; if (fills == 2) { thumb = f; break; } }
            }
            Assert.That(thumb, Is.Not.Null);
            // thumb bottom edge should match track bottom (Y + track height).
            double trackBottom = bb.Y + bb.Height;
            Assert.That(thumb.Bounds.Bottom, Is.EqualTo(trackBottom).Within(0.5));
        }

        [Test]
        public void Hover_state_changes_thumb_color() {
            var (bb, state) = MakeScrollable(100, 500, ScrollOverflow.Scroll);
            bb.Element = new Element("div");
            var sp = new FakeStateProvider { State = ElementState.None };
            var list1 = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list1, sp);
            FillRectCommand normalThumb = null;
            int n1 = 0;
            foreach (var c in list1.Commands) if (c is FillRectCommand f) { n1++; if (n1 == 2) { normalThumb = f; break; } }

            sp.State = ElementState.Hover;
            var list2 = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list2, sp);
            FillRectCommand hoverThumb = null;
            int n2 = 0;
            foreach (var c in list2.Commands) if (c is FillRectCommand f) { n2++; if (n2 == 2) { hoverThumb = f; break; } }

            Assert.That(normalThumb.Brush.Color, Is.Not.EqualTo(hoverThumb.Brush.Color));
        }

        [Test]
        public void No_track_emitted_when_overflow_hidden() {
            var (bb, state) = MakeScrollable(100, 500, ScrollOverflow.Hidden);
            var list = new PaintList();
            ScrollbarPaint.Emit(bb, state, 0, 0, list, null);
            Assert.That(list.Commands.Count, Is.EqualTo(0));
        }
    }
}
