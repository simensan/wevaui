using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Events;
using Weva.Forms.Bridge;
using Weva.Parsing;

namespace Weva.Tests.Forms.Bridge {
    // A scripted IUIPointerSource that mirrors UnityPointerSource's contract:
    //   - Tick reads current state, compares to last, dispatches events on
    //     edges only.
    //   - Y-flip: callers set raw screen coordinates (bottom-left origin)
    //     and a screen height; the Tick converts to CSS top-left before
    //     calling the dispatcher.
    //   - Idempotent: a Tick with no state change emits nothing.
    sealed class FakePointerSource : IUIPointerSource {
        public float ScreenHeight = 600f;
        public float ScreenWidth = 800f;
        public UnityEngine.Vector2 Position;
        public bool LeftDown;
        public bool RightDown;
        public bool MiddleDown;
        public KeyModifiers Modifiers = KeyModifiers.None;

        UnityEngine.Vector2 lastPosition;
        bool hasLast;
        bool lastLeft, lastRight, lastMiddle;
        bool lastOnScreen;

        public void Tick(EventDispatcher dispatcher, double now) {
            if (dispatcher == null) return;

            double cssX = Position.x;
            double cssY = ScreenHeight - Position.y;
            bool onScreen = Position.x >= 0f && Position.x <= ScreenWidth
                            && Position.y >= 0f && Position.y <= ScreenHeight;

            if (!hasLast) {
                lastPosition = Position;
                hasLast = true;
                lastOnScreen = onScreen;
                if (onScreen) dispatcher.DispatchPointerMove(cssX, cssY, Modifiers);
            } else if (Position != lastPosition) {
                if (onScreen) dispatcher.DispatchPointerMove(cssX, cssY, Modifiers);
                else if (lastOnScreen) dispatcher.DispatchPointerMove(-1, -1, Modifiers);
                lastPosition = Position;
                lastOnScreen = onScreen;
            }

            HandleButton(dispatcher, ref lastLeft, LeftDown, 0, cssX, cssY);
            HandleButton(dispatcher, ref lastRight, RightDown, 2, cssX, cssY);
            HandleButton(dispatcher, ref lastMiddle, MiddleDown, 1, cssX, cssY);
        }

        void HandleButton(EventDispatcher dispatcher, ref bool last, bool current, int button, double x, double y) {
            if (current == last) return;
            if (current) dispatcher.DispatchPointerDown(x, y, button, Modifiers);
            else dispatcher.DispatchPointerUp(x, y, button, Modifiers);
            last = current;
        }
    }

    sealed class RecordingHit : IHitTester {
        readonly Dictionary<(double, double), Element> map = new();
        public Element Default;
        public void Add(double x, double y, Element e) => map[(x, y)] = e;
        public Element HitTest(double x, double y) {
            // Treat negative coords as off-screen miss so the off-screen
            // leave path resolves correctly.
            if (x < 0 || y < 0) return null;
            return map.TryGetValue((x, y), out var e) ? e : Default;
        }
    }

    public class UnityPointerSourceTests {
        sealed class Logged {
            public EventKind Kind;
            public double X;
            public double Y;
            public int Button;
            public KeyModifiers Mods;
            public Logged(EventKind kind, double x, double y, int button, KeyModifiers mods) {
                Kind = kind; X = x; Y = y; Button = button; Mods = mods;
            }
        }

        static (Document doc, Element a, Element b, EventDispatcher d, List<Logged> log) Build() {
            var doc = HtmlParser.Parse("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new RecordingHit { Default = a };
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            var log = new List<Logged>();
            d.AddEventListener(a, EventKind.PointerMove, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerMove, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(a, EventKind.PointerDown, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerDown, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(a, EventKind.PointerUp, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerUp, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(a, EventKind.Click, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.Click, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(b, EventKind.PointerMove, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerMove, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(b, EventKind.PointerDown, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerDown, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(b, EventKind.PointerUp, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.PointerUp, p.X, p.Y, p.Button, ReadMods(p)));
            });
            d.AddEventListener(b, EventKind.Click, e => {
                var p = (PointerEvent)e;
                log.Add(new Logged(EventKind.Click, p.X, p.Y, p.Button, ReadMods(p)));
            });
            return (doc, a, b, d, log);
        }

        static KeyModifiers ReadMods(PointerEvent p) {
            var m = KeyModifiers.None;
            if (p.ShiftKey) m |= KeyModifiers.Shift;
            if (p.CtrlKey) m |= KeyModifiers.Ctrl;
            if (p.AltKey) m |= KeyModifiers.Alt;
            if (p.MetaKey) m |= KeyModifiers.Meta;
            return m;
        }

        [Test]
        public void Stationary_pointer_emits_no_events_after_first_tick() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(10, 20) };
            src.Tick(d, 0);
            int after = log.Count;
            src.Tick(d, 0.016);
            src.Tick(d, 0.032);
            Assert.That(log.Count, Is.EqualTo(after));
        }

        [Test]
        public void Moving_pointer_dispatches_pointer_move_with_y_flipped_coords() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { ScreenHeight = 600f };
            src.Position = new UnityEngine.Vector2(100, 500);
            src.Tick(d, 0);
            // CSS y = 600 - 500 = 100.
            var first = log.Find(e => e.Kind == EventKind.PointerMove);
            Assert.That(first, Is.Not.Null);
            Assert.That(first.X, Is.EqualTo(100));
            Assert.That(first.Y, Is.EqualTo(100));

            src.Position = new UnityEngine.Vector2(200, 50);
            src.Tick(d, 0.016);
            var second = log.FindAll(e => e.Kind == EventKind.PointerMove)[1];
            Assert.That(second.X, Is.EqualTo(200));
            Assert.That(second.Y, Is.EqualTo(550));
        }

        [Test]
        public void Press_dispatches_pointer_down_with_button_zero() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(10, 10) };
            src.Tick(d, 0);
            src.LeftDown = true;
            src.Tick(d, 0.016);
            var down = log.Find(e => e.Kind == EventKind.PointerDown);
            Assert.That(down, Is.Not.Null);
            Assert.That(down.Button, Is.EqualTo(0));
        }

