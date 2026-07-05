using System.Collections.Generic;
using System.IO;
using System.Text;
using Weva.Css;
using Weva.Css.Animation;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Media;
using Weva.Binding;
using Weva.Documents;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Reactive;
using static Weva.Tests.Layout.Incremental.IncrementalLayoutTestHelpers;

namespace Weva.Tests.Layout.Incremental {
    // End-to-end behaviour tests for the cascade -> tracker -> layout
    // pipeline when a :hover rule changes a layout-affecting property.
    // The cascade-side LayoutAffectingPropertyChanged drives Layout dirty
    // narrowly; the layout engine routes through TryLayoutSubtree.
    public class HoverWithLayoutPropTests {
        sealed class PerfController {
            [UIBind] public string FrameMs = "1";
            [UIBind] public string Fps = "1";
        }

        sealed class HoverState : IElementStateProvider {
            Element hovered;
            long version;
            public Element Hovered => hovered;
            public void SetHover(Element e) {
                if (!ReferenceEquals(hovered, e)) { hovered = e; version++; }
            }
            public ElementState GetState(Element e) =>
                ReferenceEquals(e, hovered) ? ElementState.Hover : ElementState.None;
            public long Version => version;
        }

        [Test]
        public void Bound_text_change_marks_flex_ancestors_layout_dirty() {
            var controller = new PerfController();
            var state = new UIDocumentBuilder {
                DocumentSource =
                    "<div id=\"head\" class=\"head\"><div class=\"title\">A</div>" +
                    "<div id=\"perf\" class=\"perf\"><span>{{ FrameMs }}</span><span>{{ Fps }}</span></div>" +
                    "<div id=\"filters\" class=\"filters\">B</div></div>",
                StylesheetSources = new List<string> {
                    ".head{display:flex;width:120px;gap:10px}.title{width:20px}" +
                    ".perf{display:flex;gap:5px;margin-left:auto}.filters{width:30px;flex-shrink:0}" +
                    "span{font-size:10px}"
                },
                MediaContext = MediaContext.Default(320, 360),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            UIDocumentLifecycle.Update(state, controller, 0);
            controller.FrameMs = "123456789";
            controller.Fps = "987654321";
            state.Bindings.Update(controller, state.Invalidation, state.StyleOf);

            Assert.That(state.Invalidation.IsDirty(state.Doc.GetElementById("head"), InvalidationKind.Layout), Is.True,
                "Bound text can change intrinsic flex item width, so layout dirtiness must bubble to the owning flex row.");
        }

        [Test]
        public void Bound_text_update_keeps_absolute_grid_scroll_row_allocation() {
            var controller = new PerfController();
            const string html =
                "<main id=\"log\" class=\"log\"><header id=\"head\" class=\"head\">" +
                "<div class=\"title\">Quest Log</div><div class=\"perf\"><span>{{ FrameMs }}</span><span>{{ Fps }}</span></div>" +
                "<div class=\"filters\">Active</div></header><section id=\"quests\" class=\"quests\">" +
                "<article class=\"quest\">one</article><article class=\"quest\">two</article><article class=\"quest\">three</article>" +
                "</section><footer>footer</footer></main>";
            const string css =
                "html,body{width:100%;height:100%;margin:0;padding:0}.log{position:absolute;left:5vw;top:5vh;width:90vw;height:90vh;display:grid;grid-template-rows:auto 1fr auto;gap:16px;padding:24px;box-sizing:border-box}" +
                ".head{display:flex;gap:24px;padding:16px 20px}.title{font-size:22px}.perf{display:flex;gap:8px;margin-left:auto}.filters{width:80px;flex-shrink:0}" +
                ".quests{display:flex;flex-direction:column;gap:12px;overflow-y:auto;min-height:0}.quest{height:180px;flex-shrink:0}";

            var state = new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(320, 360),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            UIDocumentLifecycle.Update(state, controller, 0);
            var quests = state.Doc.GetElementById("quests");
            double initialHeight = FindBoxFor(state.RootBox, quests).Height;

            controller.FrameMs = "31.0";
            controller.Fps = "331";
            UIDocumentLifecycle.Update(state, controller, 0.1);
            double updatedHeight = FindBoxFor(state.RootBox, quests).Height;

            Assert.That(updatedHeight, Is.EqualTo(initialHeight).Within(0.001),
                "A bound header text update must not let the absolute grid container's 1fr scroll row expand to content height.");
        }

        [Test]
        public void Bound_text_update_keeps_header_flex_items_sequential_when_overflowing() {
            var controller = new PerfController();
            const string html =
                "<header id=\"head\" class=\"head\"><div id=\"left\" class=\"head-left\">Quest Log</div>" +
                "<div id=\"perf\" class=\"perf-readout\"><span>{{ FrameMs }}</span><span>{{ Fps }}</span></div>" +
                "<nav id=\"filters\" class=\"filters\"><button>Active</button><button>All</button></nav></header>";
            const string css =
                ".head{display:flex;align-items:center;justify-content:space-between;gap:24px;width:238px;padding:16px 20px;box-sizing:border-box}" +
                ".head-left{width:84px;flex-shrink:0}.perf-readout{display:flex;gap:8px;margin-left:auto}" +
                ".perf-readout span{display:inline-flex;min-width:72px;padding:6px 9px;box-sizing:border-box}" +
                ".filters{display:flex;gap:4px;padding:4px}.filters button{padding:6px 14px}";

            var state = new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(320, 360),
                FontMetricsOverride = new MonoFontMetrics(),
                Controller = controller
            }.Build();

            UIDocumentLifecycle.Update(state, controller, 0);
            controller.FrameMs = "31.0";
            controller.Fps = "331";
            UIDocumentLifecycle.Update(state, controller, 0.1);

            var perf = FindBoxFor(state.RootBox, state.Doc.GetElementById("perf"));
            var filters = FindBoxFor(state.RootBox, state.Doc.GetElementById("filters"));
            Assert.That(filters.X, Is.GreaterThanOrEqualTo(perf.X + perf.Width + 23.5),
                "Flex siblings may overflow a narrow row, but the later filter group must not overlap the perf readout.");
        }

