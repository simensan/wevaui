using System.Reflection;
using NUnit.Framework;
using Weva.Binding;
using Weva.Dom;
using Weva.Events;
using Weva.Parsing;

namespace Weva.Tests.Binding {
    /// <summary>
    /// Tests for on-pointerdown / on-pointermove / on-pointerup / on-pointerleave
    /// attribute-to-EventKind mapping and end-to-end handler dispatch.
    /// </summary>
    public class PointerEventBindingTests {

        // ---------------------------------------------------------------------------
        // Shared controller used across multiple tests.
        //
        // POOLING NOTE: PointerEvent instances dispatched by EventDispatcher are
        // rented from a pool and RETURNED after dispatch (see PointerEvent.cs
        // header). Handlers MUST NOT retain the event reference — only snapshot
        // the fields they need during the synchronous handler call.
        // ---------------------------------------------------------------------------
        public class DragController {
            public int DownCalls;
            public int MoveCalls;
            public int UpCalls;
            public int LeaveCalls;
            public int NoArgCalls;

            // Snapshots of kind — captured synchronously inside the handler,
            // not the event object itself (which is pooled and returned).
            public EventKind LastDownKind;
            public EventKind LastMoveKind;
            public EventKind LastUpKind;
            public EventKind LastLeaveKind;
            public Element   LastDownTarget;

            // For typed-handler tests we snapshot the position fields inline.
            public double LastDownX = double.NaN;
            public double LastDownY = double.NaN;
            public double LastMoveX = double.NaN;
            public double LastMoveY = double.NaN;

            public void OnPointerDown(UIEvent e)  { DownCalls++;  LastDownKind   = e.Kind; LastDownTarget = e.Target; }
            public void OnPointerMove(UIEvent e)  { MoveCalls++;  LastMoveKind   = e.Kind; }
            public void OnPointerUp(UIEvent e)    { UpCalls++;    LastUpKind     = e.Kind; }
            public void OnPointerLeave(UIEvent e) { LeaveCalls++; LastLeaveKind  = e.Kind; }

            public void OnPointerDownTyped(PointerEvent e) { DownCalls++; LastDownX = e.X; LastDownY = e.Y; }
            public void OnPointerMoveTyped(PointerEvent e) { MoveCalls++; LastMoveX = e.X; LastMoveY = e.Y; }

            // Parameterless handler — should fire for any event kind.
            public void OnAnyNoArgs() => NoArgCalls++;
        }

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------
        static (Document doc, Element el, EventDispatcher d) Build(
                double x = 0, double y = 0, double w = 200, double h = 200) {
            var doc = new Document();
            var el  = new Element("div");
            doc.AppendChild(el);
            var ht = new BindingFakeHitTester();
            ht.Add(el, x, y, w, h);
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            return (doc, el, d);
        }

        static MethodInfo M(string name) =>
            typeof(DragController).GetMethod(
                name, BindingFlags.Public | BindingFlags.Instance);

        // ---------------------------------------------------------------------------
        // 1. EventAttributeMap resolves all four on-pointer* attributes.
        // ---------------------------------------------------------------------------