        [Test]
        public void Release_dispatches_pointer_up() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(10, 10) };
            src.Tick(d, 0);
            src.LeftDown = true;
            src.Tick(d, 0.016);
            src.LeftDown = false;
            src.Tick(d, 0.032);
            var up = log.Find(e => e.Kind == EventKind.PointerUp);
            Assert.That(up, Is.Not.Null);
            Assert.That(up.Button, Is.EqualTo(0));
        }

        [Test]
        public void Down_then_up_on_same_element_synthesizes_click() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(50, 50) };
            src.Tick(d, 0);
            src.LeftDown = true;
            src.Tick(d, 0.016);
            src.LeftDown = false;
            src.Tick(d, 0.032);
            Assert.That(log.Exists(e => e.Kind == EventKind.Click), Is.True);
        }

        [Test]
        public void Down_then_up_on_different_element_does_not_click() {
            var doc = HtmlParser.Parse("<div id=\"a\"></div><div id=\"b\"></div>");
            var a = doc.GetElementById("a");
            var b = doc.GetElementById("b");
            var ht = new RecordingHit { Default = a };
            var d = new EventDispatcher(doc, ht, new FakeUIClock());
            int clicksA = 0, clicksB = 0;
            d.AddEventListener(a, EventKind.Click, _ => clicksA++);
            d.AddEventListener(b, EventKind.Click, _ => clicksB++);

            var src = new FakePointerSource { ScreenHeight = 600f };
            // Press while hit-tester returns a.
            src.Position = new UnityEngine.Vector2(10, 590);
            src.Tick(d, 0);
            src.LeftDown = true;
            src.Tick(d, 0.016);
            // Switch hit target before release.
            ht.Default = b;
            src.Position = new UnityEngine.Vector2(20, 580);
            src.LeftDown = false;
            src.Tick(d, 0.032);
            Assert.That(clicksA, Is.Zero);
            Assert.That(clicksB, Is.Zero);
        }

        [Test]
        public void Modifier_keys_propagate_into_dispatched_events() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource {
                Position = new UnityEngine.Vector2(10, 10),
                Modifiers = KeyModifiers.Shift | KeyModifiers.Ctrl
            };
            src.Tick(d, 0);
            src.LeftDown = true;
            src.Tick(d, 0.016);
            var down = log.Find(e => e.Kind == EventKind.PointerDown);
            Assert.That(down, Is.Not.Null);
            Assert.That((down.Mods & KeyModifiers.Shift) != 0, Is.True);
            Assert.That((down.Mods & KeyModifiers.Ctrl) != 0, Is.True);
        }

        [Test]
        public void Tick_with_null_dispatcher_is_noop() {
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(10, 10), LeftDown = true };
            Assert.DoesNotThrow(() => src.Tick(null, 0));
        }

        [Test]
        public void Right_and_middle_buttons_dispatch_with_correct_button_codes() {
            var (_, _, _, d, log) = Build();
            var src = new FakePointerSource { Position = new UnityEngine.Vector2(10, 10) };
            src.Tick(d, 0);
            src.RightDown = true;
            src.Tick(d, 0.016);
            src.RightDown = false;
            src.MiddleDown = true;
            src.Tick(d, 0.032);
            var rightDown = log.Find(e => e.Kind == EventKind.PointerDown && e.Button == 2);
            var middleDown = log.Find(e => e.Kind == EventKind.PointerDown && e.Button == 1);
            Assert.That(rightDown, Is.Not.Null);
            Assert.That(middleDown, Is.Not.Null);
        }

        [Test]
        public void Pointer_leaving_screen_after_being_on_screen_routes_offscreen_move() {
            var (_, a, _, d, _) = Build();
            int leaves = 0;
            d.AddEventListener(a, EventKind.PointerLeave, _ => leaves++);

            var src = new FakePointerSource { ScreenHeight = 600f, ScreenWidth = 800f };
            src.Position = new UnityEngine.Vector2(100, 100);
            src.Tick(d, 0);
            // Hover should now be set to a.
            Assert.That(d.HoveredElement, Is.EqualTo(a));
            // Move off-screen (negative).
            src.Position = new UnityEngine.Vector2(-50, 100);
            src.Tick(d, 0.016);
            Assert.That(d.HoveredElement, Is.Null);
            Assert.That(leaves, Is.GreaterThan(0));
        }
    }
}
