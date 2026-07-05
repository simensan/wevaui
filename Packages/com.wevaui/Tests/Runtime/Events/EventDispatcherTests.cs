using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Events {
    public class EventDispatcherTests {
        static Document Html(string s) => HtmlParser.Parse(s);

        EventDispatcher Build(Document doc, FakeHitTester ht = null) {
            return new EventDispatcher(doc, ht ?? new FakeHitTester(), new FakeUIClock());
        }

        [Test]
        public void AddEventListener_handler_runs_with_correct_kind() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Build(doc, ht);

            EventKind seen = (EventKind)(-1);
            d.AddEventListener(a, EventKind.PointerDown, e => seen = e.Kind);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(EventKind.PointerDown));
        }

        [Test]
        public void Capture_phase_listeners_run_in_root_to_target_order() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var root = doc.GetElementById("root");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(mid, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(root, EventKind.PointerDown, _ => seen.Add("root"), useCapture: true);
            d.AddEventListener(mid, EventKind.PointerDown, _ => seen.Add("mid"), useCapture: true);
            d.AddEventListener(leaf, EventKind.PointerDown, _ => seen.Add("leaf"), useCapture: true);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "root", "mid", "leaf" }));
        }

        [Test]
        public void Bubble_phase_listeners_run_in_target_to_root_order() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var root = doc.GetElementById("root");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(mid, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(root, EventKind.PointerDown, _ => seen.Add("root"));
            d.AddEventListener(mid, EventKind.PointerDown, _ => seen.Add("mid"));
            d.AddEventListener(leaf, EventKind.PointerDown, _ => seen.Add("leaf"));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "leaf", "mid", "root" }));
        }

        [Test]
        public void Phase_value_is_set_during_dispatch() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            EventPhase rootCapture = EventPhase.AtTarget;
            EventPhase leafAt = EventPhase.Capture;
            EventPhase rootBubble = EventPhase.AtTarget;
            d.AddEventListener(root, EventKind.PointerDown, e => rootCapture = e.Phase, useCapture: true);
            d.AddEventListener(leaf, EventKind.PointerDown, e => leafAt = e.Phase);
            d.AddEventListener(root, EventKind.PointerDown, e => rootBubble = e.Phase);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(rootCapture, Is.EqualTo(EventPhase.Capture));
            Assert.That(leafAt, Is.EqualTo(EventPhase.AtTarget));
            Assert.That(rootBubble, Is.EqualTo(EventPhase.Bubble));
        }

        [Test]
        public void StopPropagation_halts_subsequent_ancestors_but_not_current_listeners() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(leaf, EventKind.PointerDown, e => { seen.Add("leaf1"); e.StopPropagation(); });
            d.AddEventListener(leaf, EventKind.PointerDown, _ => seen.Add("leaf2"));
            d.AddEventListener(root, EventKind.PointerDown, _ => seen.Add("root"));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "leaf1", "leaf2" }));
        }

        [Test]
        public void StopImmediatePropagation_halts_everything_immediately() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(leaf, EventKind.PointerDown, e => { seen.Add("leaf1"); e.StopImmediatePropagation(); });
            d.AddEventListener(leaf, EventKind.PointerDown, _ => seen.Add("leaf2"));
            d.AddEventListener(root, EventKind.PointerDown, _ => seen.Add("root"));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "leaf1" }));
        }

        [Test]
        public void PreventDefault_flag_is_observable() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Build(doc, ht);

            bool wasPrevented = false;
            d.AddEventListener(a, EventKind.PointerDown, e => {
                e.PreventDefault();
                wasPrevented = e.DefaultPrevented;
            });
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(wasPrevented, Is.True);
        }

        [Test]
        public void PointerDown_then_PointerUp_same_target_fires_click() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Build(doc, ht);

            int clicks = 0;
            d.AddEventListener(a, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(1));
        }

        [Test]
        public void PointerDown_then_PointerUp_on_different_children_fires_click_on_common_ancestor() {
            var doc = Html("<li id=\"card\"><span id=\"thumb\"></span><span id=\"label\"></span></li>");
            var card = doc.GetElementById("card");
            var thumb = doc.GetElementById("thumb");
            var label = doc.GetElementById("label");
            var ht = new FakeHitTester();
            ht.Add(card, 0, 0, 200, 100);
            ht.Add(thumb, 0, 0, 80, 100);
            ht.Add(label, 80, 0, 120, 100);
            var d = Build(doc, ht);

            int clicks = 0;
            Element clickTarget = null;
            d.AddEventListener(card, EventKind.Click, e => {
                clicks++;
                clickTarget = e.Target;
            });

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(120, 10, 0, KeyModifiers.None);

            Assert.That(clicks, Is.EqualTo(1));
            Assert.That(clickTarget, Is.SameAs(card));
        }

        [Test]
        public void Anchor_fragment_click_sets_target_state() {
            var doc = Html("<a id=\"link\" href=\"#target\"><span id=\"child\"></span></a><section id=\"target\"></section>");
            var child = doc.GetElementById("child");
            var target = doc.GetElementById("target");
            var ht = new FakeHitTester();
            ht.Add(child, 0, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);

            Assert.That(d.TargetFragment, Is.EqualTo("target"));
            Assert.That((d.StateProvider.GetState(target) & ElementState.Target) != 0, Is.True);
            Assert.That(SelectorMatcher.Matches(SelectorParser.Parse(":target"), target, d.StateProvider), Is.True);
        }

        [Test]
        public void Prevented_anchor_click_does_not_set_target_state() {
            var doc = Html("<a id=\"link\" href=\"#target\">link</a><section id=\"target\"></section>");
            var link = doc.GetElementById("link");
            var target = doc.GetElementById("target");
            var ht = new FakeHitTester();
            ht.Add(link, 0, 0, 100, 100);
            var d = Build(doc, ht);
            d.AddEventListener(link, EventKind.Click, e => e.PreventDefault());

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);

            Assert.That(d.TargetFragment, Is.Null);
            Assert.That((d.StateProvider.GetState(target) & ElementState.Target) != 0, Is.False);
        }

        [Test]
        public void PointerDown_then_PointerUp_different_targets_fires_no_click() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Build(doc, ht);

            int clicksA = 0;
            int clicksB = 0;
            int pointerUpsB = 0;
            d.AddEventListener(a, EventKind.Click, _ => clicksA++);
            d.AddEventListener(b, EventKind.Click, _ => clicksB++);
            d.AddEventListener(b, EventKind.PointerUp, _ => pointerUpsB++);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(150, 10, 0, KeyModifiers.None);
            Assert.That(clicksA, Is.EqualTo(0));
            Assert.That(clicksB, Is.EqualTo(0));
            Assert.That(pointerUpsB, Is.EqualTo(1));
        }

        [Test]
        public void PointerMove_changing_hit_dispatches_leave_and_enter() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Build(doc, ht);

            int leaveA = 0, enterA = 0, leaveB = 0, enterB = 0;
            d.AddEventListener(a, EventKind.PointerLeave, _ => leaveA++);
            d.AddEventListener(a, EventKind.PointerEnter, _ => enterA++);
            d.AddEventListener(b, EventKind.PointerLeave, _ => leaveB++);
            d.AddEventListener(b, EventKind.PointerEnter, _ => enterB++);

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            d.DispatchPointerMove(150, 50, KeyModifiers.None);

            Assert.That(enterA, Is.EqualTo(1));
            Assert.That(leaveA, Is.EqualTo(1));
            Assert.That(enterB, Is.EqualTo(1));
            Assert.That(leaveB, Is.EqualTo(0));
        }

        [Test]
        public void PointerEnter_and_PointerLeave_do_not_bubble() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var root = doc.GetElementById("root");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 200, 200);
            ht.Add(mid, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 50, 50);
            var d = Build(doc, ht);

            int rootEnter = 0, midEnter = 0, leafEnter = 0;
            int rootEnterCap = 0, midEnterCap = 0;
            d.AddEventListener(root, EventKind.PointerEnter, _ => rootEnter++);
            d.AddEventListener(mid, EventKind.PointerEnter, _ => midEnter++);
            d.AddEventListener(leaf, EventKind.PointerEnter, _ => leafEnter++);
            d.AddEventListener(root, EventKind.PointerEnter, _ => rootEnterCap++, useCapture: true);
            d.AddEventListener(mid, EventKind.PointerEnter, _ => midEnterCap++, useCapture: true);

            d.DispatchPointerMove(10, 10, KeyModifiers.None);
            Assert.That(leafEnter, Is.EqualTo(1));
            Assert.That(midEnter, Is.EqualTo(1));
            Assert.That(rootEnter, Is.EqualTo(1));
            Assert.That(rootEnterCap, Is.EqualTo(0));
            Assert.That(midEnterCap, Is.EqualTo(0));
        }

        [Test]
        public void PointerMove_does_bubble() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 200, 200);
            ht.Add(leaf, 0, 0, 50, 50);
            var d = Build(doc, ht);

            int rootMove = 0, leafMove = 0;
            d.AddEventListener(root, EventKind.PointerMove, _ => rootMove++);
            d.AddEventListener(leaf, EventKind.PointerMove, _ => leafMove++);

            d.DispatchPointerMove(10, 10, KeyModifiers.None);
            Assert.That(leafMove, Is.EqualTo(1));
            Assert.That(rootMove, Is.EqualTo(1));
        }

        [Test]
        public void KeyDown_dispatches_to_focused_element() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);

            string seenKey = null;
            d.AddEventListener(b, EventKind.KeyDown, e => seenKey = ((KeyboardEvent)e).Key);
            d.DispatchKeyDown("a", "KeyA", KeyModifiers.None, false);
            Assert.That(seenKey, Is.EqualTo("a"));
        }

        [Test]
        public void KeyDown_with_no_focus_dispatches_to_document_root() {
            // UI Events spec (DOM Living Standard §2.5): when no element has
            // focus, KeyboardEvent targets the document root. The engine's
            // DispatchKeyDown falls back to RootElement (first Element child
            // of the Document) when `focused` is null — this is the
            // <html> element produced by HtmlParser's implicit
            // <html><head><body> wrapping, even when the source has only a
            // <section>. So a no-focus KeyDown dispatches to <html>, which
            // bubbles up to nothing (already at root). The user-authored
            // <section id="root"> is a great-grandchild of the dispatch
            // target and the dispatch path (root → target) does NOT walk
            // into it — listeners registered on inner elements don't fire.
            var doc = Html("<section id=\"root\"><button id=\"b\"></button></section>");
            // doc.Children[0] is the engine-RootElement (the <html> wrapper).
            Weva.Dom.Element documentRoot = null;
            foreach (var c in doc.Children) {
                if (c is Weva.Dom.Element e) { documentRoot = e; break; }
            }
            Assert.That(documentRoot, Is.Not.Null, "HtmlParser must wrap source in a document root element");
            var d = Build(doc);

            Element seenTarget = null;
            d.AddEventListener(documentRoot, EventKind.KeyDown, e => seenTarget = e.Target);
            d.DispatchKeyDown("a", "KeyA", KeyModifiers.None, false);
            Assert.That(seenTarget, Is.SameAs(documentRoot),
                "no-focus KeyDown should target the document root (the parser-wrapped <html>), not an arbitrary inner element");
        }

        [Test]
        public void KeyDown_with_no_focus_does_not_reach_inner_elements_via_dispatch_path() {
            // Regression guard for the renamed test above: a listener
            // registered on an INNER element (the section, not the
            // <html> root) must NOT fire from a no-focus KeyDown. The
            // dispatch path is root → target with target = root, so the
            // section is not on the path and its listener stays silent.
            var doc = Html("<section id=\"root\"><button id=\"b\"></button></section>");
            var section = doc.GetElementById("root");
            var d = Build(doc);

            bool sectionListenerFired = false;
            d.AddEventListener(section, EventKind.KeyDown, _ => sectionListenerFired = true);
            d.DispatchKeyDown("a", "KeyA", KeyModifiers.None, false);
            Assert.That(sectionListenerFired, Is.False,
                "no-focus KeyDown must target the document root; a listener on an inner element must not fire");
        }

        [Test]
        public void KeyDown_bubbles() {
            var doc = Html("<section id=\"root\"><button id=\"b\"></button></section>");
            var root = doc.GetElementById("root");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);

            int rootHits = 0;
            int btnHits = 0;
            d.AddEventListener(root, EventKind.KeyDown, _ => rootHits++);
            d.AddEventListener(b, EventKind.KeyDown, _ => btnHits++);
            d.DispatchKeyDown("x", "KeyX", KeyModifiers.None, false);
            Assert.That(btnHits, Is.EqualTo(1));
            Assert.That(rootHits, Is.EqualTo(1));
        }

        [Test]
        public void Tab_advances_focus_to_next() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(a);
            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false);
            Assert.That(d.FocusedElement, Is.SameAs(b));
        }

        [Test]
        public void Shift_Tab_reverses_focus() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);
            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.Shift, false);
            Assert.That(d.FocusedElement, Is.SameAs(a));
        }

        [Test]
        public void Tab_with_PreventDefault_does_not_change_focus() {
            var doc = Html("<div><button id=\"a\"></button><button id=\"b\"></button></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(a);

            d.AddEventListener(a, EventKind.KeyDown, e => {
                if (((KeyboardEvent)e).Key == "Tab") e.PreventDefault();
            });
            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false);
            Assert.That(d.FocusedElement, Is.SameAs(a));
        }

        [Test]
        public void Multiple_listeners_run_in_registration_order() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(b, EventKind.PointerDown, _ => seen.Add("first"));
            d.AddEventListener(b, EventKind.PointerDown, _ => seen.Add("second"));
            d.AddEventListener(b, EventKind.PointerDown, _ => seen.Add("third"));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "first", "second", "third" }));
        }

        [Test]
        public void RemoveEventListener_stops_handler() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            int count = 0;
            EventListener handler = _ => count++;
            d.AddEventListener(b, EventKind.PointerDown, handler);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(count, Is.EqualTo(1));
            d.RemoveEventListener(b, EventKind.PointerDown, handler);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Removing_handler_during_dispatch_does_not_crash() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            int countA = 0;
            int countB = 0;
            EventListener handlerB = _ => countB++;
            d.AddEventListener(b, EventKind.PointerDown, e => {
                countA++;
                d.RemoveEventListener(b, EventKind.PointerDown, handlerB);
            });
            d.AddEventListener(b, EventKind.PointerDown, handlerB);

            Assert.DoesNotThrow(() => d.DispatchPointerDown(10, 10, 0, KeyModifiers.None));
            Assert.That(countA, Is.EqualTo(1));
            Assert.That(countB, Is.EqualTo(1));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(countA, Is.EqualTo(2));
            Assert.That(countB, Is.EqualTo(1));
        }

        [Test]
        public void Pointer_down_focuses_focusable_element() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.SameAs(b));
        }

        [Test]
        public void Pointer_down_on_non_focusable_blurs() {
            var doc = Html("<button id=\"b\"></button><div id=\"d\"></div>");
            var b = doc.GetElementById("b");
            var div = doc.GetElementById("d");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            ht.Add(div, 100, 0, 100, 100);
            var d = Build(doc, ht);

            d.Focus(b);
            Assert.That(d.FocusedElement, Is.SameAs(b));
            d.DispatchPointerDown(150, 10, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.Null);
        }

        [Test]
        public void Pointer_down_focuses_nearest_focusable_ancestor() {
            var doc = Html("<button id=\"b\"><span id=\"label\">x</span></button>");
            var b = doc.GetElementById("b");
            var label = doc.GetElementById("label");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            ht.Add(label, 10, 10, 30, 30);
            var d = Build(doc, ht);

            d.DispatchPointerDown(15, 15, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.SameAs(b));
        }

        [Test]
        public void Focus_dispatches_focus_and_blur_events_with_related_target() {
            var doc = Html("<button id=\"a\"></button><button id=\"b\"></button>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var d = Build(doc);

            Element focusRelated = null;
            Element blurRelated = null;
            d.AddEventListener(a, EventKind.Blur, e => blurRelated = ((FocusEvent)e).RelatedTarget);
            d.AddEventListener(b, EventKind.Focus, e => focusRelated = ((FocusEvent)e).RelatedTarget);

            d.Focus(a);
            d.Focus(b);

            Assert.That(blurRelated, Is.SameAs(b));
            Assert.That(focusRelated, Is.SameAs(a));
        }

        [Test]
        public void Focus_event_does_not_bubble() {
            var doc = Html("<section id=\"root\"><button id=\"b\"></button></section>");
            var root = doc.GetElementById("root");
            var b = doc.GetElementById("b");
            var d = Build(doc);

            int rootFocus = 0;
            d.AddEventListener(root, EventKind.Focus, _ => rootFocus++);
            d.Focus(b);
            Assert.That(rootFocus, Is.EqualTo(0));
        }

        [Test]
        public void Buttons_mask_tracks_held_buttons() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var d = Build(doc, ht);

            int seenButtons = 0;
            d.AddEventListener(a, EventKind.PointerMove, e => seenButtons = ((PointerEvent)e).Buttons);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerDown(10, 10, 2, KeyModifiers.None);
            d.DispatchPointerMove(10, 10, KeyModifiers.None);
            Assert.That(seenButtons, Is.EqualTo(1 | 2));

            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerMove(10, 10, KeyModifiers.None);
            Assert.That(seenButtons, Is.EqualTo(2));
        }

        [Test]
        public void Hover_state_follows_pointer_via_state_provider() {
            var doc = Html("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Hover) != 0, Is.True);
            d.DispatchPointerMove(150, 50, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Hover) != 0, Is.True);
            Assert.That((d.StateProvider.GetState(a) & ElementState.Hover) != 0, Is.False);
        }

        [Test]
        public void Active_state_during_pointer_down_held() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Active) != 0, Is.True);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(b) & ElementState.Active) != 0, Is.False);
        }

        [Test]
        public void Active_state_propagates_to_ancestors_on_descendant_press() {
            // CSS Selectors L4 §11.4.1: `:active` matches an element AND
            // each of its ancestors while the press is held. Pressing a
            // descendant must flip `:active` on every parent up the
            // chain — otherwise rules like `.card:active { transform:
            // scale(0.98) }` only fire when the click lands on the
            // bare padding/border of the card (where the hit-test
            // resolves directly to the card), which is exactly what
            // the user saw in a real game's challenges panel.
            var doc = Html("<li id=\"card\"><div id=\"body\"><span id=\"label\">x</span></div></li>");
            var card = doc.GetElementById("card");
            var body = doc.GetElementById("body");
            var label = doc.GetElementById("label");
            var ht = new FakeHitTester();
            ht.Add(label, 0, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(label) & ElementState.Active) != 0, Is.True, "label");
            Assert.That((d.StateProvider.GetState(body) & ElementState.Active) != 0, Is.True, "body (ancestor of label)");
            Assert.That((d.StateProvider.GetState(card) & ElementState.Active) != 0, Is.True, "card (ancestor of label)");

            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);
            Assert.That((d.StateProvider.GetState(label) & ElementState.Active) != 0, Is.False, "label cleared");
            Assert.That((d.StateProvider.GetState(body) & ElementState.Active) != 0, Is.False, "body cleared");
            Assert.That((d.StateProvider.GetState(card) & ElementState.Active) != 0, Is.False, "card cleared");
        }

        [Test]
        public void FocusVisible_set_on_tab_focus_cleared_on_click_focus() {
            var doc = Html("<button id=\"a\"></button><button id=\"b\"></button>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            ht.Add(b, 100, 0, 100, 100);
            var d = Build(doc, ht);

            d.DispatchKeyDown("Tab", "Tab", KeyModifiers.None, false);
            Assert.That((d.StateProvider.GetState(d.FocusedElement) & ElementState.FocusVisible) != 0, Is.True);

            d.DispatchPointerDown(150, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(150, 50, 0, KeyModifiers.None);
            Assert.That(d.FocusedElement, Is.SameAs(b));
            Assert.That((d.StateProvider.GetState(b) & ElementState.FocusVisible) != 0, Is.False);
        }

        [Test]
        public void Click_event_target_invariant() {
            var doc = Html("<section id=\"root\"><span id=\"leaf\"></span></section>");
            var root = doc.GetElementById("root");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 50, 50);
            var d = Build(doc, ht);

            Element rootClickTarget = null;
            Element leafClickTarget = null;
            d.AddEventListener(root, EventKind.Click, e => rootClickTarget = e.Target);
            d.AddEventListener(leaf, EventKind.Click, e => leafClickTarget = e.Target);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);

            Assert.That(leafClickTarget, Is.SameAs(leaf));
            Assert.That(rootClickTarget, Is.SameAs(leaf));
        }

        [Test]
        public void KeyboardEvent_modifier_flags_propagate() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);

            KeyboardEvent seen = null;
            d.AddEventListener(b, EventKind.KeyDown, e => seen = (KeyboardEvent)e);
            d.DispatchKeyDown("a", "KeyA", KeyModifiers.Shift | KeyModifiers.Ctrl, true);
            Assert.That(seen, Is.Not.Null);
            Assert.That(seen.ShiftKey, Is.True);
            Assert.That(seen.CtrlKey, Is.True);
            Assert.That(seen.AltKey, Is.False);
            Assert.That(seen.MetaKey, Is.False);
            Assert.That(seen.Repeat, Is.True);
        }

        [Test]
        public void KeyUp_dispatches_to_focused_element() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);

            int hits = 0;
            d.AddEventListener(b, EventKind.KeyUp, _ => hits++);
            d.DispatchKeyUp("a", "KeyA", KeyModifiers.None, false);
            Assert.That(hits, Is.EqualTo(1));
        }

        [Test]
        public void Click_does_not_fire_if_pointer_up_is_outside_root() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var ht = new FakeHitTester();
            ht.Add(b, 0, 0, 100, 100);
            var d = Build(doc, ht);

            int clicks = 0;
            d.AddEventListener(b, EventKind.Click, _ => clicks++);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(500, 500, 0, KeyModifiers.None);
            Assert.That(clicks, Is.EqualTo(0));
        }

        [Test]
        public void TimestampSeconds_set_from_clock() {
            var doc = Html("<div id=\"a\"></div>");
            var a = doc.GetElementById("a");
            var ht = new FakeHitTester();
            ht.Add(a, 0, 0, 100, 100);
            var clock = new FakeUIClock(initial: 12.5);
            var d = new EventDispatcher(doc, ht, clock);

            double ts = -1;
            d.AddEventListener(a, EventKind.PointerDown, e => ts = e.TimestampSeconds);
            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(ts, Is.EqualTo(12.5));
        }

        // Regression: target stays pinned to the deepest hit element while
        // currentTarget walks the ancestor chain during bubbling. Audited
        // EventDispatcher.Dispatch sets evt.Target once and updates
        // evt.CurrentTarget per node — this guards both invariants.
        [Test]
        public void Bubble_currentTarget_walks_ancestors_target_stays_at_leaf() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var root = doc.GetElementById("root");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(mid, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<(Element target, Element current)>();
            EventListener cap = e => seen.Add((e.Target, e.CurrentTarget));
            d.AddEventListener(leaf, EventKind.Click, cap);
            d.AddEventListener(mid, EventKind.Click, cap);
            d.AddEventListener(root, EventKind.Click, cap);

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            d.DispatchPointerUp(10, 10, 0, KeyModifiers.None);

            Assert.That(seen.Count, Is.EqualTo(3));
            foreach (var s in seen) Assert.That(s.target, Is.SameAs(leaf));
            Assert.That(seen[0].current, Is.SameAs(leaf));
            Assert.That(seen[1].current, Is.SameAs(mid));
            Assert.That(seen[2].current, Is.SameAs(root));
        }

        // Regression: StopPropagation during the capture phase must
        // suppress the at-target listener and the entire bubble walk.
        [Test]
        public void StopPropagation_in_capture_skips_target_and_bubble() {
            var doc = Html("<section id=\"root\"><div id=\"mid\"><span id=\"leaf\"></span></div></section>");
            var root = doc.GetElementById("root");
            var mid = doc.GetElementById("mid");
            var leaf = doc.GetElementById("leaf");
            var ht = new FakeHitTester();
            ht.Add(root, 0, 0, 100, 100);
            ht.Add(mid, 0, 0, 100, 100);
            ht.Add(leaf, 0, 0, 100, 100);
            var d = Build(doc, ht);

            var seen = new List<string>();
            d.AddEventListener(root, EventKind.PointerDown, e => { seen.Add("rootCap"); e.StopPropagation(); }, useCapture: true);
            d.AddEventListener(mid, EventKind.PointerDown, _ => seen.Add("midCap"), useCapture: true);
            d.AddEventListener(leaf, EventKind.PointerDown, _ => seen.Add("leafAt"));
            d.AddEventListener(root, EventKind.PointerDown, _ => seen.Add("rootBubble"));

            d.DispatchPointerDown(10, 10, 0, KeyModifiers.None);
            Assert.That(seen, Is.EqualTo(new List<string> { "rootCap" }));
        }

        // GAP PIN: EventKind.KeyPress is enumerated in EventKind.cs but the
        // dispatcher never produces it (DispatchKeyDown emits KeyDown only;
        // no DispatchKeyPress entry point exists). InputController and form
        // bindings only listen for KeyDown/KeyUp. If a future change starts
        // synthesizing KeyPress (e.g. for legacy compat), this test will
        // need to be updated — until then it documents the missing path.
        [Test]
        public void KeyPress_is_never_dispatched_v1_gap() {
            var doc = Html("<button id=\"b\"></button>");
            var b = doc.GetElementById("b");
            var d = Build(doc);
            d.Focus(b);

            int keyPressHits = 0;
            d.AddEventListener(b, EventKind.KeyPress, _ => keyPressHits++);
            d.DispatchKeyDown("a", "KeyA", KeyModifiers.None, false);
            d.DispatchKeyUp("a", "KeyA", KeyModifiers.None, false);
            Assert.That(keyPressHits, Is.EqualTo(0),
                "KeyPress is declared in EventKind but no dispatcher path emits it. " +
                "Update this test if KeyPress becomes a real event.");
        }
    }
}