        [Test]
        public void EventAttributeMap_on_pointerdown_resolves_to_PointerDown() {
            Assert.That(EventAttributeMap.TryGet("on-pointerdown", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(EventKind.PointerDown));
        }

        [Test]
        public void EventAttributeMap_on_pointermove_resolves_to_PointerMove() {
            Assert.That(EventAttributeMap.TryGet("on-pointermove", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(EventKind.PointerMove));
        }

        [Test]
        public void EventAttributeMap_on_pointerup_resolves_to_PointerUp() {
            Assert.That(EventAttributeMap.TryGet("on-pointerup", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(EventKind.PointerUp));
        }

        [Test]
        public void EventAttributeMap_on_pointerleave_resolves_to_PointerLeave() {
            Assert.That(EventAttributeMap.TryGet("on-pointerleave", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(EventKind.PointerLeave));
        }

        // ---------------------------------------------------------------------------
        // 2. Unknown on-pointer* variant is NOT in the map (no crash, just false).
        // ---------------------------------------------------------------------------

        [Test]
        public void EventAttributeMap_unknown_on_pointerfoo_returns_false() {
            Assert.That(EventAttributeMap.TryGet("on-pointerfoo", out _), Is.False);
        }

        [Test]
        public void EventAttributeMap_on_pointerenter_is_not_mapped() {
            // on-pointerenter is intentionally absent (PointerEnter is
            // a non-bubbling per-element event; the dispatcher fires it
            // internally via UpdateHover, not via user attribute binding).
            Assert.That(EventAttributeMap.TryGet("on-pointerenter", out _), Is.False);
        }

        // ---------------------------------------------------------------------------
        // 3. BindingScanner picks up the four pointer attributes end-to-end.
        // ---------------------------------------------------------------------------

        [Test]
        public void BindingScanner_detects_on_pointerdown_as_event_binding() {
            var doc = HtmlParser.Parse("<div on-pointerdown=\"OnPointerDown\"></div>");
            var set = BindingScanner.Scan(doc, new DragController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(1));
            Assert.That(set.EventBindings[0].Kind, Is.EqualTo(EventKind.PointerDown));
            Assert.That(set.EventBindings[0].MethodName, Is.EqualTo("OnPointerDown"));
        }

        [Test]
        public void BindingScanner_detects_all_four_pointer_attributes() {
            var doc = HtmlParser.Parse(
                "<div on-pointerdown=\"OnPointerDown\"" +
                "     on-pointermove=\"OnPointerMove\"" +
                "     on-pointerup=\"OnPointerUp\"" +
                "     on-pointerleave=\"OnPointerLeave\"></div>");
            var set = BindingScanner.Scan(doc, new DragController());
            Assert.That(set.EventBindings.Count, Is.EqualTo(4));
        }

        [Test]
        public void BindingScanner_unknown_on_pointerfoo_is_silently_ignored() {
            var doc = HtmlParser.Parse("<div on-pointerfoo=\"OnAnyNoArgs\"></div>");
            var set = BindingScanner.Scan(doc, new DragController());
            // Not a known event attribute — treated as a plain attribute, no warning.
            Assert.That(set.EventBindings.Count, Is.EqualTo(0));
            Assert.That(set.Warnings.Count, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------------------
        // 4. on-pointerdown handler fires when DispatchPointerDown is called.
        // ---------------------------------------------------------------------------

        [Test]
        public void PointerDown_handler_fires_on_DispatchPointerDown() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerDown, M("OnPointerDown"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That(c.DownCalls, Is.EqualTo(1));
        }

        [Test]
        public void PointerDown_handler_receives_UIEvent_with_correct_Kind_and_Target() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerDown, M("OnPointerDown"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That(c.DownCalls, Is.EqualTo(1));
            Assert.That(c.LastDownKind, Is.EqualTo(EventKind.PointerDown));
            Assert.That(c.LastDownTarget, Is.SameAs(el));
        }

        // ---------------------------------------------------------------------------
        // 5. on-pointermove handler fires on DispatchPointerMove.
        // ---------------------------------------------------------------------------

        [Test]
        public void PointerMove_handler_fires_on_DispatchPointerMove() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerMove, M("OnPointerMove"), c);
            b.Wire(d);
            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(c.MoveCalls, Is.EqualTo(1));
        }

        [Test]
        public void PointerMove_handler_receives_event_with_correct_Kind() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerMove, M("OnPointerMove"), c);
            b.Wire(d);
            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(c.MoveCalls, Is.EqualTo(1));
            Assert.That(c.LastMoveKind, Is.EqualTo(EventKind.PointerMove));
        }

        // ---------------------------------------------------------------------------
        // 6. on-pointerup handler fires on DispatchPointerUp.
        // ---------------------------------------------------------------------------

        [Test]
        public void PointerUp_handler_fires_on_DispatchPointerUp() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerUp, M("OnPointerUp"), c);
            b.Wire(d);
            // DispatchPointerUp also fires Click (which requires a prior Down),
            // but PointerUp fires unconditionally on the hit target.
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(c.UpCalls, Is.EqualTo(1));
        }

        [Test]
        public void PointerUp_handler_receives_event_with_correct_Kind() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerUp, M("OnPointerUp"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(c.UpCalls, Is.EqualTo(1));
            Assert.That(c.LastUpKind, Is.EqualTo(EventKind.PointerUp));
        }

        // ---------------------------------------------------------------------------
        // 7. on-pointerleave handler fires when pointer leaves the element
        //    (via UpdateHover internal path — move the pointer off the element).
        // ---------------------------------------------------------------------------

        [Test]
        public void PointerLeave_handler_fires_when_pointer_leaves_element() {
            var (doc, el, d) = Build(0, 0, 100, 100);
            // Add a second element outside the first so a move to (150,150)
            // hits something (not null) — UpdateHover fires leave on el.
            var el2 = new Element("div");
            doc.AppendChild(el2);
            var ht = new BindingFakeHitTester();
            ht.Add(el, 0, 0, 100, 100);
            ht.Add(el2, 100, 100, 100, 100);
            var d2 = new EventDispatcher(doc, ht, new FakeUIClock());

            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerLeave, M("OnPointerLeave"), c);
            b.Wire(d2);

