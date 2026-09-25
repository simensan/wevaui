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
using UnityEngine.InputSystem.LowLevel;
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

        [Test]
        public void AstralTextCrossesTheInputSystemAsWholeCharacters()
        {
            Load("<input id=f>");
            _doc.SetFocus(_doc.Query("#f"));
            Tick();
            Clicks(); // drain focus events
            foreach (int scalar in new[] { (int)'A', 0x1F41F, 0x10400, (int)'B' })
            {
                var ev = UnityEngine.InputSystem.LowLevel.TextEvent.Create(_keyboard.deviceId, scalar);
                InputSystem.QueueEvent(ref ev);
            }
            InputSystem.Update(); Tick();
            Assert.That(Value("#f"), Is.EqualTo("A\U0001F41F\U00010400B"));
            _events.Clear();
            _doc.PollEvents(_events);
            Assert.That(_events.FindAll(e => e.Kind == weva_event_kind.WEVA_EVENT_TEXT_INPUT).Count,
                Is.EqualTo(4), "one input event per Unicode character");
        }

        private void QueueHandoff(bool gamepad)
        {
            if (gamepad)
            {
                var pad = InputSystem.AddDevice<Gamepad>();
                InputSystem.QueueStateEvent(pad, new UnityEngine.InputSystem.LowLevel.GamepadState()
                    .WithButton(GamepadButton.South).WithButton(GamepadButton.RightShoulder));
            }
            else InputSystem.QueueStateEvent(_keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.Tab));
            InputSystem.QueueTextEvent(_keyboard, 'x');
            InputSystem.Update();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InputHandoffCanDisposeTheDocument(bool gamepad)
        {
            Load("<input id=f>");
            _doc.SetFocus(_doc.Query("#f"));
            _feed.WrapTab = false;
            _feed.GamepadTextEntry = true;
            int calls = 0;
            void DisposeDocument() { calls++; _feed.Dispose(); _doc.Dispose(); }
            _feed.TabbedOut += _ => DisposeDocument();
            _feed.TextEntryRequested += _ => DisposeDocument();
            Tick();
            QueueHandoff(gamepad);
            Assert.DoesNotThrow(Tick);
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InputHandoffDoesNotTypeIntoAReplacementDocument(bool gamepad)
        {
            Load("<input id=f>");
            _doc.SetFocus(_doc.Query("#f"));
            _feed.WrapTab = false;
            _feed.GamepadTextEntry = true;
            void Reload()
            {
                _doc.LoadHtml("<input id=new value=untouched>");
                _doc.Update(0);
                _doc.SetFocus(_doc.Query("#new"));
            }
            _feed.TabbedOut += _ => Reload();
            _feed.TextEntryRequested += _ => Reload();
            Tick();
            QueueHandoff(gamepad);
            Tick();
            Assert.That(Value("#new"), Is.EqualTo("untouched"));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void HostStopsTheFrameAfterAHandoffChangesTheDocument(bool gamepad, bool reload)
        {
            var go = new GameObject("input-handoff-host");
            go.SetActive(false);
            var host = go.AddComponent<WevaDocument>();
            host.AutoInput = false;
            host.SystemFontFallback = false;
            host.InlineHtml = "<input id=f>";
            host.WrapTab = false;
            host.GamepadTextEntry = true;
            try
            {
                go.SetActive(true);
                _doc = host.Document;
                Assert.That(_doc, Is.Not.Null, host.LastError);
                _doc.SetFocus(_doc.Query("#f"));
                _feed = host.Input;
                _feed.HasFocus = () => true;
                void Handoff()
                {
                    if (!reload) go.SetActive(false);
                    else
                    {
                        host.InlineHtml = "<input id=new value=untouched>";
                        host.Reload();
                        host.Document.SetFocus(host.Document.Query("#new"));
                    }
                }
                host.TabbedOut += _ => Handoff();
                host.TextEntryRequested += _ => Handoff();
                Assert.That(host.TickInput(), Is.True);
                QueueHandoff(gamepad);
                Assert.That(host.TickInput(), Is.False, "Update must stop after the callback changed its document");
                Assert.That(host.InputConsumed, Is.EqualTo(gamepad));
                if (reload) Assert.That(Value("#new"), Is.EqualTo("untouched"));
                else Assert.That(host.Document, Is.Null);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InputHandoffStillReleasesKeysFromThePreviousFrame(bool gamepad)
        {
            Load("<input id=f value=abcdef>");
            _doc.SetFocus(_doc.Query("#f"));
            _doc.Key(weva_key.WEVA_KEY_END, true); _doc.Key(weva_key.WEVA_KEY_END, false);
            _feed.WrapTab = false;
            _feed.GamepadTextEntry = true;
            Tick();
            Press(_keyboard.backspaceKey); Tick();
            Assert.That(Value("#f"), Is.EqualTo("abcde"));
            // Backspace is released in the same input update as the handoff.
            if (gamepad) InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            QueueHandoff(gamepad);
            Tick();
            _doc.SetFocus(_doc.Query("#f"));
            _doc.Update(0, 0.55);
            Assert.That(Value("#f"), Is.EqualTo("abcde"), "a released key must not keep repeating after a handoff");
        }

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

        // Two documents on one screen: the pointer belongs to the one painted
        // on top where it accepts the pointer. A document accepts the pointer
        // everywhere, as in a browser, transparent or not -- a HUD that lets
        // clicks through its empty areas says `html, body { pointer-events:
        // none }` and `pointer-events: auto` on its controls (the Godot host's
        // rule too). Both a mouse and a finger.
        [Test]
        public void OnlyTheTopDocumentTakesThePointer_WhereItAcceptsIt()
        {
            Load("<button id=low on-click=Low>Low</button><button id=shared on-click=Shared>Shared</button>",
                 "#low{position:absolute;left:0;top:0;width:100px;height:50px}#shared{position:absolute;left:150px;top:0;width:100px;height:50px}");
            using (var upperDoc = new NativeDocument(W, H))
            using (var upper = new NativeInputFeed(upperDoc) { HasFocus = () => true, Order = () => 1 })
            {
                upperDoc.LoadHtml("<button id=up on-click=Up>Up</button>");
                upperDoc.SetCss("html,body{margin:0;pointer-events:none}#up{pointer-events:auto;position:absolute;left:150px;top:0;width:100px;height:50px}");
                upperDoc.Update(0);
                var upperEvents = new List<NativeEvent>();
                int UpperClicks() { upperEvents.Clear(); upperDoc.PollEvents(upperEvents); int n = 0; foreach (NativeEvent e in upperEvents) if (e.Kind == weva_event_kind.WEVA_EVENT_CLICK) n++; return n; }
                Clicks(); UpperClicks();

                // Over the lower document's own button (the upper one is transparent there).
                Set(_mouse.position, Middle("#low"));
                Press(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Release(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Assert.That(Clicks(), Is.EqualTo(1), "where the upper document passes the pointer (pointer-events: none), the lower one is clicked");
                Assert.That(UpperClicks(), Is.EqualTo(0));

                // Over the overlap: the upper document's button covers the lower one's.
                Set(_mouse.position, Middle("#shared"));
                Tick(); upper.Tick(W, H);
                Assert.That(_doc.Query("#shared:hover"), Is.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "the covered button is not hovered");
                Assert.That(upperDoc.Query("#up:hover"), Is.Not.EqualTo(WevaNative.WEVA_ELEMENT_NONE), "the covering one is");
                Press(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Assert.That(_feed.Consumed, Is.False, "the lower document did not take the press");
                Assert.That(upper.Consumed, Is.True, "the upper one did");
                Release(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Assert.That(Clicks(), Is.EqualTo(0), "the covered button is not clicked");
                Assert.That(UpperClicks(), Is.EqualTo(1), "the covering one is");

                // A finger follows the same rule.
                Vector2 shared = Middle("#shared");
                BeginTouch(1, shared); Tick(); upper.Tick(W, H);
                EndTouch(1, shared); Tick(); upper.Tick(W, H);
                Assert.That(Clicks(), Is.EqualTo(0), "a tap on the overlap misses the covered button");
                Assert.That(UpperClicks(), Is.EqualTo(1), "and hits the covering one");
                Vector2 low = Middle("#low");
                BeginTouch(1, low); Tick(); upper.Tick(W, H);
                EndTouch(1, low); Tick(); upper.Tick(W, H);
                Assert.That(Clicks(), Is.EqualTo(1), "a tap where the upper document passes the pointer reaches the lower one");
                Assert.That(UpperClicks(), Is.EqualTo(0));

                // Equal orders: the later-created document paints on top.
                upper.Order = () => 0;
                Set(_mouse.position, shared);
                Press(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Release(_mouse.leftButton); Tick(); upper.Tick(W, H);
                Assert.That(Clicks(), Is.EqualTo(0), "equal orders: the later document is on top");
                Assert.That(UpperClicks(), Is.EqualTo(1));
            }
        }

        [Test]
        public void WheelNotchScrollsWhatChromeScrolls()
        {
            // Chrome on Windows scrolls 100 CSS px for one wheel notch. Both
            // hosts scrolled 40 -- a shared deviation from the reference --
            // until 2026-09-13; this and input_integration_tests.gd pin 100.
            Load("<div id=list><div id=tall></div></div>", "#list{height:100px;overflow:auto;width:200px} #tall{height:1000px}");
            Set(_mouse.position, Middle("#list"));
            Tick();
            Set(_mouse.scroll, new Vector2(0, -1));                 // one notch, down
            Assert.That(_mouse.scroll.ReadValue(), Is.EqualTo(new Vector2(0, -1)), "the fixture delivered the notch");
            Tick();
            Assert.That(_feed.Consumed, Is.True, "the feed scrolled something");
            _doc.Update(0);                                         // Boxes() reports the last published layout
            double scrolled = -1;
            uint list = _doc.Query("#list");
            foreach (weva_box box in _doc.Boxes())
                if (box.element == list && box.kind == (uint)weva_box_kind.WEVA_BOX_BLOCK) scrolled = box.scroll_y;
            Assert.That(scrolled, Is.EqualTo(100).Within(0.01), "one notch is Chrome's 100px, not 40");
            Set(_mouse.scroll, Vector2.zero);
            Tick();
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
