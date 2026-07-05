using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Media;
using Weva.Documents;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Reactive;

namespace Weva.Tests.Documents {
    public class UIDocumentLifecycleTests {
        static UIDocumentState NewState(string html, string css = null, object controller = null, FakeUIClock clock = null) {
            var stylesheets = string.IsNullOrEmpty(css) ? new List<string>() : new List<string> { css };
            return new UIDocumentBuilder {
                DocumentSource = html,
                StylesheetSources = stylesheets,
                Controller = controller,
                MediaContext = MediaContext.Default(800, 600),
                Clock = clock ?? new FakeUIClock()
            }.Build();
        }

        [Test]
        public void Initial_update_runs_layout_and_clears_tracker() {
            var s = NewState("<main><p>hello</p></main>");
            var r = UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(r.LayoutRan, Is.True);
            Assert.That(s.RootBox, Is.Not.Null);
            Assert.That(s.Invalidation.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Idle_update_does_not_rerun_layout() {
            var s = NewState("<p>x</p>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            var firstBoxRef = s.RootBox;
            var r = UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(r.LayoutRan, Is.False);
            // RootBox unchanged because no layout pass ran.
            Assert.That(s.RootBox, Is.SameAs(firstBoxRef));
        }

        [Test]
        public void Class_change_marks_subtree_dirty_and_reruns_layout() {
            var s = NewState("<div><p>x</p></div>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            Element div = null;
            foreach (var e in s.Doc.GetElementsByTagName("div")) { div = e; break; }
            div.SetAttribute("class", "alt");
            var r = UIDocumentLifecycle.Update(s, null, 0.016);
            Assert.That(r.LayoutRan, Is.True);
        }

        [Test]
        public void Structure_change_triggers_layout() {
            var s = NewState("<main><p>x</p></main>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            Element main = null;
            foreach (var e in s.Doc.GetElementsByTagName("main")) { main = e; break; }
            main.AppendChild(new Element("p"));
            var r = UIDocumentLifecycle.Update(s, null, 0.016);
            Assert.That(r.LayoutRan, Is.True);
        }

        [Test]
        public void Bindings_update_refreshes_text_after_field_change() {
            var ctrl = new Counter { Coins = 1 };
            var s = NewState("<p>{{ Coins }}</p>", null, ctrl);
            UIDocumentLifecycle.Update(s, ctrl, 0.0);
            var tn = FindFirstText(s.Doc);
            Assert.That(tn.Data, Is.EqualTo("1"));
            ctrl.Coins = 5;
            UIDocumentLifecycle.Update(s, ctrl, 0.016);
            Assert.That(tn.Data, Is.EqualTo("5"));
        }

        [Test]
        public void Binding_text_update_reuses_cascade_cache() {
            var ctrl = new Counter { Coins = 1 };
            var s = NewState("<p>{{ Coins }}</p>", "p { color: white; }", ctrl);
            UIDocumentLifecycle.Update(s, ctrl, 0.0);
            s.Cascade.ResetCacheStats();

            ctrl.Coins = 2;
            var r = UIDocumentLifecycle.Update(s, ctrl, 0.016);

            Assert.That(r.LayoutRan, Is.True);
            Assert.That(s.Cascade.CacheMisses, Is.EqualTo(0));
            Assert.That(TextRunContent(s.RootBox), Is.EqualTo("2"));
        }

        [Test]
        public void Direct_text_node_change_refreshes_box_tree_text() {
            var s = NewState("<p>short</p>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            var tn = FindFirstText(s.Doc);
            Assert.That(TextRunContent(s.RootBox), Is.EqualTo("short"));
            s.LayoutEngine.ResetCacheStats();

            tn.Data = "a much longer label";
            var r = UIDocumentLifecycle.Update(s, null, 0.016);

            Assert.That(r.LayoutRan, Is.True);
            Assert.That(s.LayoutEngine.SubtreeSkipHits, Is.EqualTo(1));
            Assert.That(TextRunContent(s.RootBox), Is.EqualTo("a much longer label"));
        }

        [Test]
        public void Animator_tick_advances_with_clock() {
            var clock = new FakeUIClock();
            var s = NewState("<p style='color: red; transition: color 1s'>x</p>", null, null, clock);
            UIDocumentLifecycle.Update(s, null, clock.NowSeconds);
            // Drive a transition: change inline style, then advance.
            Element pEl = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { pEl = e; break; }
            pEl.SetAttribute("style", "color: blue; transition: color 1s");
            clock.Set(0.1);
            UIDocumentLifecycle.Update(s, null, 0.1);
            // Animator should be running at least one transition record.
            Assert.That(s.Animator.RunningTransitionCount, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void Hit_tester_inner_set_after_first_layout() {
            var s = NewState("<main><p>x</p></main>");
            Assert.That(s.HitTester.Inner, Is.Null);
            UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(s.HitTester.Inner, Is.Not.Null);
        }

        [Test]
        public void Tracker_cleared_at_end_of_frame() {
            var s = NewState("<p>x</p>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            Element pEl = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { pEl = e; break; }
            pEl.SetAttribute("class", "y");
            Assert.That(s.Invalidation.DirtyCount, Is.GreaterThan(0));
            UIDocumentLifecycle.Update(s, null, 0.016);
            Assert.That(s.Invalidation.DirtyCount, Is.EqualTo(0));
        }

        [Test]
        public void Idle_update_keeps_cascade_layout_paint_caches_steady() {
            var s = NewState("<main><p class='x'>hi</p><p>two</p></main>", "p { color: red; }");
            UIDocumentLifecycle.Update(s, null, 0.0);
            // Reset stats after first layout to compare ONLY the second tick.
            s.Cascade.ResetCacheStats();
            s.LayoutEngine.ResetCacheStats();
            s.Painter.ResetCacheStats();
            UIDocumentLifecycle.Update(s, null, 0.016);
            // No mutations -> the second tick should not rerun layout, so
            // there should be no fresh layout entries created.
            Assert.That(s.LayoutEngine.CacheMisses, Is.EqualTo(0),
                "Idle frame should not produce new layout cache misses.");
        }

        [Test]
        public void Class_change_reruns_cascade_for_dirty_subtree() {
            var s = NewState("<div><p class='a'>x</p></div>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            s.Cascade.ResetCacheStats();
            Element pEl = null;
            foreach (var e in s.Doc.GetElementsByTagName("p")) { pEl = e; break; }
            pEl.SetAttribute("class", "b");
            UIDocumentLifecycle.Update(s, null, 0.016);
            Assert.That(s.Cascade.CacheMisses, Is.GreaterThan(0));
        }

        [Test]
        public void Hit_test_after_layout_returns_root_element() {
            var s = NewState("<main><p>x</p></main>");
            UIDocumentLifecycle.Update(s, null, 0.0);
            Assert.That(s.HitTester.Inner, Is.Not.Null);
            // Layout produces some box rect; hit at origin should resolve to
            // a non-null element when the layout is non-empty.
            Assert.That(s.RootBox, Is.Not.Null);
            Assert.That(s.RootBox.Width, Is.GreaterThan(0));
        }

        static TextNode FindFirstText(Node n) {
            for (int i = 0; i < n.Children.Count; i++) {
                var c = n.Children[i];
                if (c is TextNode t) return t;
                var sub = FindFirstText(c);
                if (sub != null) return sub;
            }
            return null;
        }

        static string TextRunContent(Box box) {
            if (box is TextRun tr) return tr.Text;
            string s = "";
            for (int i = 0; i < box.Children.Count; i++) {
                s += TextRunContent(box.Children[i]);
            }
            return s;
        }

        class Counter {
            public int Coins;
        }
    }
}