            // Enter el first, then move off it.
            d2.DispatchPointerMove(50, 50, KeyModifiers.None);  // enter el
            d2.DispatchPointerMove(150, 150, KeyModifiers.None); // leave el, enter el2
            Assert.That(c.LeaveCalls, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------------------
        // 8. Typed PointerEvent handler receives position data.
        // ---------------------------------------------------------------------------

        [Test]
        public void Typed_PointerEvent_handler_receives_position() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerDown, M("OnPointerDownTyped"), c);
            b.Wire(d);
            d.DispatchPointerDown(42, 77, 0, KeyModifiers.None);
            Assert.That(c.DownCalls, Is.EqualTo(1));
            // X/Y are snapshotted by the handler (pool contract: don't retain ref).
            Assert.That(c.LastDownX, Is.EqualTo(42.0).Within(0.001));
            Assert.That(c.LastDownY, Is.EqualTo(77.0).Within(0.001));
        }

        [Test]
        public void Typed_PointerEvent_move_handler_receives_position() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerMove, M("OnPointerMoveTyped"), c);
            b.Wire(d);
            d.DispatchPointerMove(10, 20, KeyModifiers.None);
            Assert.That(c.MoveCalls, Is.EqualTo(1));
            Assert.That(c.LastMoveX, Is.EqualTo(10.0).Within(0.001));
            Assert.That(c.LastMoveY, Is.EqualTo(20.0).Within(0.001));
        }

        // ---------------------------------------------------------------------------
        // 9. Parameterless handlers work for pointer events.
        // ---------------------------------------------------------------------------

        [Test]
        public void Parameterless_handler_fires_for_pointerdown() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerDown, M("OnAnyNoArgs"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        [Test]
        public void Parameterless_handler_fires_for_pointermove() {
            var (_, el, d) = Build();
            var c = new DragController();
            var b = new EventBinding(el, EventKind.PointerMove, M("OnAnyNoArgs"), c);
            b.Wire(d);
            d.DispatchPointerMove(50, 50, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------------------
        // 10. Regression: existing on-click binding still works after the map change.
        // ---------------------------------------------------------------------------

        [Test]
        public void OnClick_binding_still_fires_after_pointer_map_additions() {
            var (_, el, d) = Build();
            var c = new DragController();
            // Reuse OnAnyNoArgs as a click handler via direct EventBinding.
            var b = new EventBinding(el, EventKind.Click, M("OnAnyNoArgs"), c);
            b.Wire(d);
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);
            Assert.That(c.NoArgCalls, Is.EqualTo(1));
        }

        [Test]
        public void EventAttributeMap_on_click_still_maps_to_Click() {
            Assert.That(EventAttributeMap.TryGet("on-click", out var kind), Is.True);
            Assert.That(kind, Is.EqualTo(EventKind.Click));
        }

        // ---------------------------------------------------------------------------
        // 11. All four pointer bindings wired together on one element — only the
        //     relevant handler fires for each event type.
        // ---------------------------------------------------------------------------

        [Test]
        public void All_four_pointer_bindings_coexist_on_one_element() {
            var (doc, el, _) = Build();
            var el2 = new Element("div");
            doc.AppendChild(el2);
            var ht = new BindingFakeHitTester();
            ht.Add(el, 0, 0, 100, 100);
            ht.Add(el2, 100, 100, 100, 100);
            var d = new EventDispatcher(doc, ht, new FakeUIClock());

            var c = new DragController();
            new EventBinding(el, EventKind.PointerDown,  M("OnPointerDown"),  c).Wire(d);
            new EventBinding(el, EventKind.PointerMove,  M("OnPointerMove"),  c).Wire(d);
            new EventBinding(el, EventKind.PointerUp,    M("OnPointerUp"),    c).Wire(d);
            new EventBinding(el, EventKind.PointerLeave, M("OnPointerLeave"), c).Wire(d);

            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);   // down fires
            d.DispatchPointerMove(50, 50, KeyModifiers.None);      // move fires (still on el)
            d.DispatchPointerMove(150, 150, KeyModifiers.None);    // leave fires
            d.DispatchPointerDown(50, 50, 0, KeyModifiers.None);   // enter + down fires again
            d.DispatchPointerUp(50, 50, 0, KeyModifiers.None);     // up fires

            Assert.That(c.DownCalls,  Is.EqualTo(2));
            Assert.That(c.MoveCalls,  Is.EqualTo(1));
            Assert.That(c.LeaveCalls, Is.EqualTo(1));
            Assert.That(c.UpCalls,    Is.EqualTo(1));
        }
    }
}
