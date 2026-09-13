// NativeInputFeed, driven through the Input System's own test fixture so
// the mapping decisions the feed makes -- not the core they land in -- are
// what is pinned. The 2026-09-13 audit found every one of them unpinned:
// NativeInputTests drives NativeDocument directly and never instantiates
// the feed, so the y-flip, the chord, the clock, the surface edge and the
// absence of touch had no test. Each case here is a rule the Godot host's
// input_integration_tests.gd pins on its side.
#if WEVA_INPUTSYSTEM
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class NativeInputFeedTests : InputTestFixture
    {
        private const int W = 400, H = 300;
        private NativeDocument _doc;
        private NativeInputFeed _feed;
        private Keyboard _keyboard;
        private Mouse _mouse;
        private Touchscreen _touch;
        private readonly List<NativeEvent> _events = new List<NativeEvent>();

        public override void Setup()
        {
            base.Setup();
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _touch = InputSystem.AddDevice<Touchscreen>();
        }

        public override void TearDown()
        {
            _feed?.Dispose();
            _doc?.Dispose();
            _feed = null;
            _doc = null;
            base.TearDown();
        }

        private void Load(string html, string css = "")
        {
            _doc = new NativeDocument(W, H);
            _doc.LoadHtml(html);
            _doc.SetCss("body{margin:0}" + css);
            _doc.Update(0);
            _feed = new NativeInputFeed(_doc) { HasFocus = () => true };
        }

        // The fixture's Press/Release/Set/BeginTouch each run an InputSystem
        // update already, so the edge they produced is "this frame" when the
        // feed reads it. A second update here would move past it -- which is
        // exactly what an earlier version of this helper did.
        private void Tick() => _feed.Tick(W, H);

        private string Value(string selector) => _doc.ElementValue(_doc.Query(selector));

        // The document's y grows downward from the top; the Input System's
        // mouse position grows upward from the bottom.
        private static Vector2 Screen(double docX, double docY) => new Vector2((float)docX, (float)(H - docY));

        private Vector2 Middle(string selector)
        {
            Assert.That(_doc.TryGetBounds(_doc.Query(selector), out NativeBounds b), Is.True, selector + " has bounds");
            return Screen(b.X + b.Width / 2, b.Y + b.Height / 2);
        }

        private int Clicks()
        {
            _events.Clear();
            _doc.PollEvents(_events);
            int n = 0;
            foreach (NativeEvent e in _events) if (e.Kind == weva_event_kind.WEVA_EVENT_CLICK) n++;
            return n;
        }

        [Test]
        public void HeldKeyRepeatsThroughTheCoreOnTheInputClock()
        {
            // The Input System reports the edge once; the core repeats it as the
            // input clock the host feeds advances. Unity used to delete one
            // character per press however long Backspace was held.
            Load("<input id=f value=abcdef>");
            _doc.SetFocus(_doc.Query("#f"));
            _doc.Key(weva_key.WEVA_KEY_END, true); _doc.Key(weva_key.WEVA_KEY_END, false);

            Press(_keyboard.backspaceKey);
            Tick();
            Assert.That(Value("#f"), Is.EqualTo("abcde"), "the edge deletes one");
            // The feed uses the browser's timing, 0.5 s then every 0.03 s: at
            // t = 0.55 the repeats at 0.50 and 0.53 have fired and 0.56 has not.
            _doc.Update(0, 0.55);
            Assert.That(Value("#f"), Is.EqualTo("abc"), "held, it keeps deleting");

            Release(_keyboard.backspaceKey);
            Tick();
            _doc.Update(0, 1.0);
            Assert.That(Value("#f"), Is.EqualTo("abc"), "released, it stops");
        }

        [Test]
        public void DoubleClickSelectsTheWordUnderIt()
        {
            Load("<input id=f value=\"hello world\">");
            Assert.That(_doc.TryGetBounds(_doc.Query("#f"), out NativeBounds b), Is.True);
            Vector2 at = Screen(b.X + b.Width * 0.9, b.Y + b.Height / 2);
            Set(_mouse.position, at);
            Press(_mouse.leftButton); Tick();
            Release(_mouse.leftButton); Tick();
            _doc.Update(0, 0.1);
            Press(_mouse.leftButton); Tick();
            Release(_mouse.leftButton); Tick();
            Assert.That(_doc.SelectedText(), Is.EqualTo("world"));
        }

        [Test]
        public void PointerLeavingTheSurfaceClearsHover()
        {
            Load("<div id=box></div>", "#box{width:100px;height:100px}");
            Set(_mouse.position, Middle("#box"));
            Tick();
            Assert.That(_doc.Query("#box:hover"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "hovered inside");

            Set(_mouse.position, new Vector2(-10, -10));
            Tick();
            Assert.That(_doc.Query("#box:hover"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE),
                "the pointer left the surface: nothing is hovered, as a browser drops :hover on pointerleave");
        }

        [Test]
        public void LosingApplicationFocusClearsHoverAndTakesNoKeys()
        {
            Load("<div id=box></div><input id=f value=abc>", "#box{width:100px;height:100px}");
            _doc.SetFocus(_doc.Query("#f"));
            _doc.Key(weva_key.WEVA_KEY_END, true); _doc.Key(weva_key.WEVA_KEY_END, false);
            Set(_mouse.position, Middle("#box"));
            Tick();
            Assert.That(_doc.Query("#box:hover"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE));

            _feed.HasFocus = () => false;
            Press(_keyboard.backspaceKey);
            Tick();
            Assert.That(_doc.Query("#box:hover"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "unfocused: hover cleared");
            Assert.That(Value("#f"), Is.EqualTo("abc"), "unfocused: keys do not reach the document");
            Release(_keyboard.backspaceKey);
            Tick();
        }

        [Test]
        public void AcceptsKeyboardGatesKeysAndTextButNotThePointer()
        {
            Load("<input id=f value=abc><div id=box></div>", "#box{width:100px;height:100px}");
            _doc.SetFocus(_doc.Query("#f"));
            _doc.Key(weva_key.WEVA_KEY_END, true); _doc.Key(weva_key.WEVA_KEY_END, false);
            _feed.AcceptsKeyboard = false;
            Press(_keyboard.backspaceKey);
            Set(_mouse.position, Middle("#box"));
            Tick();
            Assert.That(Value("#f"), Is.EqualTo("abc"), "a host that moved focus elsewhere: no keys");
            Assert.That(_doc.Query("#box:hover"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "...but the pointer still hovers");
            Release(_keyboard.backspaceKey);
            Tick();

            _feed.AcceptsKeyboard = true;
            Press(_keyboard.backspaceKey);
            Tick();
            Assert.That(Value("#f"), Is.EqualTo("ab"));
            Release(_keyboard.backspaceKey);
            Tick();
        }

        [Test]
        public void CommandChordFollowsThePlatform()
        {
            // Ctrl on Windows and Linux, Cmd on macOS. Unity used to read Ctrl
            // only, which made copy, paste, select-all and undo dead on a Mac.
            Load("<input id=f value=abc>");
            _doc.SetFocus(_doc.Query("#f"));

            _feed.CommandIsMeta = false;
            Press(_keyboard.leftCtrlKey); Press(_keyboard.aKey); Tick();
            Assert.That(_doc.SelectedText(), Is.EqualTo("abc"), "Ctrl+A selects all where Ctrl is the command key");
            Release(_keyboard.aKey); Release(_keyboard.leftCtrlKey); Tick();
            _doc.Key(weva_key.WEVA_KEY_END, true); _doc.Key(weva_key.WEVA_KEY_END, false);
            Assert.That(_doc.SelectedText(), Is.Empty);

            _feed.CommandIsMeta = true;
            Press(_keyboard.leftCtrlKey); Press(_keyboard.aKey); Tick();
            Assert.That(_doc.SelectedText(), Is.Empty, "on a Mac, Ctrl+A is not the chord");
            Release(_keyboard.aKey); Release(_keyboard.leftCtrlKey); Tick();

            Press(_keyboard.leftMetaKey); Press(_keyboard.aKey); Tick();
            Assert.That(_doc.SelectedText(), Is.EqualTo("abc"), "Cmd+A is");
            Release(_keyboard.aKey); Release(_keyboard.leftMetaKey); Tick();
        }

        [Test]
        public void TouchTapClicksAndTouchDragPans()
        {
            Load("<button id=go on-click=Go>Go</button><div id=list><div id=tall></div></div>",
                 "#list{height:100px;overflow:auto;width:200px} #tall{height:1000px}");
            Clicks();

            // A tap: down and up in place is a click on what is under it.
            Vector2 button = Middle("#go");
            BeginTouch(1, button); Tick();
            EndTouch(1, button); Tick();
            Assert.That(Clicks(), Is.EqualTo(1), "a tap is a click");

            // A drag: the content follows the finger, so moving up scrolls down.
            Vector2 start = Middle("#list");
            BeginTouch(1, start); Tick();
            MoveTouch(1, start + new Vector2(0, 60)); Tick();       // screen y up = document y up
            Assert.That(_feed.Consumed, Is.True, "the pan scrolled something");
            EndTouch(1, start + new Vector2(0, 60)); Tick();
            double scrolled = -1;
            uint list = _doc.Query("#list");
            foreach (weva_box box in _doc.Boxes())
                if (box.element == list && box.kind == (uint)weva_box_kind.WEVA_BOX_BLOCK) scrolled = box.scroll_y;
            Assert.That(scrolled, Is.GreaterThan(0), "dragging the finger up scrolls the list down");
            Assert.That(Clicks(), Is.EqualTo(0), "a drag is not a click");
        }

        private const string Column =
            "<button id=a>A</button><button id=b>B</button><button id=c>C</button>";
        private const string ColumnCss = "button{display:block;width:100px;height:40px;margin:0}";

        [Test]
        public void GamepadMovesFocusByGeometryAndRepeatsWhileHeld()
        {
            // The d-pad moves focus to the nearest control that way; held, it
            // steps again after the delay and then at the interval, like a held
            // arrow key. The Godot host's gamepad_navigation_tests pin the same.
            Load(Column, ColumnCss);
            Gamepad pad = InputSystem.AddDevice<Gamepad>();
            double now = 0;
            _feed.Clock = () => now;
            _doc.SetFocus(_doc.Query("#a"));

            Press(pad.dpad.down); Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#b")), "down moves to the button below");
            now = 0.2; Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#b")), "inside the delay, no repeat");
            now = 0.45; Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#c")), "past the delay, it steps again");
            now = 0.6; Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#c")), "at the edge it stays, rather than wrapping under the thumb");
            Release(pad.dpad.down); Tick();

            Press(pad.dpad.up); Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#b")));
            Release(pad.dpad.up); Tick();
        }

        [Test]
        public void GamepadSouthActivatesAndShouldersStepTheTabOrder()
        {
            Load("<button id=go on-click=Go>Go</button>" + Column, ColumnCss);
            Gamepad pad = InputSystem.AddDevice<Gamepad>();
            _doc.SetFocus(_doc.Query("#go"));
            Clicks();
            Press(pad.buttonSouth); Tick();
            Release(pad.buttonSouth); Tick();
            Assert.That(Clicks(), Is.EqualTo(1), "South activates the focused button");

            Press(pad.rightShoulder); Tick(); Release(pad.rightShoulder); Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#a")), "the right shoulder steps forward in tab order");
            Press(pad.leftShoulder); Tick(); Release(pad.leftShoulder); Tick();
            Assert.That(_doc.Focus, Is.EqualTo(_doc.Query("#go")), "the left shoulder steps back");
        }

        [Test]
        public void GamepadAcceptOnATextFieldAsksTheHostForAKeyboard()
        {
            Load("<input id=name>");
            Gamepad pad = InputSystem.AddDevice<Gamepad>();
            _doc.SetFocus(_doc.Query("#name"));
            string requested = null;
            _feed.GamepadTextEntry = true;
            _feed.TextEntryRequested += id => requested = id;
            Press(pad.buttonSouth); Tick(); Release(pad.buttonSouth); Tick();
            Assert.That(requested, Is.EqualTo("name"), "a pad cannot type; the host supplies the keyboard");
        }

        [Test]
        public void ImeCompositionShowsThenCommitsWithoutTypingTwice()
        {
            // The OS composes, then delivers the committed text as key text
            // while the composition string empties. That text is the commit
            // and must not also arrive as typing.
            Load("<input id=f>");
            _doc.SetFocus(_doc.Query("#f"));
            Tick();                                              // enables the IME over the field

            var composing = UnityEngine.InputSystem.LowLevel.IMECompositionEvent.Create(
                _keyboard.deviceId, "か", UnityEngine.InputSystem.LowLevel.InputState.currentTime);
            InputSystem.QueueEvent(ref composing);
            InputSystem.Update(); Tick();

            InputSystem.QueueTextEvent(_keyboard, 'か');
            var done = UnityEngine.InputSystem.LowLevel.IMECompositionEvent.Create(
                _keyboard.deviceId, "", UnityEngine.InputSystem.LowLevel.InputState.currentTime);
            InputSystem.QueueEvent(ref done);
            InputSystem.Update(); Tick();
            Assert.That(Value("#f"), Is.EqualTo("か"), "committed once, not composed and then typed");

            InputSystem.QueueTextEvent(_keyboard, 'x');
            InputSystem.Update(); Tick();
            Assert.That(Value("#f"), Is.EqualTo("かx"), "ordinary typing resumes after the composition");
        }
    }
}
#endif