        [Test]
        public void Quest_fixture_bound_perf_readout_does_not_overlap_filters_at_narrow_viewport() {
            var controller = new PerfController { FrameMs = "1.0", Fps = "60" };
            // Use the same root resolution as Application.dataPath: two levels up from
            // Tools/TestVerifyAll, or fall back to the current directory if that's missing.
            string cwd = Directory.GetCurrentDirectory();
            string repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", ".."));
            string root = Directory.Exists(Path.Combine(repoRoot, "Assets")) ? repoRoot : cwd;
            string htmlPath = Path.Combine(root, "Assets", "UI", "quests.html");
            string cssPath = Path.Combine(root, "Assets", "UI", "quests.css");
            Assert.That(File.Exists(htmlPath), Is.True, htmlPath);
            Assert.That(File.Exists(cssPath), Is.True, cssPath);

            foreach (int width in new[] { 320, 278 }) {
                controller.FrameMs = "1.0";
                controller.Fps = "60";
                var state = new UIDocumentBuilder {
                    DocumentSource = File.ReadAllText(htmlPath),
                    DocumentPath = htmlPath,
                    StylesheetSources = new List<string> { File.ReadAllText(cssPath) },
                    StylesheetPaths = new List<string> { cssPath },
                    MediaContext = MediaContext.Default(width, 360),
                    FontMetricsOverride = new MonoFontMetrics(),
                    Controller = controller
                }.Build();

                UIDocumentLifecycle.Update(state, controller, 0);
                controller.FrameMs = "5.0";
                controller.Fps = "199";
                UIDocumentLifecycle.Update(state, controller, 0.1);
                var perf = FindFirstElementWithClass(state.Doc, "perf-readout");
                var filters = FindFirstElementWithClass(state.Doc, "filters");
                var perfBox = FindBoxFor(state.RootBox, perf);
                var filtersBox = FindBoxFor(state.RootBox, filters);
                double perfVisualRight = MaxAbsoluteRight(perfBox);

                Assert.That(filtersBox.X, Is.GreaterThanOrEqualTo(perfBox.X + perfBox.Width + 23.5),
                    $"viewport={width}; perf x={perfBox.X} w={perfBox.Width}; filters x={filtersBox.X} w={filtersBox.Width}");
                Assert.That(perfVisualRight, Is.LessThanOrEqualTo(AbsoluteX(perfBox) + perfBox.Width + 0.5),
                    $"viewport={width}; perf visual right={perfVisualRight}; perf box right={AbsoluteX(perfBox) + perfBox.Width}; filters x={AbsoluteX(filtersBox)}\n{DescribeSubtree(perfBox)}");
                Assert.That(AbsoluteX(filtersBox), Is.GreaterThanOrEqualTo(perfVisualRight + 23.5),
                    $"viewport={width}; perf visual right={perfVisualRight}; filters x={AbsoluteX(filtersBox)}");
            }
        }

        [Test]
        public void Hover_changes_color_marks_no_layout_dirty() {
            // :hover { color: red } is paint-only; the cascade must NOT add
            // any element to its layout-dirty set.
            var h = Build(
                "<div id=\"r\"><span id=\"a\">x</span></div>",
                "span:hover { color: red; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state); // prime cache

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            Assert.That(h.Cascade.LayoutDirtyElements.Count, Is.EqualTo(0));
        }

        [Test]
        public void Hover_changes_border_width_marks_layout_dirty() {
            // :hover { border-width: 4px; border-style: solid } changes a
            // layout-affecting property — cascade must surface that element
            // in LayoutDirtyElements.
            var h = Build(
                "<div id=\"r\"><span id=\"a\">x</span></div>",
                "span:hover { border-width: 4px; border-style: solid; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state);

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            Assert.That(h.Cascade.LayoutDirtyElements, Does.Contain(h.Doc.GetElementById("a")));
        }

        [Test]
        public void Hover_changes_padding_marks_layout_dirty() {
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                "div:hover { padding: 8px; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state);

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            Assert.That(h.Cascade.LayoutDirtyElements, Does.Contain(h.Doc.GetElementById("a")));
        }

        [Test]
        public void Hover_changes_box_shadow_marks_no_layout_dirty() {
            // box-shadow does not participate in box sizing per CSS Backgrounds
            // and Borders L3 §7. Cascade must NOT mark layout dirty.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                "div:hover { box-shadow: 0 0 4px black; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state);

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            Assert.That(h.Cascade.LayoutDirtyElements.Count, Is.EqualTo(0));
        }

        [Test]
        public void Hover_changes_outline_marks_no_layout_dirty() {
            // Per CSS UI L4 §4, outline is drawn outside the border edge and
            // does not participate in layout. Cascade must NOT mark layout
            // dirty even when outline-width changes.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                "div:hover { outline: 2px solid red; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state);

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            Assert.That(h.Cascade.LayoutDirtyElements.Count, Is.EqualTo(0));
        }

        [Test]
        public void ApplyLayoutInvalidation_drains_set_to_tracker() {
            // After ComputeAll surfaces dirty elements, ApplyLayoutInvalidation
            // marks them on the tracker AND clears the cascade-side set.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                "div:hover { padding: 8px; }");
            var state = new HoverState();
            h.Cascade.ComputeAll(h.Doc, state);

            state.SetHover(h.Doc.GetElementById("a"));
            h.Cascade.ComputeAll(h.Doc, state);
            var tracker = new InvalidationTracker();
            int marked = h.Cascade.ApplyLayoutInvalidation(tracker);
            Assert.That(marked, Is.EqualTo(1));
            Assert.That(tracker.IsDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout), Is.True);
            // Drained — second call returns 0.
            Assert.That(h.Cascade.ApplyLayoutInvalidation(tracker), Is.EqualTo(0));
        }

        [Test]
        public void Hover_with_layout_prop_that_changes_outer_size_falls_back_to_full_layout() {
            // End-to-end: cascade marks layout dirty on the hovered element,
            // tracker carries the flag. Because the padding changes the
            // element's outer height, sibling positions need the full block
            // formatting context to rerun.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div><div id=\"b\"></div></div>",
                "div:hover { padding: 8px; }");
            var state = new HoverState();
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }
            Restyle(state);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            h.Engine.ResetCacheStats();

            state.SetHover(h.Doc.GetElementById("a"));
            Restyle(state);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(tracker);
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
        }

        [Test]
        public void Grid_stretched_ancestor_does_not_hide_descendant_height_animation() {
            // Mirrors quests.css: an animated objective inside a flex column
            // nested in a grid-stretched body. The stretched body has a stable
            // external allocation, but its natural height still contributes to
            // the grid row. If the local path preserves the old stretched
            // height, the quest card and following content never move.
            var h = Build(
                "<article id=\"card\"><div id=\"body\"><ul id=\"list\"><li id=\"item\">Objective</li></ul></div><aside id=\"side\">Reward</aside></article><div id=\"after\"></div>",
                "article { display: grid; grid-template-columns: 1fr 100px; gap: 8px; } " +
                "#body { display: flex; flex-direction: column; } " +
                "#list { display: flex; flex-direction: column; margin: 0; padding: 0; } " +
                "#item { padding-top: 0px; padding-bottom: 0px; } " +
                "#item:hover { padding-top: 20px; padding-bottom: 20px; } " +
                "#after { height: 10px; }");
            var state = new HoverState();
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }

            Restyle(state);
            var r0 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var card0 = FindBoxFor(r0, h.Doc.GetElementById("card"));
            var after0 = FindBoxFor(r0, h.Doc.GetElementById("after"));
            h.Engine.ResetCacheStats();

            state.SetHover(h.Doc.GetElementById("item"));
            Restyle(state);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("item"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(tracker);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var card1 = FindBoxFor(r1, h.Doc.GetElementById("card"));
            var after1 = FindBoxFor(r1, h.Doc.GetElementById("after"));

            Assert.That(h.Engine.SubtreeSkipHits, Is.EqualTo(0));
            Assert.That(card1.Height, Is.GreaterThan(card0.Height + 20));
            Assert.That(after1.Y, Is.GreaterThan(after0.Y + 20));
        }

        [Test]
        public void Flex_item_layout_change_reflows_parent_so_following_item_moves() {
            // A flex/grid item's layout-affecting change must be owned by the
            // formatting parent. Relaying out only the item updates its own
            // box but leaves sibling placement stale.
            var h = Build(
                "<div id=\"row\"><div id=\"a\">A</div><div id=\"b\">B</div></div>",
                "#row { display: flex; gap: 4px; } " +
                "#a { padding-left: 0px; padding-right: 0px; } " +
                "#a:hover { padding-left: 20px; padding-right: 20px; }");
            var state = new HoverState();
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }

            Restyle(state);
            var r0 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b0 = FindBoxFor(r0, h.Doc.GetElementById("b"));
            h.Engine.ResetCacheStats();

            state.SetHover(h.Doc.GetElementById("a"));
            Restyle(state);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(tracker);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));

            Assert.That(b1.X, Is.GreaterThan(b0.X + 30));
        }

        [Test]
        public void Flex_column_item_layout_change_reflows_parent_so_following_item_moves() {
            var h = Build(
                "<ul id=\"list\"><li id=\"a\">A</li><li id=\"b\">B</li></ul>",
                "#list { display: flex; flex-direction: column; gap: 4px; margin: 0; padding: 0; } " +
                "#a { padding-top: 0px; padding-bottom: 0px; } " +
                "#a:hover { padding-top: 20px; padding-bottom: 20px; }");
            var state = new HoverState();
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }

            Restyle(state);
            var r0 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var b0 = FindBoxFor(r0, h.Doc.GetElementById("b"));
            h.Engine.ResetCacheStats();

            state.SetHover(h.Doc.GetElementById("a"));
            Restyle(state);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(tracker);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var b1 = FindBoxFor(r1, h.Doc.GetElementById("b"));

            Assert.That(b1.Y, Is.GreaterThan(b0.Y + 30));
        }

        [Test]
        public void Nested_flex_item_growth_repositions_following_quest_card() {
            // Quest scene shape: .quests is a column flex container, each
            // .quest is a grid item, and the animated objective is nested
            // under another column flex. When the first card grows, the next
            // card must be laid out at its new Y instead of resurrecting the
            // previous-frame cached card box.
            var h = Build(
                "<section id=\"quests\"><article id=\"q1\"><div id=\"body\"><ul id=\"list\"><li id=\"a\">A</li><li id=\"afterObj\">B</li></ul></div><aside>R</aside></article><article id=\"q2\">Second quest</article></section>",
                "#quests { display: flex; flex-direction: column; gap: 12px; } " +
                "article { display: grid; grid-template-columns: 1fr 100px; padding: 8px; } " +
                "#body, #list { display: flex; flex-direction: column; } " +
                "#list { margin: 0; padding: 0; gap: 4px; } " +
                "#a { padding-top: 0px; padding-bottom: 0px; } " +
                "#a:hover { padding-top: 20px; padding-bottom: 20px; }");
            var state = new HoverState();
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }

            Restyle(state);
            var r0 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var q1_0 = FindBoxFor(r0, h.Doc.GetElementById("q1"));
            var q2_0 = FindBoxFor(r0, h.Doc.GetElementById("q2"));
            h.Engine.ResetCacheStats();

            state.SetHover(h.Doc.GetElementById("a"));
            Restyle(state);
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(tracker);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, tracker);
            var q1_1 = FindBoxFor(r1, h.Doc.GetElementById("q1"));
            var q2_1 = FindBoxFor(r1, h.Doc.GetElementById("q2"));

            Assert.That(q1_1.Height, Is.GreaterThan(q1_0.Height + 30));
            Assert.That(q2_1.Y, Is.GreaterThan(q2_0.Y + 30));
        }

        [Test]
        public void Keyframe_layout_animation_repositions_following_quest_card() {
            const string css =
                "@keyframes growObj { " +
                "from { margin-left: 0px; padding-top: 0px; padding-bottom: 0px; padding-left: 22px; } " +
                "to { margin-left: 24px; padding-top: 8px; padding-bottom: 8px; padding-left: 44px; } " +
                "} " +
                "#quests { display: flex; flex-direction: column; gap: 12px; } " +
                "article { display: grid; grid-template-columns: 1fr 100px; padding: 8px; } " +
                "#body, #list { display: flex; flex-direction: column; } " +
                "#list { margin: 0; padding: 0; gap: 4px; } " +
                "li { position: relative; padding-left: 22px; } " +
                "li::before { content: \"*\"; position: absolute; left: 0; top: 0; } " +
                "#a { animation-name: growObj; animation-duration: 1.2s; animation-timing-function: ease-in-out; " +
                "animation-iteration-count: infinite; animation-direction: alternate; }";
            var h = Build(
                "<section id=\"quests\"><article id=\"q1\"><div id=\"body\"><ul id=\"list\"><li id=\"a\">A</li><li>B</li></ul></div><aside>R</aside></article><article id=\"q2\">Second quest</article></section>",
                css);
            h.Engine.BeforeStyleOf = e => h.Cascade.ComputeBefore(e, null);
            var clock = new FakeUIClock();
            var sheet = CssParser.Parse(css);
            var runner = new CssAnimationRunner(h.Cascade, new[] { sheet }, clock);
            h.Cascade.AttachAnimationRunner(runner);
            ComputedStyle StyleOf(Element e) => h.Cascade.GetComposedStyle(e);

            void Restyle() {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc)) h.Styles[kv.Key] = kv.Value;
            }

            h.Cascade.InvalidateAll();
            Restyle();
            var r0 = h.Engine.Layout(h.Doc, StyleOf, h.Ctx);
            var q1_0 = FindBoxFor(r0, h.Doc.GetElementById("q1"));
            var q2_0 = FindBoxFor(r0, h.Doc.GetElementById("q2"));
            var a0 = FindBoxFor(r0, h.Doc.GetElementById("a"));
            h.Engine.ResetCacheStats();

            var tracker = new InvalidationTracker();
            clock.Set(0.9);
            runner.Tick(0.9, tracker);
            h.Engine.Apply(tracker);
            var r1 = h.Engine.Layout(h.Doc, StyleOf, h.Ctx, tracker);
            var q1_1 = FindBoxFor(r1, h.Doc.GetElementById("q1"));
            var q2_1 = FindBoxFor(r1, h.Doc.GetElementById("q2"));
            var a1 = FindBoxFor(r1, h.Doc.GetElementById("a"));
            double q1Delta = q1_1.Height - q1_0.Height;
            double q2Delta = q2_1.Y - q2_0.Y;
            double activeDelta = a1.Height - a0.Height;

            Assert.That(tracker.IsDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout), Is.True);
            Assert.That(a1.Height, Is.GreaterThan(a0.Height + 10));
            Assert.That(q1Delta, Is.EqualTo(activeDelta).Within(0.75));
            Assert.That(q2Delta, Is.EqualTo(q1Delta).Within(0.75),
                $"q1 height {q1_0.Height}->{q1_1.Height}, q2 y {q2_0.Y}->{q2_1.Y}, subtree skips {h.Engine.SubtreeSkipHits}");

            var shrinkTracker = new InvalidationTracker();
            clock.Set(2.1);
            runner.Tick(2.1, shrinkTracker);
            h.Engine.Apply(shrinkTracker);
            var r2 = h.Engine.Layout(h.Doc, StyleOf, h.Ctx, shrinkTracker);
            var q1_2 = FindBoxFor(r2, h.Doc.GetElementById("q1"));
            var q2_2 = FindBoxFor(r2, h.Doc.GetElementById("q2"));
            var a2 = FindBoxFor(r2, h.Doc.GetElementById("a"));
            double shrinkQ1Delta = q1_1.Height - q1_2.Height;
            double shrinkQ2Delta = q2_1.Y - q2_2.Y;
            double shrinkActiveDelta = a1.Height - a2.Height;

            Assert.That(shrinkTracker.IsDirty(h.Doc.GetElementById("a"), InvalidationKind.Layout), Is.True);
            Assert.That(a2.Height, Is.LessThan(a1.Height - 6));
            Assert.That(shrinkQ1Delta, Is.EqualTo(shrinkActiveDelta).Within(0.75));
            Assert.That(shrinkQ2Delta, Is.EqualTo(shrinkQ1Delta).Within(0.75),
                $"q1 height {q1_1.Height}->{q1_2.Height}, q2 y {q2_1.Y}->{q2_2.Y}, subtree skips {h.Engine.SubtreeSkipHits}");
        }

        [Test]
        public void Lifecycle_quest_animation_shrinks_after_alternate_peak() {
            const string html =
                "<main class=\"log\"><section class=\"quests\">" +
                "<article class=\"quest quest-main\"><div class=\"quest-marker\">*</div><div class=\"quest-body\">" +
                "<header class=\"quest-head\"><h3>Whispers in the Vault</h3><span>Story</span></header>" +
                "<p>Track the missing watchmen of the Brass Gate before the seneschal can bury the story.</p>" +
                "<ul class=\"quest-objectives\"><li class=\"obj obj-done\">Speak with Captain Ren</li>" +
                "<li class=\"obj obj-done\">Retrieve the soulglass lantern</li>" +
                "<li class=\"obj obj-active\">Investigate the lower archive</li>" +
                "<li class=\"obj\">Find the missing watchmen</li><li class=\"obj obj-locked\">Confront the seneschal</li></ul>" +
                "</div><div class=\"quest-side\"><span>450 XP</span></div></article>" +
                "<article class=\"quest quest-side-q\"><div class=\"quest-marker\">*</div><div class=\"quest-body\"><h3>A Bottle of Crowsblood</h3><p>The brewer needs a sealed bottle.</p></div></article>" +
                "</section></main>";
            const string css =
                "html, body { width: 100%; height: 100%; margin: 0; padding: 0; } " +
                ".log { position: absolute; left: 5vw; top: 5vh; width: 90vw; height: 90vh; display: grid; grid-template-rows: auto 1fr auto; gap: 16px; padding: 24px; box-sizing: border-box; } " +
                ".quests { display: flex; flex-direction: column; gap: 12px; overflow-y: auto; min-height: 0; } " +
                ".quest { display: grid; grid-template-columns: 56px 1fr 140px; gap: 16px; padding: 16px 20px; border: 1px solid #777; border-radius: 10px; } " +
                ".quest-marker { width: 56px; height: 56px; display: flex; align-items: center; justify-content: center; } " +
                ".quest-body { display: flex; flex-direction: column; gap: 6px; min-width: 0; } " +
                ".quest-head { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; } " +
                "h3, p { margin: 0; } " +
                ".quest-objectives { list-style: none; margin: 8px 0 0 0; padding: 0; display: flex; flex-direction: column; gap: 4px; } " +
                ".obj { position: relative; padding-left: 22px; font-size: 12px; } " +
                ".obj::before { content: \"*\"; position: absolute; left: 0; top: 0; } " +
                ".quest-main .obj-active { animation-name: localized-layout-probe; animation-duration: 1.2s; animation-timing-function: ease-in-out; animation-iteration-count: infinite; animation-direction: alternate; } " +
                "@keyframes localized-layout-probe { from { margin-left: 0px; padding-top: 0px; padding-bottom: 0px; padding-left: 22px; } to { margin-left: 24px; padding-top: 8px; padding-bottom: 8px; padding-left: 44px; } }";

            var clock = new FakeUIClock();
            var state = new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = new List<string> { css },
                MediaContext = MediaContext.Default(1434, 781),
                Clock = clock,
                FontMetricsOverride = new MonoFontMetrics()
            }.Build();

            UIDocumentLifecycle.Update(state, null, 0);
            Assert.That(state.Animator.RunningAnimationCount, Is.GreaterThan(0));
            var active = FindFirstElementWithClass(state.Doc, "obj-active");
            var q1 = FindFirstElementWithClass(state.Doc, "quest-main");
            var q2 = FindFirstElementWithClass(state.Doc, "quest-side-q");

            state.Cascade.ResetCacheStats();
            var probeTracker = new InvalidationTracker();
            clock.Set(0.3);
            state.Animator.Tick(0.3, probeTracker);
            Assert.That(probeTracker.IsDirty(active, InvalidationKind.Layout), Is.True);
            Assert.That(state.Cascade.CacheMisses, Is.EqualTo(0),
                "Layout-animation dirty propagation must use cached styles, not re-enter cascade.");

            clock.Set(0.3);
            UIDocumentLifecycle.Update(state, null, 0.3);
            double lowActivePadding = FindBoxFor(state.RootBox, active).PaddingTop;
            double lowQ1Height = FindBoxFor(state.RootBox, q1).Height;
            double lowQ2Y = FindBoxFor(state.RootBox, q2).Y;

            clock.Set(0.9);
            UIDocumentLifecycle.Update(state, null, 0.9);
            double peakActivePadding = FindBoxFor(state.RootBox, active).PaddingTop;
            double peakQ1Height = FindBoxFor(state.RootBox, q1).Height;
            double peakQ2Y = FindBoxFor(state.RootBox, q2).Y;

            clock.Set(2.1);
            UIDocumentLifecycle.Update(state, null, 2.1);
            double shrinkActivePadding = FindBoxFor(state.RootBox, active).PaddingTop;
            double shrinkQ1Height = FindBoxFor(state.RootBox, q1).Height;
            double shrinkQ2Y = FindBoxFor(state.RootBox, q2).Y;

            Assert.That(peakActivePadding, Is.GreaterThan(lowActivePadding + 5));
            Assert.That(peakQ1Height, Is.GreaterThan(lowQ1Height + 10));
            Assert.That(peakQ2Y, Is.GreaterThan(lowQ2Y + 10));
            Assert.That(shrinkActivePadding, Is.EqualTo(lowActivePadding).Within(0.75));
            Assert.That(shrinkQ1Height, Is.EqualTo(lowQ1Height).Within(0.75));
            Assert.That(shrinkQ2Y, Is.EqualTo(lowQ2Y).Within(0.75));
        }

        [Test]
        public void Reverting_hover_undoes_layout_change() {
            // Toggling hover off must cascade-mark layout dirty (since the
            // padding regresses to its base value), and the resulting
            // re-layout must shrink the box back. Note: h.Recompute()
            // forwards through the SAME cascade engine, so we need to
            // recompute via that cascade engine WITH the state provider
            // each time.
            var h = Build(
                "<div id=\"r\"><div id=\"a\"></div></div>",
                "div { padding: 0; } div:hover { padding: 8px; }");
            var state = new HoverState();
            // Prime: no hover.
            void Restyle(IElementStateProvider sp) {
                h.Styles.Clear();
                foreach (var kv in h.Cascade.ComputeAll(h.Doc, sp)) h.Styles[kv.Key] = kv.Value;
            }
            Restyle(state);
            var r0 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a0 = FindBoxFor(r0, h.Doc.GetElementById("a"));
            double basePad = a0.PaddingTop;

            // Hover.
            state.SetHover(h.Doc.GetElementById("a"));
            Restyle(state);
            var t1 = new InvalidationTracker();
            t1.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(t1);
            var r1 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, t1);
            var a1 = FindBoxFor(r1, h.Doc.GetElementById("a"));
            Assert.That(a1.PaddingTop, Is.GreaterThan(basePad));

            // Un-hover.
            state.SetHover(null);
            Restyle(state);
            var t2 = new InvalidationTracker();
            t2.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.PseudoClassState);
            h.Cascade.ApplyLayoutInvalidation(t2);
            var r2 = h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx, t2);
            var a2 = FindBoxFor(r2, h.Doc.GetElementById("a"));
            Assert.That(a2.PaddingTop, Is.EqualTo(basePad).Within(0.001));
        }

        static Element FindFirstElementWithClass(Document doc, string className) {
            foreach (var e in AllElements(doc)) {
                if (ClassListContains(e.ClassName, className)) return e;
            }
            Assert.Fail("missing class " + className);
            return null;
        }

        static bool ClassListContains(string raw, string className) {
            if (string.IsNullOrEmpty(raw)) return false;
            int start = 0;
            for (int i = 0; i <= raw.Length; i++) {
                if (i == raw.Length || raw[i] == ' ' || raw[i] == '\t' || raw[i] == '\n' || raw[i] == '\r') {
                    if (i - start == className.Length
                        && string.CompareOrdinal(raw, start, className, 0, className.Length) == 0) {
                        return true;
                    }
                    start = i + 1;
                }
            }
            return false;
        }

        static double AbsoluteX(Box box) {
            double x = 0;
            for (var b = box; b != null; b = b.Parent) x += b.X;
            return x;
        }

        static double MaxAbsoluteRight(Box box) {
            double right = AbsoluteX(box) + box.Width;
            for (int i = 0; i < box.Children.Count; i++) {
                double childRight = MaxAbsoluteRight(box.Children[i]);
                if (childRight > right) right = childRight;
            }
            return right;
        }

        static string DescribeSubtree(Box root) {
            var sb = new StringBuilder();
            DescribeSubtree(root, 0, sb);
            return sb.ToString();
        }

        static void DescribeSubtree(Box box, int depth, StringBuilder sb) {
            sb.Append(' ', depth * 2);
            sb.Append(box.GetType().Name);
            sb.Append(" class=");
            sb.Append(box.Element?.ClassName ?? "");
            sb.Append(" ax=");
            sb.Append(AbsoluteX(box).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(" x=");
            sb.Append(box.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(" w=");
            sb.Append(box.Width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            if (box is TextRun tr) {
                sb.Append(" text=");
                sb.Append(tr.Text);
            }
            sb.AppendLine();
            for (int i = 0; i < box.Children.Count; i++) DescribeSubtree(box.Children[i], depth + 1, sb);
        }
    }
}
